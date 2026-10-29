using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Git;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Fravashi.Infrastructure.Tests.Git;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions.Tools;

/// <summary>
/// Backlog 150, over a REAL git repository with a real bare remote: an agent reads status, history, one
/// commit, a diff and blame; commits what it names with the host identity; pushes; and every text it hands
/// git that could become an option on the host is refused before git sees it. Only a real git proves what
/// the history says afterwards, which is what the tools are for.
/// </summary>
public sealed class GitToolsTests : IDisposable
{
    private readonly string _base =
        Path.Combine(Path.GetTempPath(), "fravashi-git-tools-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _repo;
    private readonly string _origin;
    private readonly SqliteTestContext _store = new();
    private readonly AgentSessionId _session = AgentSessionId.New();
    private readonly SqliteSessionChangedFiles _changed;
    private readonly SessionGit _git;
    private readonly SessionGitWork _work;

    public GitToolsTests()
    {
        _repo = Path.Combine(_base, "repo");
        _origin = Path.Combine(_base, "origin.git");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_origin);
        Git(_origin, "init", "--bare", "-b", "main");
        Git(_repo, "init", "-b", "main");
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Test");
        Git(_repo, "remote", "add", "origin", _origin);
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "one\ntwo\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "Initial commit");
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "one\ntwo\nthree\n");
        Git(_repo, "commit", "-am", "feat(seed): add a third line");

        var sessions = new SqliteSessionStore(_store.Factory);
        sessions.Save(new SessionRecord(
            _session, "w1", AgentTool.Claude, "t", null, AgentSessionState.Running, DateTimeOffset.UtcNow, null));
        _changed = new SqliteSessionChangedFiles(_store.Factory, TimeProvider.System);

        _git = new SessionGit(sessions, SessionGitWorkTests.WorkspaceGitOver(_repo, "w1"));
        _work = new SessionGitWork(
            _git,
            _changed,
            new SqliteSessionBranches(_store.Factory),
            new SqliteSessionWorkItems(_store.Factory, TimeProvider.System),
            new LocalTrackerResolver(_store.Factory, TimeProvider.System),
            TimeProvider.System);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---- reads --------------------------------------------------------------------------------

    [Fact]
    public async Task Status_reports_the_branch_and_the_working_tree_and_is_labelled_as_other_authored()
    {
        File.WriteAllText(Path.Combine(_repo, "new.txt"), "x\n");

        var answer = await Call(new GitStatusTool(_git), "{}");

        answer.IsError.Should().BeFalse(answer.Text);
        using var doc = JsonDocument.Parse(answer.Text);
        doc.RootElement.GetProperty("trust").GetString().Should().Be("other-authored-content");
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("branch").GetString().Should().Be("main");
        data.GetProperty("unstaged").EnumerateArray().Select(e => e.GetProperty("path").GetString())
            .Should().Contain("new.txt");
        data.GetProperty("pending_write_backs").GetInt32().Should().Be(0, "no copy is synced in this test");
    }

    [Fact]
    public async Task Log_lists_commits_newest_first_and_can_be_limited_to_a_path()
    {
        var answer = await Call(new GitLogTool(_git), """{"max": 10}""");

        var commits = Data(answer).GetProperty("commits").EnumerateArray().ToList();
        commits.Should().HaveCount(2);
        commits[0].GetProperty("subject").GetString().Should().Be("feat(seed): add a third line");
        commits[1].GetProperty("subject").GetString().Should().Be("Initial commit");
        commits[0].GetProperty("author").GetString().Should().Be("Test");

        var none = await Call(new GitLogTool(_git), """{"path": "does-not-exist.txt"}""");
        Data(none).GetProperty("count").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Show_gives_the_commit_its_files_and_its_diff_or_a_file_as_it_was_then()
    {
        var answer = await Call(new GitShowTool(_git), """{"sha": "HEAD"}""");

        var data = Data(answer);
        data.GetProperty("commit").GetProperty("subject").GetString().Should().Be("feat(seed): add a third line");
        data.GetProperty("files").EnumerateArray().Single().GetProperty("path").GetString().Should().Be("seed.txt");
        data.GetProperty("diff").GetString().Should().Contain("+three");

        var before = await Call(new GitShowTool(_git), """{"sha": "HEAD~1", "file": "seed.txt"}""");
        Data(before).GetProperty("content").GetString().Should().Be("one\ntwo\n");

        var unknown = await Call(new GitShowTool(_git), """{"sha": "no-such-ref"}""");
        unknown.IsError.Should().BeTrue();
        unknown.Text.Should().Contain("unknown_commit");
    }

    [Fact]
    public async Task Diff_covers_the_working_tree_by_default_and_two_references_when_asked()
    {
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "one\ntwo\nthree\nfour\n");

        var working = await Call(new GitDiffTool(_git), "{}");
        var data = Data(working);
        data.GetProperty("mode").GetString().Should().Be("working-tree");
        data.GetProperty("diff").GetString().Should().Contain("+four");
        data.GetProperty("files").EnumerateArray().Single().GetProperty("kind").GetString().Should().Be("modified");

        var between = await Call(new GitDiffTool(_git), """{"base": "HEAD~1", "head": "HEAD"}""");
        Data(between).GetProperty("diff").GetString().Should().Contain("+three").And.NotContain("+four");
    }

    [Fact]
    public async Task Blame_names_the_commit_behind_each_line()
    {
        var answer = await Call(new GitBlameTool(_git), """{"path": "seed.txt", "from": 1, "to": 3}""");

        var lines = Data(answer).GetProperty("lines").EnumerateArray().ToList();
        lines.Should().HaveCount(3);
        lines.Select(l => l.GetProperty("line").GetInt32()).Should().Equal(1, 2, 3);
        lines[0].GetProperty("subject").GetString().Should().Be("Initial commit");
        lines[2].GetProperty("subject").GetString().Should().Be("feat(seed): add a third line");
        lines[2].GetProperty("text").GetString().Should().Be("three");
        lines[2].GetProperty("author").GetString().Should().Be("Test");
        lines[0].GetProperty("sha").GetString().Should().NotBe(lines[2].GetProperty("sha").GetString());
    }

    // ---- what git is never handed ------------------------------------------------------------

    [Theory]
    [InlineData("--output=/tmp/owned")]
    [InlineData("-p")]
    [InlineData("main extra")]
    public async Task A_reference_that_would_read_as_an_option_or_two_words_is_refused_before_git_sees_it(string reference)
    {
        var log = await Call(new GitLogTool(_git), JsonSerializer.Serialize(new { reference }));
        log.IsError.Should().BeTrue();
        log.Text.Should().Contain("bad_reference");

        var show = await Call(new GitShowTool(_git), JsonSerializer.Serialize(new { sha = reference }));
        show.Text.Should().Contain("bad_reference");

        var diff = await Call(new GitDiffTool(_git), JsonSerializer.Serialize(new { @base = reference }));
        diff.Text.Should().Contain("bad_reference");
    }

    [Theory]
    [InlineData("-Lsecret")]
    [InlineData("../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    public async Task A_path_that_is_not_relative_to_the_repository_is_refused(string path)
    {
        var blame = await Call(new GitBlameTool(_git), JsonSerializer.Serialize(new { path }));
        blame.IsError.Should().BeTrue();
        blame.Text.Should().Contain("bad_path");

        var commit = await Call(new GitCommitTool(_git, _work),
            JsonSerializer.Serialize(new { message = "chore: x", paths = new[] { path } }));
        commit.Text.Should().Contain("bad_path");
    }

    [Fact]
    public void The_argument_rules_read_as_written()
    {
        GitToolArguments.IsReference("HEAD~2").Should().BeTrue();
        GitToolArguments.IsReference("origin/feature/x").Should().BeTrue();
        GitToolArguments.IsReference("v0.9.36").Should().BeTrue();
        GitToolArguments.IsReference("--all").Should().BeFalse();
        GitToolArguments.IsReference("a b").Should().BeFalse();
        GitToolArguments.IsReference("a\tb").Should().BeFalse();

        GitToolArguments.NormalizePath(@"src\Program.cs").Should().Be("src/Program.cs");
        GitToolArguments.NormalizePath("./src//a/").Should().Be("src/a");
        GitToolArguments.NormalizePath("src/../../x").Should().BeNull();
        GitToolArguments.NormalizePath("-x").Should().BeNull();
        GitToolArguments.NormalizePath("/abs").Should().BeNull();

        GitToolArguments.IsTagName("v1.2.3").Should().BeTrue();
        GitToolArguments.IsTagName("release/v1").Should().BeTrue();
        GitToolArguments.IsTagName("v1:refs/heads/main").Should().BeFalse("a colon would make the pushed word a refspec");
        GitToolArguments.IsTagName("v1:main").Should().BeFalse();
        GitToolArguments.IsTagName("+v1").Should().BeFalse();
        GitToolArguments.IsTagName("v1^{}").Should().BeFalse();
        GitToolArguments.IsTagName("v1~1").Should().BeFalse();
        GitToolArguments.IsTagName("a..b").Should().BeFalse();
        GitToolArguments.IsTagName("v1.lock").Should().BeFalse();
        GitToolArguments.IsTagName("-v1").Should().BeFalse();
        GitToolArguments.IsTagName(".v1").Should().BeFalse("a path component cannot start with a dot");
        GitToolArguments.IsTagName("v1/.x").Should().BeFalse("no slash-separated component starts with a dot");
        GitToolArguments.IsTagName("a.lock/b").Should().BeFalse("no slash-separated component ends with .lock");
    }

    // ---- writes -------------------------------------------------------------------------------

    [Fact]
    public async Task Commit_records_the_named_files_with_the_host_identity_and_push_reaches_origin()
    {
        File.WriteAllText(Path.Combine(_repo, "mine.txt"), "by the agent\n");
        File.WriteAllText(Path.Combine(_repo, "theirs.txt"), "somebody else\n");

        var commit = await Call(new GitCommitTool(_git, _work),
            """{"message": "feat(mine): add my file\n\nWhy it exists.", "paths": ["mine.txt"]}""");

        commit.IsError.Should().BeFalse(commit.Text);
        using var doc = JsonDocument.Parse(commit.Text);
        doc.RootElement.GetProperty("changed").GetString().Should().Be("committed");
        var sha = doc.RootElement.GetProperty("result").GetProperty("sha").GetString()!;
        doc.RootElement.GetProperty("result").GetProperty("files").GetInt32().Should().Be(1);

        Git(_repo, "show", "--name-only", "--format=", sha).Trim().Should().Be("mine.txt");
        Git(_repo, "log", "-1", "--format=%an", sha).Trim().Should().Be("Test");
        Git(_repo, "log", "-1", "--format=%B", sha).Should().Contain("feat(mine): add my file")
            .And.Contain("Fravashi agent session");
        Git(_repo, "status", "--porcelain").Should().Contain("theirs.txt", "only what was named went in");

        var push = await Call(new GitPushTool(_git), "{}");

        push.IsError.Should().BeFalse(push.Text);
        Git(_origin, "log", "-1", "--format=%H", "main").Trim().Should().Be(sha);
        Git(_repo, "rev-parse", "--abbrev-ref", "@{u}").Trim().Should().Be("origin/main");
    }

    /// <summary>
    /// #318: git_commit shares attempt_commit's path, so it holds a message to the same rule: one that opens with
    /// the studio's own trailer is refused in words, and nothing is committed.
    /// </summary>
    [Fact]
    public async Task Commit_refuses_a_message_that_opens_with_the_studios_trailer_and_commits_nothing()
    {
        File.WriteAllText(Path.Combine(_repo, "mine.txt"), "by the agent\n");
        var head = Git(_repo, "rev-parse", "HEAD").Trim();

        var commit = await Call(new GitCommitTool(_git, _work), JsonSerializer.Serialize(new
        {
            message = "Work items:\n- #24 Add a changelog\n\nProduced by a Fravashi agent session (703debf8).",
            paths = new[] { "mine.txt" },
        }));

        commit.IsError.Should().BeTrue();
        commit.Text.Should().Contain("subject_required").And.Contain("subject");
        Git(_repo, "rev-parse", "HEAD").Trim().Should().Be(head, "nothing was committed");
        Git(_repo, "diff", "--cached", "--name-only").Trim().Should().BeEmpty("nothing was even staged");
    }

    [Fact]
    public async Task A_tag_is_annotated_on_head_and_pushed_by_its_full_ref_and_a_bad_name_never_reaches_git()
    {
        var tagged = await Call(new GitTagTool(_git), """{"name": "v9.9.9", "message": "release nine"}""");

        tagged.IsError.Should().BeFalse(tagged.Text);
        var sha = JsonDocument.Parse(tagged.Text).RootElement.GetProperty("result").GetProperty("sha").GetString();
        sha.Should().Be(Git(_repo, "rev-parse", "HEAD").Trim());
        Git(_repo, "cat-file", "-t", "v9.9.9").Trim().Should().Be("tag", "annotated, not lightweight");

        var pushed = await Call(new GitPushTool(_git), """{"tag": "v9.9.9"}""");
        pushed.IsError.Should().BeFalse(pushed.Text);
        Git(_origin, "tag", "-l").Should().Contain("v9.9.9");

        var bad = await Call(new GitTagTool(_git), """{"name": "-f", "message": "x"}""");
        bad.Text.Should().Contain("bad_tag_name");
        var badPush = await Call(new GitPushTool(_git), """{"tag": "--force"}""");
        badPush.Text.Should().Contain("bad_tag_name");
    }

    [Theory]
    [InlineData("v1:refs/heads/main")]
    [InlineData("v1:main")]
    [InlineData("+v1")]
    [InlineData("v1^{}")]
    [InlineData("v1~1")]
    [InlineData("a..b")]
    [InlineData("v1.lock")]
    [InlineData("-v1")]
    public async Task Git_push_refuses_a_tag_whose_text_would_mean_something_other_than_a_tag_before_any_push_runs(string tag)
    {
        // Real local tags named "v1" and "a", so a colon case like "v1:refs/heads/main" would really resolve a
        // src and move origin's main if the refusal below did not stop it first (backlog 285's measured attack).
        Git(_repo, "tag", "v1");
        Git(_repo, "tag", "a");
        var before = Git(_origin, "for-each-ref").Trim();

        var pushed = await Call(new GitPushTool(_git), JsonSerializer.Serialize(new { tag }));

        pushed.IsError.Should().BeTrue();
        pushed.Text.Should().Contain("bad_tag_name").And.Contain("ref-name rules");
        Git(_origin, "for-each-ref").Trim().Should().Be(before, "a refused tag never reaches git, so origin is untouched");
    }

    [Fact]
    public async Task Git_push_refuses_a_tag_that_does_not_exist_locally_before_any_push_runs()
    {
        var before = Git(_origin, "for-each-ref").Trim();

        var pushed = await Call(new GitPushTool(_git), """{"tag": "v9.9.7"}""");

        pushed.IsError.Should().BeTrue();
        pushed.Text.Should().Contain("no_such_tag");
        Git(_origin, "for-each-ref").Trim().Should().Be(before);
    }

    [Fact]
    public async Task Git_push_still_pushes_real_tag_names_with_a_slash()
    {
        Git(_repo, "tag", "-a", "release/v1", "-m", "release");

        var pushed = await Call(new GitPushTool(_git), """{"tag": "release/v1"}""");

        pushed.IsError.Should().BeFalse(pushed.Text);
        Git(_origin, "tag", "-l").Should().Contain("release/v1");
    }

    [Fact]
    public async Task Commit_without_paths_takes_what_the_session_was_approved_to_change_and_says_when_that_is_nothing()
    {
        var nothing = await Call(new GitCommitTool(_git, _work), """{"message": "chore: nothing"}""");
        nothing.IsError.Should().BeTrue();
        nothing.Text.Should().Contain("nothing_to_commit");

        File.WriteAllText(Path.Combine(_repo, "approved.txt"), "approved\n");
        File.WriteAllText(Path.Combine(_repo, "other.txt"), "not approved\n");
        _changed.Record(_session, "approved.txt");

        var commit = await Call(new GitCommitTool(_git, _work), """{"message": "feat: the approved file"}""");
        commit.IsError.Should().BeFalse(commit.Text);
        var sha = JsonDocument.Parse(commit.Text).RootElement.GetProperty("result").GetProperty("sha").GetString()!;
        Git(_repo, "show", "--name-only", "--format=", sha).Trim().Should().Be("approved.txt");
    }

    [Fact]
    public async Task A_session_whose_workspace_has_no_repository_is_told_so_by_every_tool()
    {
        var sessions = new SqliteSessionStore(_store.Factory);
        var stranger = AgentSessionId.New();
        sessions.Save(new SessionRecord(
            stranger, "elsewhere", AgentTool.Claude, "t", null, AgentSessionState.Running, DateTimeOffset.UtcNow, null));
        var git = new SessionGit(sessions, SessionGitWorkTests.WorkspaceGitOver(_repo, "w1"));

        var answer = await new GitLogTool(git).InvokeAsync(new StudioToolCall(
            stranger, GitLogTool.ToolName, JsonDocument.Parse("{}").RootElement, "k", ProjectId.Default));

        answer.IsError.Should().BeTrue();
        answer.Text.Should().Contain("no_repository");
    }

    [Fact]
    public async Task A_session_on_a_host_with_no_git_is_told_git_is_not_installed_in_words()
    {
        // Backlog 266: the real bridge with a git program name that does not exist stands in for a host with no git.
        await using var bridge = await Fravashi.Infrastructure.Tests.Bridge.HostedBridge.StartAsync(
            _repo, gitExecutable: "fravashi-no-such-git-" + Guid.NewGuid().ToString("N"));
        var workspaces = SessionGitWorkTests.WorkspaceGitOver(
            _repo, "w1", new Fravashi.Infrastructure.Bridge.BridgeGitCommandRunner(bridge.CreateClient()));
        var sessions = new SqliteSessionStore(_store.Factory);
        var session = AgentSessionId.New();
        sessions.Save(new SessionRecord(
            session, "w1", AgentTool.Claude, "t", null, AgentSessionState.Running, DateTimeOffset.UtcNow, null));

        var answer = await new GitLogTool(new SessionGit(sessions, workspaces)).InvokeAsync(new StudioToolCall(
            session, GitLogTool.ToolName, JsonDocument.Parse("{}").RootElement, "k", ProjectId.Default));

        answer.IsError.Should().BeTrue();
        answer.Text.Should().Contain("git_not_installed").And.Contain("git is not installed on this machine");
        answer.Text.Should().NotContain("500");
    }

    // ---- the network (backlog 231) -------------------------------------------------------------

    [Fact]
    public async Task A_push_to_an_origin_whose_name_does_not_resolve_says_so_in_the_pages_words_and_is_safe_to_retry()
    {
        // Real git, real failure: origin names a host that cannot exist (RFC 6761 reserves .invalid).
        Git(_repo, "remote", "set-url", "origin", "https://nonexistent-host.invalid/r.git");
        Git(_repo, "tag", "-a", "v9.9.8", "-m", "release");

        var branch = await Call(new GitPushTool(_git), "{}");
        var tag = await Call(new GitPushTool(_git), """{"tag": "v9.9.8"}""");

        foreach (var (answer, field) in new[] { (branch, "branch"), (tag, "tag") })
        {
            answer.IsError.Should().BeTrue();
            using var doc = JsonDocument.Parse(answer.Text);
            doc.RootElement.GetProperty("refused").GetString().Should().Be("network_unreachable");
            doc.RootElement.GetProperty("retry_safe").GetBoolean().Should().BeTrue();
            var error = doc.RootElement.GetProperty("errors")[0];
            error.GetProperty("field").GetString().Should().Be(field);
            error.GetProperty("message").GetString().Should().Be("Could not reach origin (name not resolved). Nothing changed.");
        }
    }

    [Fact]
    public async Task A_push_of_unknown_outcome_is_never_called_failed_and_says_how_to_check_without_pushing()
    {
        var unknown = new GitSyncOutcome(false,
            "The push was sent but no answer came back (the connection was reset), and origin cannot be asked right now (name not resolved). It may have arrived: check origin before pushing again.")
        {
            Failure = GitSyncFailure.OutcomeUnknown,
            Pending = new GitPendingPush("refs/heads/main", "abc"),
        };

        var answer = GitPushTool.RemoteFailed(unknown, "branch");

        using var doc = JsonDocument.Parse(answer.Text);
        doc.RootElement.GetProperty("refused").GetString().Should().Be("push_outcome_unknown");
        doc.RootElement.GetProperty("retry_safe").GetBoolean().Should().BeFalse();
        var message = doc.RootElement.GetProperty("errors")[0].GetProperty("message").GetString();
        message.Should().StartWith(unknown.Error).And.Contain("check set to true").And.NotContain("failed");
    }

    [Theory]
    [InlineData(GitRefusal.Rejected, "push_rejected")]
    [InlineData(GitRefusal.HookDeclined, "push_rejected")]
    [InlineData(GitRefusal.Authentication, "authentication_failed")]
    [InlineData(GitRefusal.Permission, "permission_denied")]
    public void A_refusal_has_its_own_code_and_is_not_safe_to_retry(GitRefusal refusal, string code)
    {
        var answer = GitPushTool.RemoteFailed(
            new GitSyncOutcome(false, "Origin refused it.") { Failure = GitSyncFailure.Refused, Refusal = refusal }, "branch");

        using var doc = JsonDocument.Parse(answer.Text);
        doc.RootElement.GetProperty("refused").GetString().Should().Be(code);
        doc.RootElement.GetProperty("retry_safe").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Check_asks_origin_and_pushes_nothing()
    {
        var notYet = await Call(new GitPushTool(_git), """{"check": true}""");

        notYet.IsError.Should().BeTrue("origin does not have main yet");
        JsonDocument.Parse(notYet.Text).RootElement.GetProperty("retry_safe").GetBoolean().Should().BeTrue();
        Git(_origin, "branch", "--list").Should().BeEmpty("checking pushes nothing");

        (await Call(new GitPushTool(_git), "{}")).IsError.Should().BeFalse();
        var arrived = await Call(new GitPushTool(_git), """{"check": true}""");

        arrived.IsError.Should().BeFalse(arrived.Text);
        JsonDocument.Parse(arrived.Text).RootElement.GetProperty("changed").GetString().Should().Be("pushed");
    }

    // ---- helpers ------------------------------------------------------------------------------

    private Task<StudioToolResult> Call(IStudioTool tool, string arguments) =>
        tool.InvokeAsync(new StudioToolCall(
            _session, tool.Name, JsonDocument.Parse(arguments).RootElement, Guid.NewGuid().ToString("N"), ProjectId.Default));

    private static JsonElement Data(StudioToolResult answer)
    {
        answer.IsError.Should().BeFalse(answer.Text);
        return JsonDocument.Parse(answer.Text).RootElement.GetProperty("data");
    }

    private static string Git(string directory, params string[] args)
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
        var output = stdout.GetAwaiter().GetResult();
        process.ExitCode.Should().Be(0, "git {0} failed: {1}", string.Join(' ', args), stderr.GetAwaiter().GetResult());
        return output;
    }
}
