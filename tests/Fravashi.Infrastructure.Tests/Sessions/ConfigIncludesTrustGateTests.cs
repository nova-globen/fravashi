using System.Text.RegularExpressions;
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
/// Backlog 289: ADR 0028's configuration plane reaches a work-tree file named by <c>include.path</c> or
/// <c>includeif.&lt;condition&gt;.path</c> anywhere in the repository's own config chain. A trusted edit to such a
/// file asks with the reason on the card while an edit elsewhere still runs trusted; an include outside the
/// repository and no include at all leave #288's rule exactly as it was; and because an include can name any file at
/// all (unlike a hooks folder, which at least has a naming convention), a failed lookup here asks for EVERY edit in
/// the workspace rather than narrowing to a heuristic. The host's git is faked at the runner, so the questions
/// <see cref="HostGitConfigIncludes"/> asks are the ones held here.
/// </summary>
public sealed class ConfigIncludesTrustGateTests : IDisposable
{
    private const string Workspace = "w1";
    private const string HostRoot = "C:/Users/me/Repositories/app";
    private const string TopConfig = HostRoot + "/.git/config";

    private static readonly AgentSessionId Session = AgentSessionId.New();

    private readonly SqliteTestContext _store = new();
    private readonly SqliteWorkspaceTrust _trust;
    private readonly IGitCommandRunner _runner = Substitute.For<IGitCommandRunner>();
    private readonly string _copy = Path.Combine(
        Path.GetTempPath(), "fravashi-config-includes-" + Guid.NewGuid().ToString("N")[..8]);

    private string Copy => _copy.Replace('\\', '/');

    public ConfigIncludesTrustGateTests()
    {
        Directory.CreateDirectory(Path.Combine(_copy, "config"));
        _trust = new SqliteWorkspaceTrust(_store.Factory);
        _trust.Set(Workspace, WorkspaceTrust.Edits);

        // Every test starts with no include anywhere and nothing active: specific setups below override one call at
        // a time, and NSubstitute keeps the most recently configured matching return, so these defaults never win
        // over a later, more specific one.
        RevParse(HostRoot, "");
        ShowOrigin();
        AnyOtherFileNamesNoInclude();
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

    private void RevParse(
        string topLevel, string prefix, string gitPathConfig = ".git/config", string gitPathConfigWorktree = ".git/config.worktree") =>
        _runner.RunAsync(
                Arg.Is<IReadOnlyList<string>>(a => a.Contains("--show-toplevel")),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GitCommandResult(0, topLevel + "\n" + prefix + "\n" + gitPathConfig + "\n" + gitPathConfigWorktree + "\n", ""));

    private void RevParseFails() =>
        _runner.RunAsync(
                Arg.Is<IReadOnlyList<string>>(a => a.Contains("--show-toplevel")),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GitCommandResult(128, "", "fatal: not a git repository"));

    private void AnyOtherFileNamesNoInclude() =>
        _runner.RunAsync(
                Arg.Is<IReadOnlyList<string>>(a => a.Contains("--get-regexp")),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GitCommandResult(1, "", ""));

    /// <summary>One config file's own literal include/includeif keys, exactly as `--null --type=path --get-regexp` prints them.</summary>
    private void ConfigFile(string absoluteFile, params (string Key, string Value)[] pairs)
    {
        var stdout = string.Concat(pairs.Select(p => p.Key + "\n" + p.Value + "\0"));
        _runner.RunAsync(
                Arg.Is<IReadOnlyList<string>>(a => a.Contains("--file") && a.Contains(absoluteFile) && a.Contains("--get-regexp")),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GitCommandResult(0, stdout, ""));
    }

    private void ConfigFileFails(string absoluteFile) =>
        _runner.RunAsync(
                Arg.Is<IReadOnlyList<string>>(a => a.Contains("--file") && a.Contains(absoluteFile) && a.Contains("--get-regexp")),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GitCommandResult(128, "", "fatal: bad config"));

    /// <summary>What `git config --show-origin --list` would print: one "file:&lt;origin&gt;\t&lt;key&gt;=&lt;value&gt;" line per entry.</summary>
    private void ShowOrigin(params (string Origin, string KeyValue)[] entries)
    {
        var stdout = string.Concat(entries.Select(e => $"file:{e.Origin}\t{e.KeyValue}\n"));
        _runner.RunAsync(
                Arg.Is<IReadOnlyList<string>>(a => a.Contains("--show-origin")),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GitCommandResult(0, stdout, ""));
    }

    private void ShowOriginFails() =>
        _runner.RunAsync(
                Arg.Is<IReadOnlyList<string>>(a => a.Contains("--show-origin")),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GitCommandResult(128, "", "fatal: bad config"));

    private AgentApprovalBroker Broker(IWorkspaceConfigIncludes? includes = null, IWorkspaceHooksFolder? hooks = null)
    {
        var broker = new AgentApprovalBroker(TimeSpan.FromMinutes(5), trust: _trust, hooks: hooks, configIncludes: includes);
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
    public async Task With_include_path_naming_config_git_inc_a_trusted_edit_to_it_asks_and_one_beside_it_does_not()
    {
        // .git/config's own directory is .git/, so the value that reaches config/git.inc is ../config/git.inc, not
        // config/git.inc.
        ConfigFile(TopConfig, ("include.path", "../config/git.inc"));
        var broker = Broker(new HostGitConfigIncludes(Git()));

        var card = await AsksAsync(broker, Copy + "/config/git.inc");
        card.Warning.Should().Be(WorkspaceTrustLevels.ConfigIncludeReason);

        await RunsTrustedAsync(broker, Copy + "/config/app.json");
    }

    [Fact]
    public async Task An_include_outside_the_repository_changes_nothing()
    {
        ConfigFile(TopConfig, ("include.path", "C:/Users/me/elsewhere/shared.inc"));
        var broker = Broker(new HostGitConfigIncludes(Git()));

        await RunsTrustedAsync(broker, Copy + "/config/git.inc");
        await RunsTrustedAsync(broker, Copy + "/config/app.json");
    }

    [Fact]
    public async Task With_no_include_anywhere_the_rule_is_exactly_288s()
    {
        var broker = Broker(new HostGitConfigIncludes(Git()));

        await RunsTrustedAsync(broker, Copy + "/config/git.inc");
        (await AsksAsync(broker, Copy + "/.githooks/pre-commit")).Warning.Should().BeNull("a hidden path asked without a sentence before");
    }

    [Fact]
    public async Task A_nested_include_is_configuration_plane_too()
    {
        ConfigFile(TopConfig, ("include.path", "../config/git.inc"));
        // The recursion probes the HOST's absolute path (the arithmetic is rooted at HostRoot, not at the test's
        // own temp "Copy" folder, which stands in for the containment side of the same coordinates).
        ConfigFile(HostRoot + "/config/git.inc", ("include.path", "nested.inc"));
        var broker = Broker(new HostGitConfigIncludes(Git()));

        var card = await AsksAsync(broker, Copy + "/config/nested.inc");
        card.Warning.Should().Be(WorkspaceTrustLevels.ConfigIncludeReason);
    }

    [Fact]
    public async Task A_workspace_rooted_at_a_repository_sub_folder_resolves_the_relative_git_path_answer_correctly()
    {
        // From a sub-folder workspace, git rev-parse --git-path answers relative to THAT folder, not the repo
        // root: ../.git/config here means HostRoot/.git/config, not HostRoot's own parent, which does not exist.
        // The include value climbs back down into sub/ (.git/'s own directory, then ../sub/config/git.inc) so it
        // lands INSIDE this workspace's own copy, which only mirrors the sub/ subtree.
        RevParse(HostRoot, "sub/", "../.git/config");
        ConfigFile(TopConfig, ("include.path", "../sub/config/git.inc"));
        var broker = Broker(new HostGitConfigIncludes(Git()));

        var card = await AsksAsync(broker, Copy + "/config/git.inc");
        card.Warning.Should().Be(WorkspaceTrustLevels.ConfigIncludeReason);
    }

    [Fact]
    public async Task A_per_worktree_config_worktree_file_is_configuration_plane_too()
    {
        // extensions.worktreeConfig repositories also read config.worktree; it is seeded alongside config and
        // answers a path whether or not the extension is on, so its own includes count too.
        RevParse(HostRoot, "", gitPathConfigWorktree: ".git/config.worktree");
        ConfigFile(HostRoot + "/.git/config.worktree", ("include.path", "../config/worktree.inc"));
        var broker = Broker(new HostGitConfigIncludes(Git()));

        var card = await AsksAsync(broker, Copy + "/config/worktree.inc");
        card.Warning.Should().Be(WorkspaceTrustLevels.ConfigIncludeReason);
    }

    [Fact]
    public async Task A_linked_worktree_or_submodules_shared_config_file_is_found_by_asking_git_path_not_assumed()
    {
        // In a linked worktree or a submodule, <toplevel>/.git is a FILE naming a gitdir elsewhere, and the real
        // config file --git-path config answers with is not <toplevel>/.git/config at all. Here it is some other
        // absolute path entirely, proving the raw read starts where git itself says, never where .git/config
        // would literally be.
        const string sharedConfig = HostRoot + "/.git-common/config";
        RevParse(HostRoot, "", sharedConfig);
        ConfigFile(sharedConfig, ("include.path", "../config/git.inc"));
        var broker = Broker(new HostGitConfigIncludes(Git()));

        var card = await AsksAsync(broker, Copy + "/config/git.inc");
        card.Warning.Should().Be(WorkspaceTrustLevels.ConfigIncludeReason);
    }

    [Fact]
    public async Task An_includeif_condition_that_is_not_active_now_still_asks()
    {
        // The raw config file names onbranch:release unconditionally; show-origin, standing in for the CURRENT
        // branch not being release, omits it entirely - exactly what a real git does for an inactive condition. The
        // raw reading is condition-blind on purpose, because the branch can change under the same conversation.
        ConfigFile(TopConfig, ("includeif.onbranch:release.path", "../config/release.inc"));
        ShowOrigin((".git/config", "core.repositoryformatversion=0"));
        var broker = Broker(new HostGitConfigIncludes(Git()));

        var card = await AsksAsync(broker, Copy + "/config/release.inc");
        card.Warning.Should().Be(WorkspaceTrustLevels.ConfigIncludeReason);
    }

    [Fact]
    public async Task A_named_but_absent_file_still_asks()
    {
        // config/absent.inc is never created on disk: creating it is the same act as editing it (backlog 289).
        ConfigFile(TopConfig, ("include.path", "../config/absent.inc"));
        var broker = Broker(new HostGitConfigIncludes(Git()));

        File.Exists(Path.Combine(_copy, "config", "absent.inc")).Should().BeFalse();
        var card = await AsksAsync(broker, Copy + "/config/absent.inc");
        card.Warning.Should().Be(WorkspaceTrustLevels.ConfigIncludeReason);
    }

    [Fact]
    public async Task Show_origin_catches_what_the_raw_reading_alone_would_miss()
    {
        // A file reached only through show-origin's own effective-configuration net (method 2), never named by a
        // literal include/includeif key the raw reading (method 1) could see - the two methods are additive. A
        // relative origin git prints is relative to the REPOSITORY ROOT with .git/ already baked in, not relative
        // to some other file's directory the way a raw include value is.
        ShowOrigin((".git/../elsewhere/extra.inc", "core.fsmonitor=/tmp/x.sh"));
        var broker = Broker(new HostGitConfigIncludes(Git()));

        var card = await AsksAsync(broker, Copy + "/elsewhere/extra.inc");
        card.Warning.Should().Be(WorkspaceTrustLevels.ConfigIncludeReason);
    }

    [Fact]
    public async Task A_failed_lookup_asks_for_every_edit_not_only_ones_that_look_like_an_include()
    {
        // Unlike a hooks folder, an include can name any file at all: there is no naming convention to narrow to, so
        // the decision recorded on the item is that the whole workspace asks until the next successful lookup.
        ConfigFileFails(TopConfig);
        var broker = Broker(new HostGitConfigIncludes(Git()));

        var card = await AsksAsync(broker, Copy + "/src/Program.cs");
        card.Warning.Should().Be(WorkspaceTrustLevels.UnknownConfigIncludesReason);
    }

    [Fact]
    public async Task A_chain_deeper_than_the_cap_is_unknown_not_a_truncated_answer()
    {
        // An include chain that never ends (each chain/N.inc includes chain/(N+1).inc) runs past the recursion's
        // own cap; the cap stopping the walk must answer Unknown, never a partial Known or None built from
        // whatever the walk reached before it was cut off.
        var chainFile = new Regex(Regex.Escape(HostRoot) + @"/chain/(\d+)\.inc$");
        _runner.RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var args = call.ArgAt<IReadOnlyList<string>>(0);
                if (args.Contains("--show-toplevel"))
                {
                    return new GitCommandResult(0, HostRoot + "\n\n.git/config\n.git/config.worktree\n", "");
                }

                if (args.Contains("--show-origin"))
                {
                    return new GitCommandResult(0, "", "");
                }

                var fileIndex = args.ToList().IndexOf("--file");
                var file = fileIndex >= 0 ? args[fileIndex + 1] : null;
                if (file == TopConfig)
                {
                    // .git/config's own directory is .git/, so the value must climb out of it to land in
                    // HostRoot/chain/0.inc.
                    return new GitCommandResult(0, "include.path\n../chain/0.inc\0", "");
                }

                if (file is not null && chainFile.Match(file) is { Success: true } match)
                {
                    var next = int.Parse(match.Groups[1].Value) + 1;
                    return new GitCommandResult(0, $"include.path\n{next}.inc\0", "");
                }

                return new GitCommandResult(1, "", "");
            });

        var broker = Broker(new HostGitConfigIncludes(Git()));

        (await AsksAsync(broker, Copy + "/src/Program.cs")).Warning.Should().Be(WorkspaceTrustLevels.UnknownConfigIncludesReason);
    }

    [Fact]
    public async Task A_failed_show_origin_also_asks_for_every_edit()
    {
        ShowOriginFails();
        var broker = Broker(new HostGitConfigIncludes(Git()));

        (await AsksAsync(broker, Copy + "/src/Program.cs")).Warning.Should().Be(WorkspaceTrustLevels.UnknownConfigIncludesReason);
    }

    [Fact]
    public async Task A_failed_rev_parse_also_asks_for_every_edit()
    {
        RevParseFails();
        var broker = Broker(new HostGitConfigIncludes(Git()));

        (await AsksAsync(broker, Copy + "/src/Program.cs")).Warning.Should().Be(WorkspaceTrustLevels.UnknownConfigIncludesReason);
    }

    [Fact]
    public async Task With_no_port_at_all_the_rule_is_exactly_288s() =>
        await RunsTrustedAsync(Broker(includes: null), Copy + "/config/git.inc");

    [Fact]
    public async Task The_answer_is_asked_again_at_each_edit_so_a_changed_config_applies_to_the_next_one()
    {
        var broker = Broker(new HostGitConfigIncludes(Git()));
        await RunsTrustedAsync(broker, Copy + "/config/git.inc");

        ConfigFile(TopConfig, ("include.path", "../config/git.inc"));
        await AsksAsync(broker, Copy + "/config/git.inc");
    }

    [Fact]
    public async Task A_lookup_that_outlasts_its_budget_is_unknown()
    {
        _runner.RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(2));
                return new GitCommandResult(0, "", "");
            });

        (await new HostGitConfigIncludes(Git(), TimeSpan.FromMilliseconds(50)).ForAsync(Workspace))
            .Should().Be(WorkspaceConfigIncludes.Unknown);
    }

    [Fact]
    public async Task No_bridge_for_the_workspace_is_unknown() =>
        (await new HostGitConfigIncludes(Substitute.For<IWorkspaceGit>()).ForAsync(Workspace))
            .Should().Be(WorkspaceConfigIncludes.Unknown);

    [Fact]
    public async Task An_exception_asking_git_is_unknown_and_never_none()
    {
        _runner.RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("the bridge is down"));

        (await new HostGitConfigIncludes(Git()).ForAsync(Workspace)).Should().Be(WorkspaceConfigIncludes.Unknown);
    }
}
