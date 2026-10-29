using Bunit;
using Fravashi.Application.Git;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Repository;
using Fravashi.Web.Components.Git;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 231 on the Git page: a remote operation that meets the network shows what happened under the sync buttons,
/// with Retry only when doing it again is safe and Check origin only for a push whose answer was lost, and nothing
/// from the bridge ends the circuit. The real <see cref="GitRemoteService"/> runs over a scripted runner, so the words
/// are the ones the product says.
/// </summary>
public sealed class GitPageNetworkTests
{
    private const string Local = "1111111111111111111111111111111111111111";
    private const string Other = "2222222222222222222222222222222222222222";

    private sealed class ScriptedRunner : IGitCommandRunner
    {
        public Func<GitCommandResult> Push { get; set; } = () => new(0, "", "");

        public Func<GitCommandResult> LsRemote { get; set; } = () => new(0, "", "");

        public List<string> Ran { get; } = [];

        public bool IsAvailable() => true;

        public Task<GitCommandResult> RunAsync(
            IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
        {
            var command = string.Join(' ', args);
            Ran.Add(command);
            return Task.FromResult(command switch
            {
                "rev-parse --abbrev-ref HEAD" => new GitCommandResult(0, "main\n", ""),
                "rev-parse --abbrev-ref @{u}" => new GitCommandResult(0, "origin/main\n", ""),
                "rev-parse HEAD" => new GitCommandResult(0, Local + "\n", ""),
                "push origin main" => Push(),
                "ls-remote origin refs/heads/main" => LsRemote(),
                _ => new GitCommandResult(0, "", ""),
            });
        }
    }

    private sealed class NoHosts : IGitHostStore
    {
        public IReadOnlyList<GitHostRecord> GetAll() => [];

        public GitHostRecord? Get(string host) => null;

        public void Upsert(GitHostRecord record)
        {
        }

        public void Remove(string host)
        {
        }
    }

    private sealed class NoSecrets : ISecretStore
    {
        public void Set(string reference, string plaintext)
        {
        }

        public string? Get(string reference) => null;

        public void Remove(string reference)
        {
        }
    }

    private sealed class NoConnections : IProviderConnectionStore
    {
        public IReadOnlyList<ProviderConnection> GetAll() => [];

        public IReadOnlyList<ProviderConnection> GetByKind(ProviderKind kind) => [];

        public ProviderConnection? Get(string id) => null;

        public void Upsert(ProviderConnection connection)
        {
        }

        public void Remove(string id)
        {
        }
    }

    private sealed class NoSettings : IConfigurationStore
    {
        public string? Get(string key) => null;

        public IReadOnlyDictionary<string, string?> GetAll() => new Dictionary<string, string?>();

        public void Set(string key, string? value)
        {
        }

        public void SetMany(IReadOnlyDictionary<string, string?> values)
        {
        }

        public void Remove(string key)
        {
        }
    }

    /// <summary>A remote service that throws past its own wording, to prove the page still catches it.</summary>
    private sealed class ThrowingRemote : IGitRemoteService
    {
        public Task<IReadOnlyList<GitRemote>> GetRemotesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitRemote>>([]);

        public Task<GitSyncOutcome> FetchAsync(CancellationToken cancellationToken = default) => Throw();

        public Task<GitSyncOutcome> PullAsync(CancellationToken cancellationToken = default) => Throw();

        public Task<GitSyncOutcome> PushAsync(CancellationToken cancellationToken = default) => Throw();

        public Task<GitSyncOutcome> PushTagAsync(string tag, CancellationToken cancellationToken = default) => Throw();

        public Task<GitSyncOutcome> ComparePushAsync(GitPendingPush pending, CancellationToken cancellationToken = default) => Throw();

        private static Task<GitSyncOutcome> Throw() =>
            Task.FromException<GitSyncOutcome>(new TaskCanceledException("A task was canceled."));
    }

    private static Exception StudioTimeout() =>
        new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 600 seconds elapsing.",
            new TimeoutException("The operation was canceled."));

    private static BunitContext Context(IGitRemoteService remote)
    {
        var ctx = new BunitContext();
        GitPageTests.Register(ctx);
        ctx.Services.AddSingleton(remote);
        return ctx;
    }

    private static GitRemoteService Real(ScriptedRunner runner) =>
        new(runner, new RepositoryWriteGate(), new GitHostAccess(new NoHosts(), new NoSecrets(), new NoConnections(), new NoSettings()),
            (_, _) => Task.CompletedTask);

    [Fact]
    public void A_studio_timeout_on_push_that_did_not_reach_origin_is_worded_with_retry_and_the_page_keeps_rendering()
    {
        var runner = new ScriptedRunner
        {
            Push = () => throw StudioTimeout(),
            LsRemote = () => new GitCommandResult(0, $"{Other}\trefs/heads/main\n", ""),
        };
        using var ctx = Context(Real(runner));
        var cut = ctx.Render<GitPage>();

        GitPageTests.OpenSyncMenu(cut);
        cut.InvokeAsync(() => cut.Find("[data-testid=git-push]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-sync-note]")));
        Assert.Equal(
            "The push did not reach origin (origin did not answer within 10 minutes, so the push was stopped); origin is still at 2222222. Nothing changed there.",
            cut.Find("[data-testid=git-sync-note] span").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid=git-sync-retry]"));
        Assert.Empty(cut.FindAll("[data-testid=git-sync-check]"));

        // Still alive: Retry pushes again, and this time it lands.
        runner.Push = () => new GitCommandResult(0, "", "");
        cut.InvokeAsync(() => cut.Find("[data-testid=git-sync-retry]").Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=git-sync-note]")));
        Assert.Equal(2, runner.Ran.Count(c => c == "push origin main"));
    }

    [Fact]
    public void A_push_of_unknown_outcome_offers_check_origin_and_no_retry_and_checking_pushes_nothing()
    {
        var runner = new ScriptedRunner
        {
            Push = () => new GitCommandResult(1, "",
                "error: RPC failed; curl 56 Recv failure: Connection reset by peer\nsend-pack: unexpected disconnect while reading sideband packet\nfatal: the remote end hung up unexpectedly\nEverything up-to-date\n"),
            LsRemote = () => new GitCommandResult(128, "",
                "fatal: unable to access 'https://nonexistent-host.invalid/r.git/': Could not resolve host: nonexistent-host.invalid"),
        };
        using var ctx = Context(Real(runner));
        var cut = ctx.Render<GitPage>();

        GitPageTests.OpenSyncMenu(cut);
        cut.InvokeAsync(() => cut.Find("[data-testid=git-push]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-sync-check]")));
        var note = cut.Find("[data-testid=git-sync-note] span").TextContent;
        Assert.StartsWith("The push was sent but no answer came back", note);
        Assert.DoesNotContain("failed", note, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("[data-testid=git-sync-retry]"));

        runner.LsRemote = () => new GitCommandResult(0, $"{Local}\trefs/heads/main\n", "");
        cut.InvokeAsync(() => cut.Find("[data-testid=git-sync-check]").Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=git-sync-note]")));
        Assert.Equal(1, runner.Ran.Count(c => c == "push origin main"));
        var notes = ctx.Services.GetRequiredService<INotificationHistory>().Recent;
        Assert.Contains(notes, n => n.Severity == NotificationSeverity.Success && n.Message.Contains("the push arrived", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refusal_is_shown_without_retry()
    {
        var runner = new ScriptedRunner
        {
            Push = () => new GitCommandResult(128, "", "fatal: Authentication failed for 'https://example.com/r.git/'"),
        };
        using var ctx = Context(Real(runner));
        var cut = ctx.Render<GitPage>();

        GitPageTests.OpenSyncMenu(cut);
        cut.InvokeAsync(() => cut.Find("[data-testid=git-push]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-sync-note]")));
        Assert.StartsWith("Origin did not accept the credentials.", cut.Find("[data-testid=git-sync-note] span").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=git-sync-retry]"));
        Assert.Empty(cut.FindAll("[data-testid=git-sync-check]"));
    }

    [Fact]
    public void An_exception_past_the_services_own_wording_is_caught_by_the_page()
    {
        using var ctx = Context(new ThrowingRemote());
        var cut = ctx.Render<GitPage>();

        GitPageTests.OpenSyncMenu(cut);
        cut.InvokeAsync(() => cut.Find("[data-testid=git-push]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-sync-note]")));
        Assert.Contains("stopped before an answer came back", cut.Find("[data-testid=git-sync-note]").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid=git-sync-menu]"));
    }
}
