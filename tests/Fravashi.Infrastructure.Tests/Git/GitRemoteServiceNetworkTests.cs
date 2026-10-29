using System.Net.Http;
using System.Net.Sockets;
using AwesomeAssertions;
using Fravashi.Application.Git;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Repository;
using Fravashi.Infrastructure.Tests.AgentRuntimes;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Git;

/// <summary>
/// Backlog 231: fetch, pull and push when the network fails, over a scripted runner that answers each git command as
/// the bridge would (git's measured stderr, or the studio-to-bridge hop throwing). Plan @48's faults: F1 a name that
/// will not resolve, F3 a link down (a black-holed connect), F4 a link down long enough that the studio's own
/// 10-minute timeout stops the call.
/// </summary>
public sealed class GitRemoteServiceNetworkTests
{
    private const string Local = "1111111111111111111111111111111111111111";
    private const string Other = "2222222222222222222222222222222222222222";

    private const string NameNotResolved =
        "fatal: unable to access 'https://nonexistent-host.invalid/r.git/': Could not resolve host: nonexistent-host.invalid";

    private const string BlackHoled =
        "fatal: unable to access 'https://192.0.2.1/r.git/': Failed to connect to 192.0.2.1 port 443 after 134265 ms: Couldn't connect to server";

    private const string ResetAfterPack =
        "error: RPC failed; curl 56 Recv failure: Connection reset by peer\nsend-pack: unexpected disconnect while reading sideband packet\nfatal: the remote end hung up unexpectedly\nEverything up-to-date\n";

    private const string Rejected =
        "To https://example.com/r.git\n ! [rejected]        main -> main (fetch first)\nerror: failed to push some refs to 'https://example.com/r.git'\n";

    private const string AuthFailed = "fatal: Authentication failed for 'https://example.com/r.git/'";

    /// <summary>A runner that answers by the command's first words; anything unscripted succeeds with no output.</summary>
    private sealed class ScriptedRunner : IGitCommandRunner
    {
        private readonly Dictionary<string, Func<GitCommandResult>> _answers = new(StringComparer.Ordinal);

        public List<string> Ran { get; } = [];

        public ScriptedRunner()
        {
            On("rev-parse --abbrev-ref HEAD", () => Ok("main\n"));
            On("rev-parse --abbrev-ref @{u}", () => Ok("origin/main\n"));
            On("rev-parse HEAD", () => Ok(Local + "\n"));
            On("rev-parse --verify refs/tags/v1.0.0", () => Ok(Local + "\n"));
            On("remote get-url origin", () => Ok("https://example.com/r.git\n"));
        }

        public ScriptedRunner On(string command, Func<GitCommandResult> answer)
        {
            _answers[command] = answer;
            return this;
        }

        public bool IsAvailable() => true;

        public Task<GitCommandResult> RunAsync(
            IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
        {
            var command = string.Join(' ', args);
            Ran.Add(command);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (key, answer) in _answers.OrderByDescending(a => a.Key.Length))
            {
                if (command == key || command.StartsWith(key + " ", StringComparison.Ordinal))
                {
                    return Task.FromResult(answer());
                }
            }

            return Task.FromResult(Ok(""));
        }
    }

    private static GitCommandResult Ok(string output) => new(0, output, "");

    private static GitCommandResult Failed(string stderr) => new(128, "", stderr);

    private static Func<GitCommandResult> Throws(Exception exception) => () => throw exception;

    /// <summary>HttpClient's own timeout, as it reaches a caller: a cancellation with a TimeoutException inside.</summary>
    private static Exception StudioTimeout() =>
        new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 600 seconds elapsing.",
            new TimeoutException("The operation was canceled."));

    /// <summary>No real wait by default, so a test that happens to hit the authentication retry does not sleep.</summary>
    private static Task NoDelay(TimeSpan wait, CancellationToken cancellationToken) => Task.CompletedTask;

    private static GitRemoteService Service(
        ScriptedRunner runner, Func<TimeSpan, CancellationToken, Task>? delay = null, ILogger<GitRemoteService>? logger = null) =>
        new(runner, new RepositoryWriteGate(), new GitHostAccess(
            Substitute.For<IGitHostStore>(), Substitute.For<ISecretStore>(),
            Substitute.For<IProviderConnectionStore>(), Substitute.For<IConfigurationStore>()),
            delay ?? NoDelay, logger);

    private static Task<GitSyncOutcome> Run(GitRemoteService service, string op) => op switch
    {
        "fetch" => service.FetchAsync(),
        "pull" => service.PullAsync(),
        _ => service.PushAsync(),
    };

    private static string Command(string op) => op switch
    {
        "fetch" => "fetch origin",
        "pull" => "pull --ff-only origin",
        _ => "push origin main",
    };

    [Theory]
    [InlineData("fetch", "F1")]
    [InlineData("pull", "F1")]
    [InlineData("push", "F1")]
    [InlineData("fetch", "F3")]
    [InlineData("pull", "F3")]
    [InlineData("push", "F3")]
    public async Task A_network_failure_git_reported_says_nothing_changed_and_offers_retry(string op, string fault)
    {
        var runner = new ScriptedRunner().On(Command(op), () => Failed(fault == "F1" ? NameNotResolved : BlackHoled));

        var logger = new CapturingLogger<GitRemoteService>();

        var outcome = await Run(Service(runner, logger: logger), op);

        outcome.Succeeded.Should().BeFalse();
        outcome.Failure.Should().Be(GitSyncFailure.Network);
        outcome.RetrySafe.Should().BeTrue();
        outcome.Error.Should().Be(fault == "F1"
            ? "Could not reach origin (name not resolved). Nothing changed."
            : "Could not reach origin (timed out connecting after 134 s). Nothing changed.");
        runner.Ran.Should().NotContain(c => c.StartsWith("ls-remote", StringComparison.Ordinal),
            "a push that never left has nothing to compare");
        logger.Lines.Should().BeEmpty("a network failure is not an authentication refusal, so no retry line is written");
    }

    [Theory]
    [InlineData("fetch")]
    [InlineData("pull")]
    public async Task F4_a_read_the_studio_stopped_is_worded_and_safe_to_retry(string op)
    {
        var runner = new ScriptedRunner().On(Command(op), Throws(StudioTimeout()));

        var outcome = await Run(Service(runner), op);

        outcome.Failure.Should().Be(GitSyncFailure.Network);
        outcome.RetrySafe.Should().BeTrue();
        outcome.Error.Should().Be($"Origin did not answer within 10 minutes, so the {op} was stopped. Nothing was merged.");
    }

    [Fact]
    public async Task F4_a_timed_out_push_whose_commit_origin_has_is_reported_as_pushed()
    {
        var runner = new ScriptedRunner()
            .On("push origin main", Throws(StudioTimeout()))
            .On("ls-remote origin refs/heads/main", () => Ok($"{Local}\trefs/heads/main\n"));

        var outcome = await Service(runner).PushAsync();

        outcome.Succeeded.Should().BeTrue();
        outcome.Detail.Should().Contain("origin did not answer within 10 minutes").And.Contain("the push arrived");
        runner.Ran.Should().Contain($"update-ref refs/remotes/origin/main {Local}", "the local view of origin is moved as a completed push would");
    }

    [Fact]
    public async Task F4_a_timed_out_push_origin_does_not_have_is_not_pushed_and_offers_retry()
    {
        var runner = new ScriptedRunner()
            .On("push origin main", Throws(StudioTimeout()))
            .On("ls-remote origin refs/heads/main", () => Ok($"{Other}\trefs/heads/main\n"));

        var outcome = await Service(runner).PushAsync();

        outcome.Succeeded.Should().BeFalse();
        outcome.Failure.Should().Be(GitSyncFailure.Network);
        outcome.RetrySafe.Should().BeTrue("it was compared, and origin does not have it");
        outcome.Error.Should().Be(
            "The push did not reach origin (origin did not answer within 10 minutes, so the push was stopped); origin is still at 2222222. Nothing changed there.");
    }

    [Fact]
    public async Task A_push_whose_answer_was_lost_and_origin_cannot_be_asked_is_unknown_never_failed()
    {
        var runner = new ScriptedRunner()
            .On("push origin main", () => new GitCommandResult(1, "", ResetAfterPack))
            .On("ls-remote origin refs/heads/main", () => Failed(NameNotResolved));

        var outcome = await Service(runner).PushAsync();

        outcome.Succeeded.Should().BeFalse();
        outcome.Failure.Should().Be(GitSyncFailure.OutcomeUnknown);
        outcome.RetrySafe.Should().BeFalse("it may have arrived");
        outcome.Pending.Should().Be(new GitPendingPush("refs/heads/main", Local));
        outcome.Error.Should().Be(
            "The push was sent but no answer came back (the connection was reset), and origin cannot be asked right now "
            + "(name not resolved). It may have arrived: check origin before pushing again.");
        outcome.Error.Should().NotContainAny("failed", "Everything up-to-date");
    }

    [Fact]
    public async Task Checking_again_once_origin_answers_settles_an_unknown_push()
    {
        var reachable = false;
        var runner = new ScriptedRunner()
            .On("push origin main", () => new GitCommandResult(1, "", ResetAfterPack))
            .On("ls-remote origin refs/heads/main", () => reachable ? Ok($"{Local}\trefs/heads/main\n") : Failed(NameNotResolved));
        var service = Service(runner);
        var unknown = await service.PushAsync();

        reachable = true;
        var settled = await service.ComparePushAsync(unknown.Pending!);

        settled.Succeeded.Should().BeTrue();
        runner.Ran.Count(c => c.StartsWith("push", StringComparison.Ordinal)).Should().Be(1, "checking pushes nothing");
    }

    [Fact]
    public async Task A_first_push_settled_by_comparing_also_sets_the_upstream()
    {
        var runner = new ScriptedRunner()
            .On("rev-parse --abbrev-ref @{u}", () => Failed("fatal: no upstream configured for branch 'main'"))
            .On("push --set-upstream origin main", Throws(StudioTimeout()))
            .On("ls-remote origin refs/heads/main", () => Ok($"{Local}\trefs/heads/main\n"));

        var outcome = await Service(runner).PushAsync();

        outcome.Succeeded.Should().BeTrue();
        runner.Ran.Should().Contain("branch --set-upstream-to=origin/main main");
    }

    [Fact]
    public async Task A_tag_push_whose_answer_was_lost_is_compared_by_its_tag_ref()
    {
        var runner = new ScriptedRunner()
            .On("push origin refs/tags/v1.0.0", Throws(StudioTimeout()))
            .On("ls-remote origin refs/tags/v1.0.0", () => Ok($"{Local}\trefs/tags/v1.0.0\n{Other}\trefs/tags/v1.0.0^{{}}\n"));

        var outcome = await Service(runner).PushTagAsync("v1.0.0");

        outcome.Succeeded.Should().BeTrue();
        runner.Ran.Should().NotContain(c => c.StartsWith("update-ref", StringComparison.Ordinal), "a tag has no remote-tracking ref");
    }

    [Fact]
    public async Task Pushing_a_tag_runs_exactly_push_origin_refs_tags_name_backlog_285()
    {
        var runner = new ScriptedRunner();

        var outcome = await Service(runner).PushTagAsync("v1.0.0");

        outcome.Succeeded.Should().BeTrue();
        runner.Ran.Should().Contain("push origin refs/tags/v1.0.0");
    }

    [Theory]
    [InlineData(Rejected, GitRefusal.Rejected, "Origin refused it: origin has commits this branch does not.")]
    [InlineData(AuthFailed, GitRefusal.Authentication, "Origin did not accept the credentials.")]
    public async Task A_refusal_says_so_and_offers_no_retry(string stderr, GitRefusal refusal, string start)
    {
        var runner = new ScriptedRunner().On("push origin main", () => new GitCommandResult(1, "", stderr));

        var outcome = await Service(runner).PushAsync();

        outcome.Failure.Should().Be(GitSyncFailure.Refused);
        outcome.Refusal.Should().Be(refusal);
        outcome.RetrySafe.Should().BeFalse();
        outcome.Error.Should().StartWith(start).And.Contain("Git said:");
        runner.Ran.Should().NotContain(c => c.StartsWith("ls-remote", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_push_refused_on_authentication_once_is_retried_and_the_retry_succeeds()
    {
        var attempt = 0;
        var runner = new ScriptedRunner().On("push origin main", () => ++attempt == 1 ? Failed(AuthFailed) : Ok(""));
        var logger = new CapturingLogger<GitRemoteService>();

        var outcome = await Service(runner, logger: logger).PushAsync();

        outcome.Succeeded.Should().BeTrue();
        runner.Ran.Count(c => c == "push origin main").Should().Be(2, "exactly two pushes were attempted");
        logger.Lines.Should().ContainSingle(l => l.Level == LogLevel.Information)
            .Which.Message.Should().Contain("Retrying").And.Contain("the push of main").And.Contain("origin")
            .And.NotContain(AuthFailed, "never the raw git stderr");
    }

    [Fact]
    public async Task Two_authentication_refusals_in_a_row_park_with_todays_words()
    {
        var runner = new ScriptedRunner().On("push origin main", () => Failed(AuthFailed));
        var logger = new CapturingLogger<GitRemoteService>();

        var outcome = await Service(runner, logger: logger).PushAsync();

        outcome.Succeeded.Should().BeFalse();
        outcome.Failure.Should().Be(GitSyncFailure.Refused);
        outcome.Refusal.Should().Be(GitRefusal.Authentication);
        outcome.RetrySafe.Should().BeFalse();
        outcome.Error.Should().StartWith("Origin did not accept the credentials.").And.Contain("Git said:");
        runner.Ran.Count(c => c == "push origin main").Should().Be(2, "retried once, parked on the second refusal");
        logger.Lines.Should().HaveCount(2, "one line for the retry firing, one for the retry being refused too");
        logger.Lines.Select(l => l.Level).Should().AllBeEquivalentTo(LogLevel.Information);
        logger.Lines.ElementAt(0).Message.Should().Contain("Retrying").And.Contain("the push of main").And.Contain("origin");
        logger.Lines.ElementAt(1).Message.Should().Contain("refused on authentication again after the retry")
            .And.Contain("the push of main").And.Contain("origin");
        logger.Lines.Should().NotContain(l => l.Message.Contains(AuthFailed));
    }

    [Fact]
    public async Task A_non_fast_forward_rejection_is_never_retried()
    {
        var runner = new ScriptedRunner().On("push origin main", () => Failed(Rejected));
        var logger = new CapturingLogger<GitRemoteService>();

        var outcome = await Service(runner, logger: logger).PushAsync();

        outcome.Refusal.Should().Be(GitRefusal.Rejected);
        runner.Ran.Count(c => c == "push origin main").Should().Be(1, "only an authentication refusal is retried");
        logger.Lines.Should().BeEmpty("a non fast forward rejection is not an authentication refusal");
    }

    [Fact]
    public async Task A_lost_answer_compare_refused_once_on_authentication_is_retried_and_settles()
    {
        var attempt = 0;
        var runner = new ScriptedRunner()
            .On("push origin main", Throws(StudioTimeout()))
            .On("ls-remote origin refs/heads/main", () => ++attempt == 1 ? Failed(AuthFailed) : Ok($"{Local}\trefs/heads/main\n"));
        var logger = new CapturingLogger<GitRemoteService>();

        var outcome = await Service(runner, logger: logger).PushAsync();

        outcome.Succeeded.Should().BeTrue();
        runner.Ran.Count(c => c == "ls-remote origin refs/heads/main").Should().Be(2, "retried once on the authentication refusal");
        logger.Lines.Should().ContainSingle(l => l.Level == LogLevel.Information)
            .Which.Message.Should().Contain("Retrying").And.Contain("the ls-remote compare of refs/heads/main").And.Contain("origin")
            .And.NotContain(AuthFailed);
    }

    [Fact]
    public async Task Two_authentication_refusals_comparing_a_lost_answer_park_with_todays_words()
    {
        var runner = new ScriptedRunner()
            .On("push origin main", Throws(StudioTimeout()))
            .On("ls-remote origin refs/heads/main", () => Failed(AuthFailed));
        var logger = new CapturingLogger<GitRemoteService>();

        var outcome = await Service(runner, logger: logger).PushAsync();

        outcome.Succeeded.Should().BeFalse();
        outcome.Failure.Should().Be(GitSyncFailure.OutcomeUnknown);
        outcome.Error.Should().Contain("origin cannot be asked right now").And.Contain(AuthFailed);
        runner.Ran.Count(c => c == "ls-remote origin refs/heads/main").Should().Be(2, "retried once, parked on the second refusal");
        logger.Lines.Should().HaveCount(2, "one line for the retry firing, one for the retry being refused too");
        logger.Lines.Select(l => l.Level).Should().AllBeEquivalentTo(LogLevel.Information);
        logger.Lines.ElementAt(0).Message.Should().Contain("Retrying").And.Contain("the ls-remote compare of refs/heads/main").And.Contain("origin");
        logger.Lines.ElementAt(1).Message.Should().Contain("refused on authentication again after the retry")
            .And.Contain("the ls-remote compare of refs/heads/main").And.Contain("origin");
        logger.Lines.Should().NotContain(l => l.Message.Contains(AuthFailed));
    }

    [Fact]
    public async Task The_authentication_retry_pauses_once_before_trying_again()
    {
        var waited = new List<TimeSpan>();
        var runner = new ScriptedRunner().On("push origin main", () => Failed(AuthFailed));

        await Service(runner, (wait, _) =>
        {
            waited.Add(wait);
            return Task.CompletedTask;
        }).PushAsync();

        waited.Should().ContainSingle().Which.Should().Be(GitRemoteService.AuthenticationRetryPause);
    }

    [Fact]
    public async Task A_bridge_that_cannot_be_reached_changed_nothing()
    {
        var refused = new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused",
            new SocketException((int)SocketError.ConnectionRefused));
        var runner = new ScriptedRunner().On("push origin main", Throws(refused));

        var outcome = await Service(runner).PushAsync();

        outcome.Failure.Should().Be(GitSyncFailure.Network);
        outcome.RetrySafe.Should().BeTrue();
        outcome.Error.Should().Be("Could not reach the host bridge (connection refused). Nothing changed.");
    }

    [Fact]
    public async Task A_bridge_that_is_down_before_the_push_is_worded_rather_than_thrown()
    {
        var refused = new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused",
            new SocketException((int)SocketError.ConnectionRefused));
        var runner = new ScriptedRunner().On("rev-parse --abbrev-ref HEAD", Throws(refused));

        var outcome = await Service(runner).PushAsync();

        outcome.Failure.Should().Be(GitSyncFailure.Network);
        outcome.Error.Should().Be("Could not reach the host bridge (connection refused). Nothing changed.");
    }

    [Fact]
    public void Remote_git_runs_on_the_bridge_clients_ten_minute_client_and_nothing_else_does()
    {
        Fravashi.Infrastructure.Bridge.BridgeClient.RemoteGitTimeout.Should().Be(TimeSpan.FromMinutes(10))
            .And.Be(GitRemoteService.RemoteTimeout, "the words name the limit the client enforces");
        foreach (var remote in new[] { "fetch", "pull", "push", "ls-remote" })
        {
            Fravashi.Infrastructure.Bridge.BridgeClient.IsRemoteGit([remote, "origin"]).Should().BeTrue(remote);
        }

        Fravashi.Infrastructure.Bridge.BridgeClient.IsRemoteGit(["status"]).Should().BeFalse();
        Fravashi.Infrastructure.Bridge.BridgeClient.IsRemoteGit([]).Should().BeFalse();
    }

    [Fact]
    public async Task A_cancellation_the_caller_asked_for_is_not_worded()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => Service(new ScriptedRunner()).PushAsync(cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
