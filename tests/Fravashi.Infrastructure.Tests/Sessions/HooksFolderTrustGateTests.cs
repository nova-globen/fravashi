using AwesomeAssertions;
using Fravashi.Application.Git;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Tests.Persistence;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 288: ADR 0028's configuration plane includes the folder the host's git runs hooks from. With
/// <c>core.hooksPath</c> naming an ordinary folder, a trusted edit there asks with the reason on the card while an edit
/// elsewhere still runs trusted; a path outside the repository and an unset value leave the rule as it was; and a
/// lookup that fails asks for anything that looks like a hooks folder. The host's git is faked at the runner, so the
/// questions <see cref="HostGitHooksFolder"/> asks are the ones held here.
/// </summary>
public sealed class HooksFolderTrustGateTests : IDisposable
{
    private const string Workspace = "w1";
    private const string HostRoot = "C:/Users/me/Repositories/app";

    private static readonly AgentSessionId Session = AgentSessionId.New();

    private readonly SqliteTestContext _store = new();
    private readonly SqliteWorkspaceTrust _trust;
    private readonly IGitCommandRunner _runner = Substitute.For<IGitCommandRunner>();
    private readonly string _copy = Path.Combine(
        Path.GetTempPath(), "fravashi-hooks-" + Guid.NewGuid().ToString("N")[..8]);

    private string Copy => _copy.Replace('\\', '/');

    public HooksFolderTrustGateTests()
    {
        Directory.CreateDirectory(Path.Combine(_copy, "scripts", "hooks"));
        Directory.CreateDirectory(Path.Combine(_copy, "hooks"));
        Directory.CreateDirectory(Path.Combine(_copy, "githooks"));
        _trust = new SqliteWorkspaceTrust(_store.Factory);
        _trust.Set(Workspace, WorkspaceTrust.Edits);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_copy, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Dictionary<string, string> Editing(string path) => new()
    {
        ["file_path"] = path,
        ["old_string"] = "a",
        ["new_string"] = "b",
    };

    private IWorkspaceGit Git()
    {
        var git = Substitute.For<IWorkspaceGit>();
        git.For(Workspace).Returns(new WorkspaceGitServices(
            Workspace,
            _runner,
            Substitute.For<IGitService>(),
            Substitute.For<IGitWorkingTreeService>(),
            Substitute.For<IGitRemoteService>()));
        return git;
    }

    private void HooksPath(string? value)
    {
        _runner.RunAsync(
                Arg.Is<IReadOnlyList<string>>(a => a.Contains("core.hooksPath")),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(value is null ? new GitCommandResult(1, "", "") : new GitCommandResult(0, value + "\n", ""));
        _runner.RunAsync(
                Arg.Is<IReadOnlyList<string>>(a => a.Contains("--show-toplevel")),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GitCommandResult(0, HostRoot + "\n\n", ""));
    }

    private AgentApprovalBroker Broker(IWorkspaceHooksFolder? hooks)
    {
        var broker = new AgentApprovalBroker(TimeSpan.FromMinutes(5), trust: _trust, hooks: hooks);
        broker.RegisterSession(Session, Copy, workspaceId: Workspace);
        return broker;
    }

    private static async Task<PendingToolApproval> AsksAsync(AgentApprovalBroker broker, string path)
    {
        var trusted = new List<TrustedToolCall>();
        broker.Trusted += trusted.Add;
        var pending = broker.RequestAsync(Session, "Edit", "toolu_1", Editing(path));

        broker.PendingFor(Session).Should().ContainSingle();
        trusted.Should().BeEmpty();
        var card = broker.PendingFor(Session)[0];
        broker.Answer(card.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
        return card;
    }

    private static async Task RunsTrustedAsync(AgentApprovalBroker broker, string path)
    {
        var decision = await broker.RequestAsync(Session, "Edit", "toolu_1", Editing(path));

        decision.Approved.Should().BeTrue();
        decision.Reason.Should().Be(AgentApprovalBroker.TrustedEditReason);
        broker.PendingFor(Session).Should().BeEmpty();
    }

    [Fact]
    public async Task With_hooks_path_scripts_hooks_a_trusted_edit_to_a_hook_asks_with_the_reason()
    {
        HooksPath("scripts/hooks");
        var broker = Broker(new HostGitHooksFolder(Git()));

        var card = await AsksAsync(broker, Copy + "/scripts/hooks/pre-commit");

        card.Warning.Should().Be(WorkspaceTrustLevels.HooksFolderReason);
        card.Warning.Should().Contain("git runs this folder's scripts on the host at the next commit");
    }

    [Fact]
    public async Task With_hooks_path_scripts_hooks_a_trusted_edit_beside_it_still_runs()
    {
        HooksPath("scripts/hooks");

        await RunsTrustedAsync(Broker(new HostGitHooksFolder(Git())), Copy + "/scripts/build.sh");
    }

    [Theory]
    [InlineData("C:/Users/me/shared-hooks")]
    [InlineData("D:\\hooks")]
    [InlineData("../shared-hooks")]
    public async Task A_hooks_path_outside_the_repository_changes_nothing_inside_the_copy(string outside)
    {
        HooksPath(outside);
        var broker = Broker(new HostGitHooksFolder(Git()));

        await RunsTrustedAsync(broker, Copy + "/scripts/hooks/pre-commit");
        await RunsTrustedAsync(broker, Copy + "/hooks/pre-commit");
    }

    [Fact]
    public async Task With_no_hooks_path_set_the_rule_is_exactly_adr_0028s()
    {
        HooksPath(null);
        var broker = Broker(new HostGitHooksFolder(Git()));

        await RunsTrustedAsync(broker, Copy + "/scripts/hooks/pre-commit");
        await RunsTrustedAsync(broker, Copy + "/hooks/pre-commit");
        (await AsksAsync(broker, Copy + "/.githooks/pre-commit")).Warning.Should().BeNull("a hidden path asked without a sentence before");
    }

    [Fact]
    public async Task With_no_port_at_all_the_rule_is_exactly_adr_0028s() =>
        await RunsTrustedAsync(Broker(hooks: null), Copy + "/hooks/pre-commit");

    [Theory]
    [InlineData("hooks/pre-commit")]
    [InlineData("githooks/pre-commit")]
    public async Task A_failed_lookup_asks_for_a_path_under_a_hooks_looking_folder(string path)
    {
        _runner.RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("the bridge is down"));
        var broker = Broker(new HostGitHooksFolder(Git()));

        var card = await AsksAsync(broker, Copy + "/" + path);

        card.Warning.Should().Be(WorkspaceTrustLevels.UnknownHooksFolderReason);
        await RunsTrustedAsync(broker, Copy + "/scripts/build.sh");
    }

    [Fact]
    public async Task A_git_failure_is_unknown_and_never_none()
    {
        _runner.RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(new GitCommandResult(128, "", "fatal: something"));

        (await new HostGitHooksFolder(Git()).ForAsync(Workspace)).Should().Be(WorkspaceHooksFolder.Unknown);
    }

    [Fact]
    public async Task No_bridge_for_the_workspace_is_unknown() =>
        (await new HostGitHooksFolder(Substitute.For<IWorkspaceGit>()).ForAsync(Workspace))
            .Should().Be(WorkspaceHooksFolder.Unknown);

    [Fact]
    public async Task A_lookup_that_outlasts_its_budget_is_unknown()
    {
        _runner.RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(2));
                return new GitCommandResult(0, "", "");
            });

        (await new HostGitHooksFolder(Git(), TimeSpan.FromMilliseconds(50)).ForAsync(Workspace))
            .Should().Be(WorkspaceHooksFolder.Unknown);
    }

    [Fact]
    public async Task The_answer_is_asked_again_at_each_edit_so_a_changed_hooks_path_applies_to_the_next_one()
    {
        HooksPath(null);
        var broker = Broker(new HostGitHooksFolder(Git()));
        await RunsTrustedAsync(broker, Copy + "/scripts/hooks/pre-commit");

        HooksPath("scripts/hooks");
        await AsksAsync(broker, Copy + "/scripts/hooks/pre-commit");
    }
}
