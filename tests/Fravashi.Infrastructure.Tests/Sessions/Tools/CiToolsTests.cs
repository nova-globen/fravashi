using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Ci;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Fravashi.Infrastructure.Tests.Git;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Testing.Network;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions.Tools;

/// <summary>
/// Backlog 153: an agent reads the CI runs and logs of its workspace's repository from the git host, in each host's
/// own dialect, with the token that host's Settings entry holds and never with one of its own. Over a real local
/// repository (the origin URL is what decides the provider) and a recorded HTTP conversation per provider, because
/// the shapes below are the ones the hosts really answer with (Forgejo's measured on 2026-09-26 against the forge).
/// </summary>
public sealed class CiToolsTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "fravashi-ci-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SqliteTestContext _store = new();
    private readonly AgentSessionId _session = AgentSessionId.New();
    private readonly FakeHandler _http = new();
    private readonly IGitHostStore _hosts = Substitute.For<IGitHostStore>();
    private readonly ISecretStore _secrets = Substitute.For<ISecretStore>();
    private readonly ForgeRuns _forge;

    public CiToolsTests()
    {
        var access = new GitHostAccess(
            _hosts, _secrets, Substitute.For<IProviderConnectionStore>(), Substitute.For<IConfigurationStore>());
        _forge = new ForgeRuns(new HttpClient(_http), () => access);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            if (Directory.Exists(_base))
            {
                foreach (var file in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_base, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---- Forgejo -------------------------------------------------------------------------------

    private const string ForgejoTasks = """
        {"workflow_runs":[
          {"id":1076,"name":"bundle","head_branch":"v0.9.36","head_sha":"450faabf1426409ad8394124ade3877566603652","run_number":231,"event":"push","display_title":"fix(sync): a description","status":"success","workflow_id":"release.yml","url":"https://forge.example.test/nova-globen/fravashi/actions/runs/231","created_at":"2026-09-24T13:19:44Z","updated_at":"2026-09-24T13:40:11Z","run_started_at":"2026-09-24T13:19:44Z"},
          {"id":1075,"name":"test","head_branch":"v0.9.36","head_sha":"450faabf1426409ad8394124ade3877566603652","run_number":231,"event":"push","display_title":"fix(sync): a description","status":"success","workflow_id":"release.yml","url":"https://forge.example.test/nova-globen/fravashi/actions/runs/231","created_at":"2026-09-24T13:19:40Z","updated_at":"2026-09-24T13:30:00Z","run_started_at":"2026-09-24T13:19:40Z"},
          {"id":1070,"name":"build-test","head_branch":"main","head_sha":"9674ee8aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","run_number":230,"event":"push","display_title":"docs(backlog): the project","status":"failure","workflow_id":"ci.yml","url":"https://forge.example.test/nova-globen/fravashi/actions/runs/230","created_at":"2026-09-24T12:00:00Z","updated_at":"2026-09-24T12:20:00Z","run_started_at":"2026-09-24T12:00:00Z"}
        ],"total_count":3}
        """;

    [Fact]
    public async Task On_forgejo_the_runs_are_the_tasks_newest_first_with_the_stored_token_and_a_sha_filter()
    {
        var git = Repo("https://forge.example.test/nova-globen/fravashi.git");
        Host("forge.example.test", OnboardingProviders.Forgejo, "forge-token");
        _http.Routes["https://forge.example.test/api/v1/repos/nova-globen/fravashi/actions/tasks"] = (HttpStatusCode.OK, ForgejoTasks);

        var answer = Data(await Call(new CiRunsTool(git, _forge), """{"max": 2}"""));

        answer.GetProperty("provider").GetString().Should().Be("Forgejo");
        answer.GetProperty("repository").GetString().Should().Be("nova-globen/fravashi on forge.example.test");
        var runs = answer.GetProperty("runs").EnumerateArray().ToList();
        runs.Should().HaveCount(2);
        runs[0].GetProperty("id").GetString().Should().Be("231");
        runs[0].GetProperty("job").GetString().Should().Be("bundle");
        runs[0].GetProperty("workflow").GetString().Should().Be("release.yml");
        runs[0].GetProperty("status").GetString().Should().Be("success");
        _http.Sent.Should().ContainSingle().Which.Authorization.Should().Be("token forge-token");

        var filtered = Data(await Call(new CiRunsTool(git, _forge), """{"sha": "9674ee8"}"""));
        filtered.GetProperty("count").GetInt32().Should().Be(1);
        filtered.GetProperty("runs")[0].GetProperty("status").GetString().Should().Be("failure");

        // A reference is resolved against the host repository before it is matched.
        var head = Data(await Call(new CiRunsTool(git, _forge), """{"sha": "HEAD"}"""));
        head.GetProperty("sha").GetString().Should().HaveLength(40);
        head.GetProperty("count").GetInt32().Should().Be(0, "the forge never ran this temp repository's commit");
    }

    [Fact]
    public async Task On_forgejo_a_log_comes_from_the_web_page_by_job_position_and_a_missing_one_is_said()
    {
        var git = Repo("https://forge.example.test/nova-globen/fravashi.git");
        Host("forge.example.test", OnboardingProviders.Forgejo, null);
        _http.Routes["https://forge.example.test/api/v1/repos/nova-globen/fravashi/actions/tasks"] = (HttpStatusCode.OK, ForgejoTasks);
        _http.Routes["https://forge.example.test/nova-globen/fravashi/actions/runs/231/jobs/1/attempt/1/logs"] =
            (HttpStatusCode.OK, "Preflight ok\nBuilding...\nerror: it broke\n");

        // "bundle" is the SECOND task of run 231 (tasks are numbered oldest first), so job 1.
        var log = Data(await Call(new CiRunLogTool(git, _forge), """{"run": "231", "job": "bundle"}"""));
        log.GetProperty("text").GetString().Should().Contain("it broke");
        log.GetProperty("source").GetString().Should().EndWith("/runs/231/jobs/1/attempt/1/logs");
        log.GetProperty("truncated").GetBoolean().Should().BeFalse();

        var missing = await Call(new CiRunLogTool(git, _forge), """{"run": "231", "job": "9"}""");
        missing.IsError.Should().BeTrue();
        missing.Text.Should().Contain("no_log");
    }

    // ---- GitHub --------------------------------------------------------------------------------

    [Fact]
    public async Task On_github_runs_are_listed_and_the_failed_job_of_a_run_is_the_log_by_default()
    {
        var git = Repo("https://github.com/acme/app.git");
        Host("github.com", OnboardingProviders.GitHub, "gh-token");
        _http.Routes["https://api.github.com/repos/acme/app/actions/runs?per_page=10"] = (HttpStatusCode.OK, """
            {"workflow_runs":[{"id":42,"run_number":7,"name":"ci","status":"completed","conclusion":"failure","head_sha":"abcdef1234567890abcdef1234567890abcdef12","head_branch":"main","display_title":"feat: x","html_url":"https://github.com/acme/app/actions/runs/42","run_started_at":"2026-09-26T10:00:00Z","updated_at":"2026-09-26T10:05:00Z"}]}
            """);
        _http.Routes["https://api.github.com/repos/acme/app/actions/runs/42/jobs"] = (HttpStatusCode.OK, """
            {"jobs":[{"id":6,"name":"lint","conclusion":"success"},{"id":7,"name":"test","conclusion":"failure"}]}
            """);
        _http.Routes["https://api.github.com/repos/acme/app/actions/jobs/7/logs"] = (HttpStatusCode.OK, "FAIL tests/x.cs\n");

        var runs = Data(await Call(new CiRunsTool(git, _forge), "{}"));
        runs.GetProperty("provider").GetString().Should().Be("GitHub");
        runs.GetProperty("runs")[0].GetProperty("id").GetString().Should().Be("42");
        runs.GetProperty("runs")[0].GetProperty("status").GetString().Should().Be("failure");
        _http.Sent.Should().ContainSingle().Which.Authorization.Should().Be("Bearer gh-token");

        var log = Data(await Call(new CiRunLogTool(git, _forge), """{"run": "42"}"""));
        log.GetProperty("text").GetString().Should().Contain("FAIL tests/x.cs");
        log.GetProperty("source").GetString().Should().Contain("test");
    }

    // ---- Azure DevOps ----------------------------------------------------------------------------

    [Fact]
    public async Task On_azure_devops_builds_are_listed_and_the_last_log_of_a_build_is_the_default()
    {
        var git = Repo("https://dev.azure.com/org/proj/_git/repo");
        _http.Routes["https://dev.azure.com/org/proj/_apis/build/builds?api-version=7.1&$top=10"] = (HttpStatusCode.OK, """
            {"value":[{"id":501,"buildNumber":"20260926.3","status":"completed","result":"succeeded","sourceVersion":"0123456789abcdef0123456789abcdef01234567","sourceBranch":"refs/heads/main","definition":{"name":"repo-ci"},"_links":{"web":{"href":"https://dev.azure.com/org/proj/_build/results?buildId=501"}},"startTime":"2026-09-26T09:00:00Z","finishTime":"2026-09-26T09:10:00Z"}]}
            """);
        _http.Routes["https://dev.azure.com/org/proj/_apis/build/builds/501/logs?api-version=7.1"] =
            (HttpStatusCode.OK, """{"value":[{"id":1,"lineCount":3},{"id":5,"lineCount":40}]}""");
        _http.Routes["https://dev.azure.com/org/proj/_apis/build/builds/501/logs/5?api-version=7.1"] = (HttpStatusCode.OK, "Finishing: Job\n");

        var runs = Data(await Call(new CiRunsTool(git, _forge), "{}"));
        runs.GetProperty("provider").GetString().Should().Be("Azure DevOps");
        runs.GetProperty("repository").GetString().Should().Be("org/proj/repo on dev.azure.com");
        var run = runs.GetProperty("runs")[0];
        run.GetProperty("id").GetString().Should().Be("501");
        run.GetProperty("workflow").GetString().Should().Be("repo-ci");
        run.GetProperty("status").GetString().Should().Be("succeeded");
        run.GetProperty("title").GetString().Should().Be("20260926.3");

        var log = Data(await Call(new CiRunLogTool(git, _forge), """{"run": "501"}"""));
        log.GetProperty("text").GetString().Should().Be("Finishing: Job\n");
        log.GetProperty("source").GetString().Should().Be("log 5 of build 501");
    }

    // ---- what is refused, and the cap -----------------------------------------------------------

    [Fact]
    public async Task A_repository_with_no_origin_or_an_unknown_host_is_told_so()
    {
        var bare = Repo(null);
        (await Call(new CiRunsTool(bare, _forge), "{}")).Text.Should().Contain("no_remote");

        var stranger = Repo("https://example.com/a/b.git");
        var answer = await Call(new CiRunsTool(stranger, _forge), "{}");
        answer.IsError.Should().BeTrue();
        answer.Text.Should().Contain("no_provider").And.Contain("Git hosts");
    }

    [Fact]
    public async Task A_long_log_is_cut_from_the_end_by_default_and_from_the_start_on_request()
    {
        var git = Repo("https://github.com/acme/app.git");
        Host("github.com", OnboardingProviders.GitHub, null);
        _http.Routes["https://api.github.com/repos/acme/app/actions/runs/42/jobs"] =
            (HttpStatusCode.OK, """{"jobs":[{"id":7,"name":"test","conclusion":"failure"}]}""");
        _http.Routes["https://api.github.com/repos/acme/app/actions/jobs/7/logs"] =
            (HttpStatusCode.OK, "START " + new string('x', 60_000) + " END");

        var tail = Data(await Call(new CiRunLogTool(git, _forge), """{"run": "42"}"""));
        tail.GetProperty("truncated").GetBoolean().Should().BeTrue();
        tail.GetProperty("text").GetString().Should().HaveLength(GitToolBase.TextCapForTests).And.EndWith(" END");

        var head = Data(await Call(new CiRunLogTool(git, _forge), """{"run": "42", "tail": false}"""));
        head.GetProperty("text").GetString().Should().StartWith("START ");
    }

    [Fact]
    public async Task A_provider_failure_names_the_provider_and_never_the_token()
    {
        var git = Repo("https://github.com/acme/app.git");
        Host("github.com", OnboardingProviders.GitHub, "gh-secret-token");
        _http.Routes["https://api.github.com/repos/acme/app/actions/runs?per_page=10"] = (HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");

        var answer = await Call(new CiRunsTool(git, _forge), "{}");

        answer.IsError.Should().BeTrue();
        answer.Text.Should().Contain("provider_failed").And.Contain("GitHub").And.NotContain("gh-secret-token");
    }

    // ---- the network, not the forge (backlog 233) ------------------------------------------------

    [Theory]
    [InlineData(NetworkBehaviour.NameFails, "name_not_resolved", "name not resolved")]
    [InlineData(NetworkBehaviour.Refuses, "refused", "connection refused")]
    [InlineData(NetworkBehaviour.NoRoute, "unreachable", "no network route")]
    [InlineData(NetworkBehaviour.Hangs, "left_with_no_answer", "no answer came back")]
    public async Task A_network_failure_is_said_as_the_network_with_its_kind_and_is_safe_to_repeat(
        NetworkBehaviour behaviour, string kind, string words)
    {
        var git = Repo("https://forge.example.test/nova-globen/fravashi.git");
        Host("forge.example.test", OnboardingProviders.Forgejo, "forge-token");
        var forge = Scripted(new NetworkScript(new FakeNetworkClock(), new NetworkPhase(behaviour)));

        foreach (var (tool, arguments, field) in new (IStudioTool, string, string)[]
        {
            (new CiRunsTool(git, forge), "{}", "origin"),
            (new CiRunLogTool(git, forge), """{"run": "231"}""", "run"),
        })
        {
            var answer = await Call(tool, arguments);

            answer.IsError.Should().BeTrue();
            answer.Text.Should().NotContain("failed inside the studio").And.NotContain("forge-token");
            var root = JsonDocument.Parse(answer.Text).RootElement;
            root.GetProperty("refused").GetString().Should().Be("network_unreachable", tool.Name);
            root.GetProperty("retry_safe").GetBoolean().Should().BeTrue("reading runs and logs changes nothing");
            root.GetProperty("network").GetProperty("kind").GetString().Should().Be(kind);
            root.GetProperty("network").GetProperty("host").GetString().Should().Be("forge.example.test");
            var error = root.GetProperty("errors")[0];
            error.GetProperty("field").GetString().Should().Be(field);
            error.GetProperty("message").GetString().Should()
                .StartWith($"Could not reach forge.example.test ({words}")
                .And.Contain("This is the network, not Forgejo")
                .And.Contain("safe to try again in a minute");
        }
    }

    [Fact]
    public async Task Under_F1_the_read_says_the_network_and_the_same_read_a_minute_later_answers()
    {
        var git = Repo("https://forge.example.test/nova-globen/fravashi.git");
        Host("forge.example.test", OnboardingProviders.Forgejo, null);
        var clock = new FakeNetworkClock();
        var forge = Scripted(NetworkScript.F1(clock), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ForgejoTasks) });

        var during = await Call(new CiRunsTool(git, forge), "{}");
        during.Text.Should().Contain("network_unreachable").And.Contain("name_not_resolved");

        clock.Advance(TimeSpan.FromSeconds(61));
        Data(await Call(new CiRunsTool(git, forge), "{}")).GetProperty("count").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task A_status_the_forge_answered_with_is_never_called_the_network()
    {
        var git = Repo("https://github.com/acme/app.git");
        Host("github.com", OnboardingProviders.GitHub, "gh-token");
        _http.Routes["https://api.github.com/repos/acme/app/actions/runs?per_page=10"] = (HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");

        var unauthorized = JsonDocument.Parse((await Call(new CiRunsTool(git, _forge), "{}")).Text).RootElement;
        unauthorized.GetProperty("refused").GetString().Should().Be("provider_failed");
        unauthorized.TryGetProperty("network", out _).Should().BeFalse();
        unauthorized.TryGetProperty("retry_safe", out _).Should().BeFalse();

        // No route for the jobs list: the forge's 404 is its answer.
        var notFound = JsonDocument.Parse((await Call(new CiRunLogTool(git, _forge), """{"run": "42"}""")).Text).RootElement;
        notFound.GetProperty("refused").GetString().Should().Be("provider_failed");
        notFound.GetProperty("errors")[0].GetProperty("message").GetString().Should().NotContain("network");

        // A Forgejo log page that is not there is "no log", not the network.
        var forgejo = Repo("https://forge.example.test/nova-globen/fravashi.git");
        Host("forge.example.test", OnboardingProviders.Forgejo, null);
        (await Call(new CiRunLogTool(forgejo, _forge), """{"run": "231"}""")).Text.Should().Contain("no_log");
    }

    [Fact]
    public async Task The_callers_own_cancellation_stays_a_cancellation()
    {
        Host("forge.example.test", OnboardingProviders.Forgejo, null);
        var forge = Scripted(new NetworkScript(new FakeNetworkClock(), new NetworkPhase(NetworkBehaviour.Healthy)));
        var repository = forge.Repository("https://forge.example.test/nova-globen/fravashi.git")!;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => forge.ListAsync(repository, 10, null, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("a call the caller stopped is not the network");
    }

    private ForgeRuns Scripted(NetworkScript script, Func<HttpRequestMessage, HttpResponseMessage>? answer = null)
    {
        var access = new GitHostAccess(
            _hosts, _secrets, Substitute.For<IProviderConnectionStore>(), Substitute.For<IConfigurationStore>());
        return new ForgeRuns(new HttpClient(new ScriptedHttpHandler(script, answer)), () => access);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private void Host(string host, string provider, string? token)
    {
        _hosts.Get(host).Returns(new GitHostRecord(host, provider, "cred:" + host, DateTimeOffset.UtcNow));
        _secrets.Get("cred:" + host).Returns(token);
    }

    private SessionGit Repo(string? origin)
    {
        var repo = Path.Combine(_base, Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-b", "main");
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "one\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", "Initial commit");
        if (origin is not null)
        {
            Git(repo, "remote", "add", "origin", origin);
        }

        var sessions = new SqliteSessionStore(_store.Factory);
        var workspace = "w-" + Path.GetFileName(repo);
        sessions.Save(new SessionRecord(
            _session, workspace, AgentTool.Claude, "t", null, AgentSessionState.Running, DateTimeOffset.UtcNow, null));
        return new SessionGit(sessions, SessionGitWorkTests.WorkspaceGitOver(repo, workspace));
    }

    private Task<StudioToolResult> Call(IStudioTool tool, string arguments) =>
        tool.InvokeAsync(new StudioToolCall(
            _session, tool.Name, JsonDocument.Parse(arguments).RootElement, Guid.NewGuid().ToString("N"), ProjectId.Default));

    private static JsonElement Data(StudioToolResult answer)
    {
        answer.IsError.Should().BeFalse(answer.Text);
        return JsonDocument.Parse(answer.Text).RootElement.GetProperty("data");
    }

    private static void Git(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, "git {0} failed: {1}", string.Join(' ', args), stderr.GetAwaiter().GetResult());
        _ = stdout.GetAwaiter().GetResult();
    }

    /// <summary>One request as it left: enough to assert the URL and how it authenticated.</summary>
    public sealed record SentRequest(string Url, string? Authorization);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body)> Routes { get; } = new(StringComparer.Ordinal);

        public List<SentRequest> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Sent.Add(new SentRequest(url, request.Headers.Authorization?.ToString()));
            var (status, body) = Routes.TryGetValue(url, out var route) ? route : (HttpStatusCode.NotFound, "no such route in this test");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
