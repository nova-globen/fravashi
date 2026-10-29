using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.AppRunner;
using Xunit;

namespace Fravashi.AppRunner.Tests;

/// <summary>
/// Backlog 226 (plan @69): the ONE named field a verification run gets. A spec can never set it, no operation but a
/// verification run's start passes it, only the application container receives it, the token is minted per run and
/// never sits in an engine argument, and nothing the app runner reports prints it.
/// </summary>
[Collection(ProcessEnvironment.Collection)]
public sealed class VerificationRunFieldTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fravashi-verification-field", Guid.NewGuid().ToString("N"), "studio");

    public VerificationRunFieldTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".devcontainer"));
        File.WriteAllText(Path.Combine(_root, ".devcontainer", "devcontainer.json"),
            """{ "image": "docker.io/library/python:3.12-alpine", "containerEnv": { "APP_MODE": "verify" }, "customizations": { "fravashi": { "port": 8080 } } }""");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return relative;
    }

    private AppRunner Runner(AppRunnerTests.RecordingEngine engine) => new(
        engine,
        new AppRunnerOptions { ScratchDirectory = Path.Combine(Path.GetDirectoryName(_root)!, "scratch"), RelayImage = "fravashi:0.9.64" },
        id => id == "ws-studio" ? _root : null,
        new AppRunnerTests.TestClock(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void The_field_has_the_names_the_studio_reads()
    {
        VerificationRunField.Name.Should().Be("FRAVASHI_VERIFICATION_RUN");
        VerificationRunField.WindowTokenName.Should().Be("FRAVASHI_WINDOW_TOKEN");
        VerificationRunField.Names.Should().Equal("FRAVASHI_VERIFICATION_RUN", "FRAVASHI_WINDOW_TOKEN");
    }

    // ---- a spec can never set it -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("FRAVASHI_VERIFICATION_RUN")]
    [InlineData("FRAVASHI_WINDOW_TOKEN")]
    public void A_spec_naming_the_field_in_its_environment_is_refused_by_name_with_the_reason(string name)
    {
        var read = SpecReader.Read(_root, Write(".devcontainer/devcontainer.json",
            $$"""{ "image": "alpine", "containerEnv": { "{{name}}": "{{new string('a', 64)}}" } }"""), Ceilings.Default);

        read.Spec.Should().BeNull();
        read.Refusals.Should().ContainSingle().Which.Should().Be(new SpecRefusal("containerEnv." + name, VerificationRunField.Refusal));
        read.Refusals[0].Reason.Should().Contain("backlog 226").And.Contain("never a spec");
    }

    [Theory]
    [InlineData("FRAVASHI_VERIFICATION_RUN")]
    [InlineData("FRAVASHI_WINDOW_TOKEN")]
    public void A_spec_naming_the_field_as_a_build_argument_is_refused_by_name(string name)
    {
        Write(".devcontainer/Dockerfile", "FROM alpine\n");
        var read = SpecReader.Read(_root, Write(".devcontainer/devcontainer.json",
            $$"""{ "build": { "dockerfile": "Dockerfile", "args": { "{{name}}": "x" } } }"""), Ceilings.Default);

        read.Refusals.Should().Contain(new SpecRefusal("build.args." + name, VerificationRunField.Refusal));
        read.Spec.Should().BeNull();
    }

    [Theory]
    [InlineData("ENV FRAVASHI_VERIFICATION_RUN=x", "FRAVASHI_VERIFICATION_RUN")]
    [InlineData("env FRAVASHI_WINDOW_TOKEN x", "FRAVASHI_WINDOW_TOKEN")]
    [InlineData("  ARG FRAVASHI_VERIFICATION_RUN", "FRAVASHI_VERIFICATION_RUN")]
    [InlineData("ENV A=1 FRAVASHI_WINDOW_TOKEN=2", "FRAVASHI_WINDOW_TOKEN")]
    public void A_dockerfile_that_bakes_the_field_is_refused_by_name_as_a_bare_spec_and_as_a_devcontainer_build(string line, string name)
    {
        var bare = SpecReader.Read(_root, Write("Dockerfile", $"FROM alpine\n{line}\nEXPOSE 8080\n"), Ceilings.Default);
        bare.Spec.Should().BeNull();
        bare.Refusals.Should().ContainSingle().Which.Should().Be(new SpecRefusal("Dockerfile: " + name, VerificationRunField.Refusal));

        Write(".devcontainer/Dockerfile", $"FROM alpine\n{line}\n");
        var built = SpecReader.Read(_root, Write(".devcontainer/devcontainer.json", """{ "build": { "dockerfile": "Dockerfile" } }"""), Ceilings.Default);
        built.Spec.Should().BeNull();
        built.Refusals.Should().ContainSingle().Which.Field.Should().Be(".devcontainer/Dockerfile: " + name);
    }

    /// <summary>The host's review of the first attempt: a continuation line, or any instruction that sets it, bakes it too.</summary>
    [Theory]
    [InlineData("ENV A=1 \\\n    FRAVASHI_VERIFICATION_RUN=x", "FRAVASHI_VERIFICATION_RUN")]
    [InlineData("ENV A=1 \\\n# a comment the builder drops\n    FRAVASHI_WINDOW_TOKEN=x", "FRAVASHI_WINDOW_TOKEN")]
    [InlineData("ENV FRAVASHI_VERIFI\\\nCATION_RUN=x", "FRAVASHI_VERIFICATION_RUN")]
    [InlineData("ENV\tFRAVASHI_WINDOW_TOKEN=x", "FRAVASHI_WINDOW_TOKEN")]
    [InlineData("CMD [\"env\", \"FRAVASHI_VERIFICATION_RUN=x\", \"dotnet\", \"Fravashi.Web.dll\"]", "FRAVASHI_VERIFICATION_RUN")]
    [InlineData("ENTRYPOINT env FRAVASHI_WINDOW_TOKEN=x dotnet Fravashi.Web.dll", "FRAVASHI_WINDOW_TOKEN")]
    [InlineData("RUN echo FRAVASHI_WINDOW_TOKEN", "FRAVASHI_WINDOW_TOKEN")]
    public void A_dockerfile_that_names_the_field_on_any_instruction_or_continuation_is_refused(string lines, string name)
    {
        var read = SpecReader.Read(_root, Write("Dockerfile", $"FROM alpine\n{lines}\nEXPOSE 8080\n"), Ceilings.Default);

        read.Spec.Should().BeNull();
        read.Refusals.Should().ContainSingle().Which.Should().Be(new SpecRefusal("Dockerfile: " + name, VerificationRunField.Refusal));
    }

    [Fact]
    public void A_name_split_by_a_backtick_escape_named_in_the_parser_directive_is_found()
    {
        var read = SpecReader.Read(_root, Write("Dockerfile", "# escape=`\nFROM alpine\nENV FRAVASHI_VERIFI`\nCATION_RUN=x\n"), Ceilings.Default);

        read.Refusals.Should().ContainSingle().Which.Field.Should().Be("Dockerfile: FRAVASHI_VERIFICATION_RUN");
    }

    [Theory]
    [InlineData("# ENV FRAVASHI_VERIFICATION_RUN is the app runner's, never ours")]
    [InlineData("ENV FRAVASHI_VERIFICATION_RUNNER=1")]
    [InlineData("ENV MY_FRAVASHI_WINDOW_TOKEN=1")]
    public void A_dockerfile_that_only_mentions_the_name_in_a_comment_or_names_a_longer_one_is_read(string line)
    {
        var read = SpecReader.Read(_root, Write("Dockerfile", $"FROM alpine\n{line}\nEXPOSE 8080\n"), Ceilings.Default);

        read.Refusals.Should().BeEmpty();
        read.Spec!.EntryPort.Should().Be(8080);
    }

    [Fact]
    public void This_repositorys_own_dockerfile_does_not_bake_the_field()
    {
        var dockerfile = Path.Combine(RepositoryRoot(), "Dockerfile");
        SpecReader.DeclaresVerificationField("Dockerfile", File.ReadAllLines(dockerfile)).Should().BeEmpty(
            "a disposable studio is built from this Dockerfile, and the field must only ever come from a verification run's start");
    }

    // ---- only a verification run's start passes it, and only to the application ------------------------------------

    [Fact]
    public async Task An_ordinary_start_and_every_other_operation_pass_neither_name()
    {
        var engine = new AppRunnerTests.RecordingEngine();
        var runner = Runner(engine);
        var built = await runner.BuildAsync("ws-studio", null, CancellationToken.None);

        var run = await runner.StartAsync(built.BuildId!, null, null, CancellationToken.None);
        await runner.ReadyAsync(run.RunId!, CancellationToken.None);
        await runner.LogsAsync(run.RunId!, 0, CancellationToken.None);
        await runner.ListAsync(CancellationToken.None);
        await runner.StopAsync(run.RunId!, CancellationToken.None);
        await runner.RemoveAsync(run.RunId!, CancellationToken.None);
        await runner.SweepAsync(CancellationToken.None);

        run.WindowToken.Should().BeNull();
        engine.Builds.Should().OnlyContain(b => !b.BuildArgs.Keys.Any(VerificationRunField.Names.Contains));
        engine.Containers.Should().OnlyContain(c => c.SecretEnvironment.Count == 0 && !c.Environment.Keys.Any(VerificationRunField.Names.Contains));
        engine.Calls.Should().NotContain(c => c.Contains("FRAVASHI_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_verification_start_gives_the_application_alone_a_token_minted_for_that_run()
    {
        var engine = new AppRunnerTests.RecordingEngine();
        var runner = Runner(engine);
        var built = await runner.BuildAsync("ws-studio", null, CancellationToken.None);

        var first = await runner.StartAsync(built.BuildId!, null, null, CancellationToken.None, verification: true);
        var second = await runner.StartAsync(built.BuildId!, null, null, CancellationToken.None, verification: true);

        first.Started.Should().BeTrue(first.Refusal);
        var token = first.WindowToken!.Value;
        token.Should().MatchRegex("^[0-9a-f]{64}$");
        second.WindowToken!.Value.Should().NotBe(token, "each run gets its own token");

        var app = engine.Containers.Single(c => c.Name == $"fravashi-run-{first.RunId}");
        app.SecretEnvironment.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["FRAVASHI_VERIFICATION_RUN"] = token,
            ["FRAVASHI_WINDOW_TOKEN"] = token,
        });
        app.Environment.Should().ContainKey("APP_MODE").And.NotContainKeys(VerificationRunField.Names);
        engine.Containers.Where(c => c.Name.EndsWith("-relay", StringComparison.Ordinal))
            .Should().OnlyContain(c => c.SecretEnvironment.Count == 0 && c.Environment.Count == 0, "the relay never gets the field");
        engine.Builds.Should().OnlyContain(b => !b.BuildArgs.Keys.Any(VerificationRunField.Names.Contains));
    }

    [Fact]
    public async Task Nothing_the_app_runner_reports_prints_the_token()
    {
        var engine = new AppRunnerTests.RecordingEngine();
        var runner = Runner(engine);
        var built = await runner.BuildAsync("ws-studio", null, CancellationToken.None);
        var run = await runner.StartAsync(built.BuildId!, null, null, CancellationToken.None, verification: true);
        var token = run.WindowToken!.Value;

        var said = new List<string?>
        {
            run.ToString(),
            run.WindowToken.ToString(),
            (await runner.ReadyAsync(run.RunId!, CancellationToken.None)).ToString(),
            (await runner.LogsAsync(run.RunId!, 0, CancellationToken.None)).ToString(),
            string.Join("\n", await runner.ListAsync(CancellationToken.None)),
            string.Join("\n", engine.Calls),
            string.Join("\n", engine.Containers.SelectMany(c => c.Labels.Values)),
        };

        said.Should().OnlyContain(s => s == null || !s.Contains(token, StringComparison.Ordinal));
        run.ToString().Should().Contain("withheld");
    }

    // ---- the token never sits in an engine argument ---------------------------------------------------------------

    [Fact]
    public void The_run_template_names_the_field_and_carries_its_value_only_in_the_engine_commands_own_environment()
    {
        var token = VerificationRunField.Mint();
        var template = new ContainerTemplate(
            "fravashi-run-r1", "fravashi-apprun:b1", ["fravashi-run-r1"], new Dictionary<string, string>(),
            new Dictionary<string, string> { ["APP_MODE"] = "verify" }, null, null, null, null, Ceilings.Default, false)
        {
            SecretEnvironment = token.Environment,
        };

        var args = CliEngine.CreateArgs(template);

        args.Should().ContainInConsecutiveOrder("--env", "FRAVASHI_VERIFICATION_RUN");
        args.Should().ContainInConsecutiveOrder("--env", "FRAVASHI_WINDOW_TOKEN");
        args.Should().ContainInConsecutiveOrder("--env", "APP_MODE=verify");
        args.Should().NotContain(a => a.Contains(token.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void The_engine_command_is_given_the_runs_token_even_when_this_process_carries_another()
    {
        const string live = "live-studio-token-that-must-never-reach-a-run";
        var before = Environment.GetEnvironmentVariable(VerificationRunField.WindowTokenName);
        Environment.SetEnvironmentVariable(VerificationRunField.WindowTokenName, live);
        try
        {
            var token = VerificationRunField.Mint();
            var start = CliEngine.StartInfo("podman", ["create", "--env", "FRAVASHI_WINDOW_TOKEN"], token.Environment);

            start.Environment["FRAVASHI_WINDOW_TOKEN"].Should().Be(token.Value);
            start.Environment["FRAVASHI_VERIFICATION_RUN"].Should().Be(token.Value);
            start.ArgumentList.Should().NotContain(a => a.Contains(token.Value, StringComparison.Ordinal));
            start.Environment.Values.Should().NotContain(live);

            var ordinary = CliEngine.StartInfo("podman", ["start", "fravashi-run-r1"]);
            ordinary.Environment.Should().NotContainKeys(VerificationRunField.Names, "no other engine command inherits either name");
        }
        finally
        {
            Environment.SetEnvironmentVariable(VerificationRunField.WindowTokenName, before);
        }
    }

    /// <summary>
    /// The host's review of the first attempt asked for the handoff to be MEASURED, not asserted from a template. This
    /// runs <see cref="CliEngine"/> against a real executable that writes down, for every call, the arguments it was
    /// given and the two values in its environment. The engine's own reading of <c>--env NAME</c> is measured by
    /// <see cref="AppRunnerLiveTests"/> against podman or docker.
    /// </summary>
    [UnixFact]
    public async Task A_real_engine_process_is_given_the_value_in_its_environment_for_the_create_call_alone_and_in_no_argument()
    {
        // Under the test's own output, not the temp folder: a temp folder may be mounted noexec (the studio container's is).
        var folder = Path.Combine(AppContext.BaseDirectory, "fake-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var cleanup = new DeleteOnDispose(folder);
        var log = Path.Combine(folder, "calls.log");
        var script = Path.Combine(folder, "engine");
        File.WriteAllText(script, string.Join("\n",
            "#!/bin/sh",
            $"echo \"CALL $1 FLAG=[$FRAVASHI_VERIFICATION_RUN] WINDOW=[$FRAVASHI_WINDOW_TOKEN]\" >> '{log}'",
            $"for a in \"$@\"; do echo \"ARG $a\" >> '{log}'; done",
            "exit 0",
            string.Empty));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        const string live = "live-studio-token-the-host-might-carry";
        var before = Environment.GetEnvironmentVariable(VerificationRunField.WindowTokenName);
        Environment.SetEnvironmentVariable(VerificationRunField.WindowTokenName, live);
        try
        {
            var token = VerificationRunField.Mint();
            var template = new ContainerTemplate(
                "fravashi-run-r1", "fravashi-apprun:b1", ["fravashi-run-r1", "fravashi-run-r1-edge"], new Dictionary<string, string>(),
                new Dictionary<string, string>(), null, null, null, null, Ceilings.Default, false)
            {
                SecretEnvironment = token.Environment,
            };

            var started = await new CliEngine(script).StartContainerAsync(template, CancellationToken.None);
            started.Ok.Should().BeTrue(started.Output);

            var lines = File.ReadAllLines(log);
            var calls = lines.Where(l => l.StartsWith("CALL ", StringComparison.Ordinal)).ToList();
            calls.Should().HaveCount(3, "create, connect to the second network, start");
            calls[0].Should().Be($"CALL create FLAG=[{token.Value}] WINDOW=[{token.Value}]");
            calls.Skip(1).Should().OnlyContain(c => c.EndsWith("FLAG=[] WINDOW=[]", StringComparison.Ordinal),
                "only the create call is given the run's values, and no engine call inherits this process's");
            lines.Should().OnlyContain(l => !l.Contains(live, StringComparison.Ordinal));
            lines.Where(l => l.StartsWith("ARG ", StringComparison.Ordinal)).Should().OnlyContain(l => !l.Contains(token.Value, StringComparison.Ordinal));
            lines.Should().ContainInConsecutiveOrder("ARG --env", "ARG FRAVASHI_VERIFICATION_RUN");
            lines.Should().ContainInConsecutiveOrder("ARG --env", "ARG FRAVASHI_WINDOW_TOKEN");
        }
        finally
        {
            Environment.SetEnvironmentVariable(VerificationRunField.WindowTokenName, before);
        }
    }

    // ---- the HTTP surface: only the start reads the field, and only its answer carries the token ---------------------

    [Fact]
    public async Task Only_the_start_operation_reads_verification_and_only_its_answer_carries_the_token()
    {
        var engine = new AppRunnerTests.RecordingEngine();
        await using var app = AppRunnerApplication.Create("runner-token", ["http://127.0.0.1:0"], Runner(engine));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        client.DefaultRequestHeaders.Add(AppRunnerToken.Header, "runner-token");

        var built = await (await client.PostAsJsonAsync("/apprunner/builds", new { workspaceId = "ws-studio", verification = true }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var buildId = built.GetProperty("buildId").GetString();
        built.TryGetProperty("windowToken", out _).Should().BeFalse();

        var ordinary = await (await client.PostAsJsonAsync("/apprunner/runs", new { buildId })).Content.ReadFromJsonAsync<JsonElement>();
        ordinary.GetProperty("windowToken").ValueKind.Should().Be(JsonValueKind.Null);

        var verified = await client.PostAsJsonAsync("/apprunner/runs", new { buildId, verification = true });
        verified.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await verified.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("windowToken").GetString();
        token.Should().MatchRegex("^[0-9a-f]{64}$");
        engine.Containers.Single(c => c.Name == $"fravashi-run-{body.GetProperty("runId").GetString()}")
            .SecretEnvironment["FRAVASHI_VERIFICATION_RUN"].Should().Be(token);

        var runId = body.GetProperty("runId").GetString();
        foreach (var text in new[]
                 {
                     await client.GetStringAsync($"/apprunner/runs/{runId}/ready"),
                     await client.GetStringAsync($"/apprunner/runs/{runId}/logs"),
                     await client.GetStringAsync("/apprunner/runs"),
                 })
        {
            text.Should().NotContain(token!);
        }

        engine.Builds.Should().OnlyContain(b => !b.BuildArgs.Keys.Any(VerificationRunField.Names.Contains));
        await app.StopAsync();
    }

    private sealed class DeleteOnDispose(string folder) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A fact that needs a POSIX shell to run a stand-in engine; skipped with its reason on Windows.</summary>
    private sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute() => Skip = OperatingSystem.IsWindows() ? "The stand-in engine is a POSIX shell script; CI runs this on Linux." : null;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fravashi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (Fravashi.sln) was not found.");
    }
}
