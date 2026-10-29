using Bunit;
using Fravashi.Application.Git;
using Fravashi.Application.Notifications;
using Fravashi.Application.PullRequests;
using Fravashi.Domain.PullRequests;
using Fravashi.Application.Repository;
using Fravashi.Domain.Git;
using Fravashi.Domain.Repository;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Git;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

public class GitPageTests
{
    private sealed class FakeGitService : IGitService
    {
        public bool IsAvailable { get; init; } = true;

        public string? UnavailableReason { get; init; }

        public List<string> Checkouts { get; } = [];

        /// <summary>Tracking checkouts keep the whole branch, so a test can see which remote reached git.</summary>
        public List<GitRemoteBranch> TrackingCheckouts { get; } = [];

        public Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default) => Task.FromResult("main");

        public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitBranch>>([new GitBranch("main", true), new GitBranch("feature/x", false)]);

        public Task CheckoutAsync(string branch, CancellationToken cancellationToken = default)
        {
            Checkouts.Add(branch);
            return Task.CompletedTask;
        }

        public Task CreateBranchAsync(string name, string baseBranch, bool checkout, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<RepositoryNode>> ListTreeAsync(string reference, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RepositoryNode>>([]);

        public Task<RepositoryFile?> ReadFileAsync(string reference, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<RepositoryFile?>(null);

        /// <summary>Every path a history filter handed git, so a test can see what reached it and what never did.</summary>
        public List<string?> CommitPathQueries { get; } = [];

        public Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string reference, int max = 50, string? path = null, CancellationToken cancellationToken = default)
        {
            CommitPathQueries.Add(path);
            return Task.FromResult<IReadOnlyList<GitCommit>>(path is null
                ? [
                    new GitCommit("abc123full", "abc123f", "feat: latest thing", "Ada", DateTimeOffset.UtcNow.AddHours(-2)),
                    new GitCommit("def456full", "def456f", "fix: earlier thing", "Grace", DateTimeOffset.UtcNow.AddDays(-3)),
                ]
                : [new GitCommit("def456full", "def456f", "fix: earlier thing", "Grace", DateTimeOffset.UtcNow.AddDays(-3))]);
        }

        public Task<IReadOnlyList<GitTag>> GetTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitTag>>([new GitTag("v1.0", "def456full", DateTimeOffset.UtcNow.AddDays(-3), "First release")]);

        public Task<GitCommitDetail?> GetCommitAsync(string sha, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitCommitDetail?>(new GitCommitDetail(
                new GitCommit(sha, sha[..7], "feat: latest thing", "Ada", DateTimeOffset.UtcNow.AddHours(-2)),
                "Body text of the commit.",
                [new GitFileChange("src/One.cs", GitChangeKind.Modified), new GitFileChange("Two.md", GitChangeKind.Added)]));

        public Task<string> GetDiffAsync(string sha, string? path = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<IReadOnlyList<GitCommit>> GetFileHistoryAsync(string path, string reference, int max = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitCommit>>([
                new GitCommit("hist1full", "hist1", $"touch {path} once", "Ada", DateTimeOffset.UtcNow.AddDays(-1)),
                new GitCommit("hist2full", "hist2", $"touch {path} twice", "Grace", DateTimeOffset.UtcNow.AddDays(-9)),
            ]);

        public Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("https://github.com/acme/widget.git");

        public Task<IReadOnlyList<GitRemoteBranch>> GetRemoteBranchesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitRemoteBranch>>([
                new GitRemoteBranch("origin/main", "origin", "main"),
                new GitRemoteBranch("origin/epic/pushed-elsewhere", "origin", "epic/pushed-elsewhere"),
            ]);

        public Task CheckoutTrackingAsync(GitRemoteBranch branch, CancellationToken cancellationToken = default)
        {
            TrackingCheckouts.Add(branch);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GitGraphCommit>> GetGraphAsync(int max = 150, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitGraphCommit>>([
                new GitGraphCommit("abc123full", "abc123f", "feat: latest thing", "Ada",
                    DateTimeOffset.UtcNow.AddHours(-2), ["def456full"], ["main", "origin/main"]),
                new GitGraphCommit("def456full", "def456f", "fix: earlier thing", "Grace",
                    DateTimeOffset.UtcNow.AddDays(-3), [], []),
            ]);

        public Task<GitLineBlame?> GetLineBlameAsync(string path, int line, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitLineBlame?>(null);

        public List<(string Base, string Head)> DiffFileRequests { get; } = [];

        public Task<IReadOnlyList<GitFileChange>> GetDiffFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default)
        {
            DiffFileRequests.Add((baseRef, headRef));
            return Task.FromResult<IReadOnlyList<GitFileChange>>([
                new GitFileChange("src/One.cs", GitChangeKind.Modified),
                new GitFileChange("docs/new.md", GitChangeKind.Added),
            ]);
        }

        public Task<string?> GetMergeBaseAsync(string first, string second, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("mergebase1");
    }

    private sealed class FakeWorkingTree : IGitWorkingTreeService
    {
        public GitWorkingStatus Status { get; set; } = new(
            "main", "origin/main", 1, 0,
            [new GitWorkingEntry("src/staged.cs", GitEntryState.Modified, Staged: true)],
            [
                new GitWorkingEntry("src/changed.cs", GitEntryState.Modified, Staged: false),
                new GitWorkingEntry("new.txt", GitEntryState.Untracked, Staged: false),
            ]);

        public List<IReadOnlyList<string>> Staged { get; } = [];

        public List<IReadOnlyList<string>> Unstaged { get; } = [];

        public List<string> Commits { get; } = [];

        public GitCommitOutcome NextCommitOutcome { get; set; } = new(true, Sha: "abc1234def");

        public Task<GitWorkingStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);

        public Task StageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            Staged.Add(paths);
            return Task.CompletedTask;
        }

        public Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            Unstaged.Add(paths);
            return Task.CompletedTask;
        }

        public Task<GitCommitOutcome> CommitAsync(string message, CancellationToken cancellationToken = default)
        {
            Commits.Add(message);
            return Task.FromResult(NextCommitOutcome);
        }

        public Task MarkExecutableAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<(string Original, string Modified)> GetWorkingDiffAsync(string path, bool staged, CancellationToken cancellationToken = default) =>
            Task.FromResult(("old content", "new content"));

        public List<(string Message, IReadOnlyList<string>? Paths)> Stashes { get; } = [];

        public List<string> StashRestores { get; } = [];

        public Task<GitCommitOutcome> StashAsync(string message, IReadOnlyList<string>? paths = null, CancellationToken cancellationToken = default)
        {
            Stashes.Add((message, paths));
            return Task.FromResult(new GitCommitOutcome(true));
        }

        public List<GitStash> StashList { get; } = [new GitStash(0, "change backup: src/changed.cs", "main")];

        public Task<IReadOnlyList<GitStash>> GetStashesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitStash>>([.. StashList]);

        public Task<GitCommitOutcome> StashPopAsync(int index, CancellationToken cancellationToken = default)
        {
            StashRestores.Add($"pop {index}");
            return Task.FromResult(new GitCommitOutcome(true));
        }

        public Task<GitCommitOutcome> StashApplyAsync(int index, CancellationToken cancellationToken = default)
        {
            StashRestores.Add($"apply {index}");
            return Task.FromResult(new GitCommitOutcome(true));
        }
    }

    private sealed class FakeRemote : IGitRemoteService
    {
        public List<string> Operations { get; } = [];

        public GitSyncOutcome NextOutcome { get; set; } = new(true);

        public Task<IReadOnlyList<GitRemote>> GetRemotesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitRemote>>([new GitRemote("origin", "https://github.com/acme/widget.git")]);

        public Task<GitSyncOutcome> FetchAsync(CancellationToken cancellationToken = default)
        {
            Operations.Add("fetch");
            return Task.FromResult(NextOutcome);
        }

        public Task<GitSyncOutcome> PullAsync(CancellationToken cancellationToken = default)
        {
            Operations.Add("pull");
            return Task.FromResult(NextOutcome);
        }

        public Task<GitSyncOutcome> PushAsync(CancellationToken cancellationToken = default)
        {
            Operations.Add("push");
            return Task.FromResult(NextOutcome);
        }

        public Task<GitSyncOutcome> PushTagAsync(string tag, CancellationToken cancellationToken = default)
        {
            Operations.Add("push-tag " + tag);
            return Task.FromResult(NextOutcome);
        }

        public GitSyncOutcome CompareOutcome { get; set; } = new(true);

        public Task<GitSyncOutcome> ComparePushAsync(GitPendingPush pending, CancellationToken cancellationToken = default)
        {
            Operations.Add("compare " + pending.RemoteRef);
            return Task.FromResult(CompareOutcome);
        }
    }

    private sealed class FakePullRequests : IPullRequestService
    {
        public bool Configured { get; set; } = true;

        public List<CreatePullRequestRequest> Created { get; } = [];

        public List<(int Number, MergeMethod Method)> Merged { get; } = [];

        public Task<PullRequestProvider> GetProviderAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Configured ? PullRequestProvider.Forgejo : PullRequestProvider.None);

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(Configured);

        public Task<IReadOnlyList<Fravashi.Domain.PullRequests.PullRequest>> ListAsync(PullRequestState state, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Fravashi.Domain.PullRequests.PullRequest>>(state == PullRequestState.Open
                ? [new Fravashi.Domain.PullRequests.PullRequest(12, "Improve things", "ada", "feat/x", "main", "open", null, DateTimeOffset.UtcNow)]
                : []);

        public Task<PullRequestDetail?> GetAsync(int number, CancellationToken cancellationToken = default) =>
            Task.FromResult<PullRequestDetail?>(new PullRequestDetail(
                new Fravashi.Domain.PullRequests.PullRequest(number, "Improve things", "ada", "feat/x", "main", "open", null, DateTimeOffset.UtcNow),
                "PR description",
                [new PullRequestComment("grace", "Looks good", DateTimeOffset.UtcNow)]));

        public Task<Fravashi.Domain.PullRequests.PullRequest> CreateAsync(CreatePullRequestRequest request, CancellationToken cancellationToken = default)
        {
            Created.Add(request);
            return Task.FromResult(new Fravashi.Domain.PullRequests.PullRequest(99, request.Title, "me", request.SourceBranch, request.TargetBranch, "open", null, DateTimeOffset.UtcNow));
        }

        public Task MergeAsync(int number, MergeMethod method, CancellationToken cancellationToken = default)
        {
            Merged.Add((number, method));
            return Task.CompletedTask;
        }

        public List<(int Number, ReviewVerdict Verdict, string Body)> Reviews { get; } = [];

        public List<int> Closed { get; } = [];

        public Exception? NextReviewError { get; set; }

        public Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(int number, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PullRequestReview>>([
                new PullRequestReview("grace", "APPROVED", "ship it", DateTimeOffset.UtcNow.AddHours(-1)),
            ]);

        public Task SubmitReviewAsync(int number, ReviewVerdict verdict, string body, CancellationToken cancellationToken = default)
        {
            if (NextReviewError is { } error)
            {
                NextReviewError = null;
                throw error;
            }

            Reviews.Add((number, verdict, body));
            return Task.CompletedTask;
        }

        public Task CloseAsync(int number, CancellationToken cancellationToken = default)
        {
            Closed.Add(number);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopNotifier : IRepositoryChangeNotifier
    {
        public IDisposable Subscribe(Action<RepositoryChange> handler) => new Dummy();

        private sealed class Dummy : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    /// <summary>Everything the page needs except its remote service, for a test that brings its own (backlog 231).</summary>
    internal static void Register(BunitContext ctx)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(TimeProvider.System);
        ctx.Services.AddSingleton<IGitService>(new FakeGitService());
        ctx.Services.AddSingleton<IGitWorkingTreeService>(new FakeWorkingTree());
        ctx.Services.AddSingleton<IPullRequestService>(new FakePullRequests());
        ctx.Services.AddSingleton<IRepositoryChangeNotifier>(new NoopNotifier());
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
    }

    /// <summary>Opens the page bar's Sync menu, where Fetch, Pull and Push live since work item #367.</summary>
    internal static void OpenSyncMenu(IRenderedComponent<GitPage> cut)
    {
        cut.InvokeAsync(() => cut.Find("[data-testid=git-sync-menu]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-sync-panel]")));
    }

    private static (BunitContext Ctx, FakeGitService Git, FakeWorkingTree Tree, FakeRemote Remote, FakePullRequests Prs) PageContext(bool available = true, string? reason = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var git = new FakeGitService { IsAvailable = available, UnavailableReason = reason };
        var tree = new FakeWorkingTree();
        var remote = new FakeRemote();
        var prs = new FakePullRequests();
        // The page takes a clock now, so relative time can be tested and a demo install fixed (backlog 71).
        ctx.Services.AddSingleton(TimeProvider.System);
        ctx.Services.AddSingleton<IGitService>(git);
        ctx.Services.AddSingleton<IGitWorkingTreeService>(tree);
        ctx.Services.AddSingleton<IGitRemoteService>(remote);
        ctx.Services.AddSingleton<IPullRequestService>(prs);
        ctx.Services.AddSingleton<IRepositoryChangeNotifier>(new NoopNotifier());
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        return (ctx, git, tree, remote, prs);
    }

    [Fact]
    public void Unconfigured_git_shows_the_honest_hint()
    {
        var (ctx, _, _, _, _) = PageContext(available: false);
        using var _1 = ctx;

        var cut = ctx.Render<GitPage>();

        Assert.NotEmpty(cut.FindAll("[data-testid=git-unavailable]"));
    }

    [Fact]
    public void The_diff_pane_shows_the_unavailable_reason_when_there_is_one()
    {
        var (ctx, _, _, _, _) = PageContext(available: false, reason: "git is not installed on this machine, so the studio cannot read or change this folder's history. Install git on this computer to use it here.");
        using var _1 = ctx;

        var cut = ctx.Render<GitPage>();

        var empty = cut.Find("[data-testid=git-diff-empty]");
        Assert.Contains("git is not installed on this machine", empty.TextContent);
        Assert.DoesNotContain("No git working copy is configured yet.", empty.TextContent);
    }

    [Fact]
    public void The_diff_pane_falls_back_to_the_old_line_when_there_is_no_reason()
    {
        var (ctx, _, _, _, _) = PageContext(available: false);
        using var _1 = ctx;

        var cut = ctx.Render<GitPage>();

        var empty = cut.Find("[data-testid=git-diff-empty]");
        Assert.Contains("No git working copy is configured yet.", empty.TextContent);
    }

    [Fact]
    public void The_page_renders_the_repository_its_upstream_and_both_change_groups()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;

        var cut = ctx.Render<GitPage>();

        Assert.Contains("widget", cut.Find("[data-testid=git-repo-current]").TextContent);
        Assert.Contains("origin/main", cut.Find("[data-testid=git-upstream]").TextContent);
        var rows = cut.FindAll("[data-testid=git-change-row]");
        Assert.Equal(3, rows.Count); // 1 staged + 2 unstaged
        // Tree view is the default: the row shows the file name, the full path rides the tooltip.
        Assert.Contains("staged.cs", rows[0].TextContent);
        Assert.Equal("src/staged.cs", rows[0].GetAttribute("title"));
    }

    [Fact]
    public void The_page_bar_shows_the_branch_and_working_tree_counts()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;

        var cut = ctx.Render<GitPage>();

        var statusline = cut.Find("[data-testid=git-statusline]").TextContent;
        Assert.Contains("main", statusline);
        Assert.Contains("2 changes", statusline);
        Assert.Contains("1 staged", statusline);
        Assert.Contains("1 ahead", statusline);
        Assert.Contains("0 behind", statusline);
    }

    [Fact]
    public void Switching_branch_asks_first_and_can_stash_before_the_switch()
    {
        var (ctx, git, tree, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        // feature/x sits under the "feature" prefix group in the branch tree; its leaf label is "x".
        cut.InvokeAsync(() => cut.FindAll("[data-testid=git-branch-row]")
            .First(r => r.GetAttribute("title")!.Contains("feature/x")).Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-checkout-confirm]")));
        Assert.Empty(git.Checkouts); // nothing happens before the confirm

        cut.InvokeAsync(() => cut.Find("[data-testid=git-checkout-stash]").Click());

        cut.WaitForAssertion(() => Assert.Single(git.Checkouts));
        Assert.Equal("feature/x", git.Checkouts[0]);
        Assert.Single(tree.Stashes);
        Assert.StartsWith("switch backup:", tree.Stashes[0].Message);
        Assert.Null(tree.Stashes[0].Paths); // the whole working tree is stashed
    }

    [Fact]
    public void Undo_reverts_a_file_by_stashing_it_as_a_named_backup()
    {
        var (ctx, _, tree, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        cut.InvokeAsync(() => cut.FindAll("[data-testid=git-row-undo]").First().Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-undo-confirm]")));
        Assert.Contains("change backup: src/staged.cs", cut.Find("[data-testid=git-undo-confirm]").TextContent);
        Assert.Empty(tree.Stashes); // nothing happens before the confirm

        cut.InvokeAsync(() => cut.Find("[data-testid=git-undo-go]").Click());

        cut.WaitForAssertion(() => Assert.Single(tree.Stashes));
        Assert.Equal("change backup: src/staged.cs", tree.Stashes[0].Message);
        Assert.Equal(["src/staged.cs"], tree.Stashes[0].Paths);
    }

    [Fact]
    public void Stash_asks_for_a_message_then_stashes_everything()
    {
        var (ctx, _, tree, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        cut.InvokeAsync(() => cut.Find("[data-testid=git-stash-open]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-stash-prompt]")));
        cut.Find("[data-testid=git-stash-message]").Input("wip: spike");
        cut.InvokeAsync(() => cut.Find("[data-testid=git-stash-go]").Click());

        cut.WaitForAssertion(() => Assert.Single(tree.Stashes));
        Assert.Equal("wip: spike", tree.Stashes[0].Message);
        Assert.Null(tree.Stashes[0].Paths);
    }

    [Fact]
    public void The_stash_list_offers_pop_and_apply()
    {
        var (ctx, _, tree, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-stash-row]")));
        cut.InvokeAsync(() => cut.Find("[data-testid=git-stash-pop]").Click());
        cut.WaitForAssertion(() => Assert.Contains("pop 0", tree.StashRestores));

        cut.InvokeAsync(() => cut.Find("[data-testid=git-stash-apply]").Click());
        cut.WaitForAssertion(() => Assert.Contains("apply 0", tree.StashRestores));
    }

    [Fact]
    public void The_pull_requests_section_initializes_on_the_plain_page_and_an_old_remote_link_still_works()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        // There is no Remote tab any more (work item #367): the section is on the page from the start.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-row]")));

        // An old /git/remote link lands on the same page, with the provider still initialized (the reported bug was a
        // tab switch showing the "no provider configured" stub although Forgejo was configured).
        cut.Render(p => p.Add(c => c.Tab, "remote"));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=git-prs-unconfigured]")));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-row]")));
    }

    [Fact]
    public void The_branches_section_lists_remote_branches_and_checks_out_tracking_locals()
    {
        var (ctx, git, _, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-remote-branch]")));
        Assert.Contains("origin/main", cut.Find("[data-testid=git-remote-branch]").TextContent);
        // The row carries a VISIBLE checkout/switch button (discoverability, owner report 2026-07-27).
        Assert.NotEmpty(cut.FindAll("[data-testid=git-remote-checkout]"));

        cut.InvokeAsync(() => cut.Find("[data-testid=git-remote-checkout]").Click());

        cut.WaitForAssertion(() => Assert.Contains(git.TrackingCheckouts, b => b.FullName == "origin/main"));
    }

    [Fact]
    public void A_remote_branch_whose_name_contains_a_slash_reaches_git_whole()
    {
        // The remote and the branch used to be flattened into one string on the way down and split apart
        // again at the first slash, which turned "epic/pushed-elsewhere" into "pushed-elsewhere".
        var (ctx, git, _, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-remote-branch]")));
        cut.InvokeAsync(() => cut.FindAll("[data-testid=git-remote-branch]")
            .First(r => r.TextContent.Contains("epic/pushed-elsewhere"))
            .QuerySelector("[data-testid=git-remote-checkout]")!.Click());

        cut.WaitForAssertion(() =>
        {
            var chosen = Assert.Single(git.TrackingCheckouts);
            Assert.Equal("origin", chosen.Remote);
            Assert.Equal("epic/pushed-elsewhere", chosen.Name);
        });
    }

    [Fact]
    public void Selecting_a_pr_shows_reviews_changed_files_and_the_lifecycle_actions()
    {
        var (ctx, git, _, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-row]")));

        cut.InvokeAsync(() => cut.Find("[data-testid=git-pr-row]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-detail]")));
        // Reviews from the provider render with the host's wording.
        Assert.Contains("approved", cut.Find("[data-testid=git-pr-review]").TextContent.ToLowerInvariant());
        // Changed files come from the local clone's origin refs (merge-base diff).
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=git-pr-file]").Count));
        Assert.Equal(("origin/main", "origin/feat/x"), git.DiffFileRequests[^1]);
        // The lifecycle actions are all present for an open PR.
        Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-approve]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-request-changes]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-merge]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-close]"));
    }

    [Fact]
    public void Approving_submits_a_review_and_request_changes_needs_a_comment()
    {
        var (ctx, _, _, _, prs) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-row]")));
        cut.InvokeAsync(() => cut.Find("[data-testid=git-pr-row]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-detail]")));

        // Request changes is disabled until a comment explains what to change.
        Assert.True(cut.Find("[data-testid=git-pr-request-changes]").HasAttribute("disabled"));

        cut.InvokeAsync(() => cut.Find("[data-testid=git-pr-approve]").Click());
        cut.WaitForAssertion(() => Assert.Single(prs.Reviews));
        Assert.Equal((12, ReviewVerdict.Approve, ""), prs.Reviews[0]);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-review-comment]")));
        cut.Find("[data-testid=git-pr-review-comment]").Input("please rename the flag");
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=git-pr-request-changes]").HasAttribute("disabled")));
        cut.InvokeAsync(() => cut.Find("[data-testid=git-pr-request-changes]").Click());

        cut.WaitForAssertion(() => Assert.Equal(2, prs.Reviews.Count));
        Assert.Equal((12, ReviewVerdict.RequestChanges, "please rename the flag"), prs.Reviews[1]);
    }

    [Fact]
    public void Review_outcomes_show_inline_in_the_pane_success_and_refusal_alike()
    {
        var (ctx, _, _, _, prs) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-row]")));
        cut.InvokeAsync(() => cut.Find("[data-testid=git-pr-row]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-approve]")));

        // A provider refusal (Forgejo's self-approve 422) lands INLINE, not only in the bell.
        prs.NextReviewError = new HttpRequestException("Forgejo API 422: user can not approve their own pull request");
        cut.InvokeAsync(() => cut.Find("[data-testid=git-pr-approve]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-action-note]")));
        var refusal = cut.Find("[data-testid=git-pr-action-note]");
        Assert.Contains("422", refusal.TextContent);
        Assert.Contains("your own pull request", refusal.TextContent);
        Assert.Contains("git-pr-action-failed", refusal.GetAttribute("class"));

        // And a success reads as one.
        cut.InvokeAsync(() => cut.Find("[data-testid=git-pr-approve]").Click());
        cut.WaitForAssertion(() =>
            Assert.Contains("Approved !12", cut.Find("[data-testid=git-pr-action-note]").TextContent));
        Assert.Contains("git-pr-action-ok", cut.Find("[data-testid=git-pr-action-note]").GetAttribute("class"));
    }

    [Fact]
    public void A_review_that_met_the_network_says_so_and_a_lost_answer_may_have_been_made()
    {
        var (ctx, _, _, _, prs) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-row]")));
        cut.InvokeAsync(() => cut.Find("[data-testid=git-pr-row]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-approve]")));

        // Backlog 231: never left, so nothing changed.
        prs.NextReviewError = new HttpRequestException(HttpRequestError.NameResolutionError, "Name or service not known");
        cut.InvokeAsync(() => cut.Find("[data-testid=git-pr-approve]").Click());
        cut.WaitForAssertion(() => Assert.Contains("Could not reach the forge (name not resolved). Nothing changed.",
            cut.Find("[data-testid=git-pr-action-note]").TextContent));

        // The client's own timeout: the review may have been recorded, and the page is still alive to say so.
        prs.NextReviewError = new TaskCanceledException("timeout", new TimeoutException());
        cut.InvokeAsync(() => cut.Find("[data-testid=git-pr-approve]").Click());
        cut.WaitForAssertion(() => Assert.Contains("the change may have been made",
            cut.Find("[data-testid=git-pr-action-note]").TextContent));
    }

    [Fact]
    public void Closing_a_pr_confirms_first_then_calls_the_service()
    {
        var (ctx, _, _, _, prs) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-row]")));
        cut.InvokeAsync(() => cut.Find("[data-testid=git-pr-row]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-close]")));

        cut.InvokeAsync(() => cut.Find("[data-testid=git-pr-close]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-close-confirm]")));
        Assert.Empty(prs.Closed); // nothing happens before the confirm

        cut.InvokeAsync(() => cut.Find("[data-testid=git-pr-close-go]").Click());

        cut.WaitForAssertion(() => Assert.Equal([12], prs.Closed));
    }

    [Fact]
    public void The_create_pr_dialog_offers_remote_only_branches_too()
    {
        // A branch pushed from another machine exists only as origin/…; it must still be pickable
        // as the pull request's source without a local checkout (owner report 2026-07-27).
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-create-open]")));

        cut.InvokeAsync(() => cut.Find("[data-testid=git-pr-create-open]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-source]")));
        var options = cut.FindAll("[data-testid=git-pr-source] option").Select(o => o.TextContent.Trim()).ToList();
        Assert.Contains("epic/pushed-elsewhere", options);
        Assert.Contains("main", options);
        Assert.Contains("feature/x", options);
    }

    [Fact]
    public void An_old_remote_link_opens_on_the_history_too_not_a_dashboard_or_an_empty_pane()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=git-commit-row]").Count));
        Assert.Empty(cut.FindAll("[data-testid=git-detail]"));
        Assert.Empty(cut.FindAll("[data-testid=git-diff-empty]"));
    }

    [Fact]
    public void Commit_button_needs_a_message_and_staged_changes_then_calls_the_service()
    {
        var (ctx, _, tree, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        Assert.True(cut.Find("[data-testid=git-commit]").HasAttribute("disabled"));

        cut.Find("[data-testid=git-commit-message]").Input("feat: something");
        Assert.False(cut.Find("[data-testid=git-commit]").HasAttribute("disabled"));

        cut.Find("[data-testid=git-commit]").Click();

        cut.WaitForAssertion(() => Assert.Single(tree.Commits));
        Assert.Equal("feat: something", tree.Commits[0]);
    }

    [Fact]
    public void Stage_and_unstage_buttons_route_the_right_paths()
    {
        var (ctx, _, tree, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        cut.Find("[data-testid=git-stage-all]").Click();
        cut.WaitForAssertion(() => Assert.Single(tree.Staged));
        Assert.Equal(["src/changed.cs", "new.txt"], tree.Staged[0]);

        cut.Find("[data-testid=git-unstage-all]").Click();
        cut.WaitForAssertion(() => Assert.Single(tree.Unstaged));
        Assert.Equal(["src/staged.cs"], tree.Unstaged[0]);
    }

    [Fact]
    public void Selecting_a_change_asks_for_its_diff_and_shows_the_header()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        cut.FindAll("[data-testid=git-change-row]")[0].Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-diff-header]")));
        // The diff opens in the detail pane, beside the history, which stays.
        Assert.NotNull(cut.Find("[data-testid=git-detail] [data-testid=git-diff-header]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=git-commit-row]"));
        Assert.Contains("staged", cut.Find("[data-testid=git-diff-header]").TextContent);
        cut.WaitForAssertion(() => ctx.JSInterop.VerifyInvoke("fravashiCode.openDiff"));
    }

    [Fact]
    public void Graph_lists_commits_and_selecting_one_shows_its_detail_and_file_diff()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        // The history is on the page from the start (work item #367): nothing to expand.
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=git-commit-row]").Count));
        Assert.Contains("feat: latest thing", cut.FindAll("[data-testid=git-commit-row]")[0].TextContent);

        cut.FindAll("[data-testid=git-commit-row]")[0].Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-detail] [data-testid=git-commit-detail]")));
        Assert.Contains("Body text of the commit.", cut.Find("[data-testid=git-commit-detail]").TextContent);
        Assert.Equal(2, cut.FindAll("[data-testid=git-commit-file]").Count);

        cut.FindAll("[data-testid=git-commit-file]")[0].Click();
        cut.WaitForAssertion(() => ctx.JSInterop.VerifyInvoke("fravashiCode.openDiff"));
    }

    [Fact]
    public void Sync_buttons_call_the_remote_service()
    {
        var (ctx, _, _, remote, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        OpenSyncMenu(cut);
        cut.InvokeAsync(() => cut.Find("[data-testid=git-fetch]").Click());
        cut.WaitForAssertion(() => Assert.Contains("fetch", remote.Operations));
        // Choosing an operation closes the menu.
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=git-sync-panel]")));

        OpenSyncMenu(cut);
        cut.InvokeAsync(() => cut.Find("[data-testid=git-pull]").Click());
        cut.WaitForAssertion(() => Assert.Contains("pull", remote.Operations));

        OpenSyncMenu(cut);
        cut.InvokeAsync(() => cut.Find("[data-testid=git-push]").Click());
        cut.WaitForAssertion(() => Assert.Contains("push", remote.Operations));
    }

    [Fact]
    public void The_side_panel_shows_the_origin_and_the_pull_requests()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;

        var cut = ctx.Render<GitPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-remote-row]")));
        Assert.Contains("origin", cut.Find("[data-testid=git-remote-row]").TextContent);
        Assert.Contains("github.com/acme/widget", cut.Find("[data-testid=git-remote-row]").TextContent);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-row]")));
        Assert.Contains("!12 Improve things", cut.Find("[data-testid=git-pr-row]").TextContent);
    }

    [Fact]
    public void Selecting_a_pull_request_shows_its_detail_and_comments()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-row]")));

        cut.Find("[data-testid=git-pr-row]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-detail]")));
        Assert.Contains("PR description", cut.Find("[data-testid=git-pr-detail]").TextContent);
        Assert.Contains("Looks good", cut.Find("[data-testid=git-pr-comment]").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-merge]"));
    }

    [Fact]
    public void Creating_a_pull_request_routes_through_the_service()
    {
        var (ctx, _, _, _, prs) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-create-open]")));

        cut.Find("[data-testid=git-pr-create-open]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-create]")));
        cut.Find("[data-testid=git-pr-title]").Input("feat: new work");
        cut.Find("[data-testid=git-pr-target]").Change("feature/x");

        cut.Find("[data-testid=git-pr-create-go]").Click();

        cut.WaitForAssertion(() => Assert.Single(prs.Created));
        Assert.Equal("feat: new work", prs.Created[0].Title);
        Assert.Equal("main", prs.Created[0].SourceBranch);
        Assert.Equal("feature/x", prs.Created[0].TargetBranch);
    }

    [Fact]
    public void Merging_calls_the_service_with_the_chosen_method()
    {
        var (ctx, _, _, _, prs) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-row]")));
        cut.Find("[data-testid=git-pr-row]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-merge]")));

        cut.Find("[data-testid=git-pr-merge]").Click();

        cut.WaitForAssertion(() => Assert.Single(prs.Merged));
        Assert.Equal((12, MergeMethod.Merge), prs.Merged[0]);
    }

    [Fact]
    public void An_unconfigured_provider_shows_the_honest_hint()
    {
        var (ctx, _, _, _, prs) = PageContext();
        prs.Configured = false;
        using var _1 = ctx;

        var cut = ctx.Render<GitPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-prs-unconfigured]")));
    }

    [Fact]
    public void The_history_route_lists_a_files_commits_and_diffs_a_selected_one()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;

        var cut = ctx.Render<GitPage>(p => p.Add(c => c.HistoryPath, "src/Program.cs"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-history]")));
        Assert.Contains("History of src/Program.cs", cut.Find("[data-testid=git-history]").TextContent);
        Assert.Equal(2, cut.FindAll("[data-testid=git-history-row]").Count);

        cut.FindAll("[data-testid=git-history-row]")[0].Click();
        cut.WaitForAssertion(() => ctx.JSInterop.VerifyInvoke("fravashiCode.openDiff"));
    }

    // ---- Work item #367: the page's structure ----

    [Fact]
    public void Opening_the_page_shows_the_history_at_once_with_no_empty_pane()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;

        var cut = ctx.Render<GitPage>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=git-main] [data-testid=git-commit-row]").Count));
        Assert.Empty(cut.FindAll("[data-testid=git-detail]"));
        Assert.Empty(cut.FindAll("[data-testid=git-diff-empty]"));
        Assert.DoesNotContain("Select a change", cut.Markup);
        // Branch and tag chips ride on their commits.
        var newest = cut.FindAll("[data-testid=git-commit-row]")[0];
        Assert.Contains("git-ref-head", newest.InnerHtml);
        Assert.Contains("origin/main", newest.TextContent);
    }

    [Fact]
    public void A_clean_working_tree_takes_one_line_and_no_commit_box()
    {
        var (ctx, _, tree, _, _) = PageContext();
        tree.Status = new GitWorkingStatus("main", "origin/main", 0, 0, [], []);
        using var _1 = ctx;

        var cut = ctx.Render<GitPage>();

        Assert.Contains("Working tree clean", cut.Find("[data-testid=git-clean]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=git-commit-message]"));
        Assert.Empty(cut.FindAll("[data-testid=git-changes]"));
        Assert.True(cut.Find("[data-testid=git-stash-open]").HasAttribute("disabled"));
    }

    [Fact]
    public void With_changes_the_commit_block_sits_above_the_history()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;

        var cut = ctx.Render<GitPage>();

        var column = cut.Find(".git-main-column");
        var order = column.Children.Select(c => c.GetAttribute("data-testid")).Where(t => t is not null).ToList();
        Assert.Equal(["git-changes", "git-graph"], order);
        Assert.NotNull(cut.Find("[data-testid=git-changes] [data-testid=git-commit-message]"));
        Assert.Empty(cut.FindAll("[data-testid=git-clean]"));
    }

    [Fact]
    public void Selecting_a_commit_opens_its_files_and_diff_in_the_detail_pane_and_closing_gives_the_space_back()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        cut.InvokeAsync(() => cut.FindAll("[data-testid=git-commit-row]")[0].Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-detail]")));
        Assert.Contains("has-detail", cut.Find("[data-testid=git-main]").GetAttribute("class"));
        Assert.Contains("active", cut.FindAll("[data-testid=git-commit-row]")[0].GetAttribute("class"));
        Assert.Equal(2, cut.FindAll("[data-testid=git-detail] [data-testid=git-commit-file]").Count);

        cut.InvokeAsync(() => cut.FindAll("[data-testid=git-commit-file]")[0].Click());
        cut.WaitForAssertion(() => ctx.JSInterop.VerifyInvoke("fravashiCode.openDiff"));
        Assert.NotEmpty(cut.FindAll("[data-testid=git-diff-mode]"));

        cut.InvokeAsync(() => cut.Find("[data-testid=git-detail-close]").Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=git-detail]")));
        Assert.DoesNotContain("has-detail", cut.Find("[data-testid=git-main]").GetAttribute("class"));
        cut.WaitForAssertion(() => ctx.JSInterop.VerifyInvoke("fravashiCode.disposeDiff"));
        Assert.Equal(2, cut.FindAll("[data-testid=git-commit-row]").Count);
    }

    [Fact]
    public void Branches_tags_stashes_and_pull_requests_are_side_panel_sections_and_the_local_remote_switch_is_gone()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;

        var cut = ctx.Render<GitPage>();

        var sidebar = cut.Find("[data-testid=git-sidebar]");
        foreach (var section in new[] { "git-branches-section", "git-tags", "git-stashes", "git-prs" })
        {
            Assert.NotNull(sidebar.QuerySelector($"[data-testid={section}]"));
        }

        Assert.Empty(cut.FindAll("[data-testid=git-tab-local]"));
        Assert.Empty(cut.FindAll("[data-testid=git-tab-remote]"));
        // Local branches come first, then the remote ones.
        var branches = cut.Find("[data-testid=git-branches]").TextContent;
        Assert.True(branches.IndexOf("Local", StringComparison.Ordinal) < branches.IndexOf("Remote", StringComparison.Ordinal));
    }

    [Fact]
    public void A_local_branch_that_is_not_checked_out_has_a_visible_checkout_that_asks_first()
    {
        var (ctx, git, _, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        var current = cut.FindAll("[data-testid=git-branch-row]").Single(r => r.QuerySelector("[data-testid=git-branch-current]") is not null);
        Assert.Contains("main", current.TextContent);
        Assert.Single(cut.FindAll("[data-testid=git-branch-checkout]"));

        cut.InvokeAsync(() => cut.Find("[data-testid=git-branch-checkout]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-checkout-confirm]")));
        Assert.Empty(git.Checkouts);
        cut.InvokeAsync(() => cut.Find("[data-testid=git-checkout-bring]").Click());
        cut.WaitForAssertion(() => Assert.Equal(["feature/x"], git.Checkouts));
    }

    [Fact]
    public void The_tags_section_lists_tags_and_a_tag_opens_its_commit()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        var tag = cut.Find("[data-testid=git-tag-row]");
        Assert.Contains("v1.0", tag.TextContent);
        Assert.Contains("First release", tag.GetAttribute("title"));

        cut.InvokeAsync(() => cut.Find("[data-testid=git-tag-row]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-detail] [data-testid=git-commit-detail]")));
        Assert.Contains("def456f", cut.Find("[data-testid=git-commit-detail]").TextContent);
    }

    [Fact]
    public void An_empty_stash_list_says_so_in_its_section()
    {
        var (ctx, _, tree, _, _) = PageContext();
        tree.StashList.Clear();
        using var _1 = ctx;

        var cut = ctx.Render<GitPage>();

        Assert.NotNull(cut.Find("[data-testid=git-stashes] [data-testid=git-stashes-empty]"));
    }

    [Fact]
    public void The_history_filters_by_message_and_by_author_over_the_loaded_commits()
    {
        var (ctx, git, _, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        cut.Find("[data-testid=git-history-filter]").Input("LATEST");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=git-commit-row]")));
        Assert.Contains("feat: latest thing", cut.Find("[data-testid=git-commit-row]").TextContent);

        cut.Find("[data-testid=git-history-filter-kind]").Change("author");
        cut.Find("[data-testid=git-history-filter]").Input("grace");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=git-commit-row]")));
        Assert.Contains("fix: earlier thing", cut.Find("[data-testid=git-commit-row]").TextContent);

        cut.Find("[data-testid=git-history-filter]").Input(string.Empty);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=git-commit-row]").Count));
        Assert.DoesNotContain(git.CommitPathQueries, q => q is not null); // no git call for these two
    }

    [Fact]
    public void The_path_filter_asks_git_after_a_separator_and_refuses_what_is_not_a_plain_path()
    {
        var (ctx, git, _, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();

        cut.Find("[data-testid=git-history-filter-kind]").Change("path");
        foreach (var refused in new[] { "--output=x", "../outside.txt", ":(top)src", "/etc/passwd" })
        {
            cut.Find("[data-testid=git-history-filter]").Input(refused);
            cut.WaitForAssertion(() => Assert.Contains("relative to the repository root", cut.Find("[data-testid=git-history-filter-note]").TextContent));
        }

        Assert.DoesNotContain(git.CommitPathQueries, q => q is not null);

        cut.Find("[data-testid=git-history-filter]").Input("src\\One.cs");
        cut.WaitForAssertion(() => Assert.Contains("src/One.cs", git.CommitPathQueries));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=git-commit-row]")));
        Assert.Contains("fix: earlier thing", cut.Find("[data-testid=git-commit-row]").TextContent);
    }

    [Fact]
    public void The_sync_control_is_labelled_with_its_counts_and_every_icon_button_says_what_it_does()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;
        var cut = ctx.Render<GitPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-row]")));

        var sync = cut.Find("[data-testid=git-sync-menu]");
        Assert.Contains("Sync", sync.TextContent);
        Assert.Equal("Sync: 1 to push, 0 to pull", sync.GetAttribute("aria-label"));
        OpenSyncMenu(cut);
        Assert.Contains("Push 1", cut.Find("[data-testid=git-push]").TextContent);
        Assert.Contains("Pull 0", cut.Find("[data-testid=git-pull]").TextContent);
        Assert.Contains("Fetch", cut.Find("[data-testid=git-fetch]").TextContent);
        Assert.Contains("New branch", cut.Find("[data-testid=git-new-branch]").TextContent);
        Assert.Contains("Stash", cut.Find("[data-testid=git-stash-open]").TextContent);

        var unlabelled = cut.FindAll("button")
            .Where(b => b.TextContent.Trim().Length == 0
                && string.IsNullOrWhiteSpace(b.GetAttribute("title"))
                && string.IsNullOrWhiteSpace(b.GetAttribute("aria-label")))
            .Select(b => b.OuterHtml)
            .ToList();
        Assert.Empty(unlabelled);
    }

    [Fact]
    public void A_pull_request_link_opens_it_in_the_detail_pane_with_its_lifecycle_actions()
    {
        var (ctx, _, _, _, _) = PageContext();
        using var _1 = ctx;

        var cut = ctx.Render<GitPage>(p => p.Add(c => c.PrNumber, 12));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=git-detail] [data-testid=git-pr-detail]")));
        Assert.NotEmpty(cut.FindAll("[data-testid=git-pr-merge]"));
        Assert.Contains("active", cut.Find("[data-testid=git-pr-row]").GetAttribute("class"));
        // The history stays beside it.
        Assert.Equal(2, cut.FindAll("[data-testid=git-commit-row]").Count);
    }
}
