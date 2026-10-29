using System.Diagnostics;
using Fravashi.Infrastructure.AgentSync;
using Fravashi.Infrastructure.Tests.Bridge;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentSync;

/// <summary>
/// Backlog 161: what the agent's copy leaves out is what the HOST's git ignores, by every source it honours, and not
/// only the repository's root <c>.gitignore</c>. The host here is a real repository behind the real bridge, and the
/// person's own excludes file is set through <c>core.excludesFile</c> in the repository's config, which is the same
/// setting a global one is read from; the test never touches the machine's real global config.
/// </summary>
public sealed class HostGitIgnoreTests : IAsyncLifetime
{
    private readonly string _base = Path.Combine(
        Path.GetTempPath(), "fravashi-host-ignore-tests", Guid.NewGuid().ToString("N"));
    private string _host = null!;
    private string _copyRoot = null!;
    private HostedBridge _bridge = null!;

    public async Task InitializeAsync()
    {
        _host = Path.Combine(_base, "host");
        _copyRoot = Path.Combine(_base, "copies");
        Directory.CreateDirectory(_host);
        Directory.CreateDirectory(_copyRoot);

        Git("init");
        var excludes = Path.Combine(_base, "person-ignores");
        File.WriteAllText(excludes, ".env*\nsettings.local.json\n*.log\n");
        Git("config", "core.excludesFile", excludes);

        Write("src/Program.cs", "class P { }\n");
        Write(".gitignore", "node_modules/\n");
        Write(".env", "TOKEN=from-the-person\n");
        Write(".claude/settings.local.json", "{ \"outputStyle\": \"mine\" }\n");
        Write(".claude/settings.json", "{ }\n");
        Write("tools/.gitignore", "out/\n");
        Write("tools/out/built.dll", "x\n");
        Write("tools/run.sh", "echo\n");
        _bridge = await HostedBridge.StartAsync(_host);
    }

    public async Task DisposeAsync()
    {
        await _bridge.DisposeAsync();
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task A_file_the_person_ignores_everywhere_is_not_copied_into_the_agent_copy()
    {
        await using var session = await SessionAsync();
        await session.StartAsync(CancellationToken.None);

        File.Exists(CopyPath("src/Program.cs")).Should().BeTrue();
        File.Exists(CopyPath(".claude/settings.json")).Should().BeTrue("a file nobody ignores still syncs");
        File.Exists(CopyPath("tools/run.sh")).Should().BeTrue();
        File.Exists(CopyPath(".env")).Should().BeFalse("the person's excludes file ignores it");
        File.Exists(CopyPath(".claude/settings.local.json")).Should().BeFalse("the person's excludes file ignores it");
        File.Exists(CopyPath("tools/out/built.dll")).Should().BeFalse("a nested .gitignore ignores it");
    }

    [Fact]
    public async Task A_file_copied_under_the_old_rules_leaves_the_copy_at_the_next_start_and_the_host_keeps_it()
    {
        await using (var old = OldRulesSession())
        {
            await old.StartAsync(CancellationToken.None);
            File.Exists(CopyPath(".env")).Should().BeTrue("the old rules read the root .gitignore alone");
        }

        await using var session = await SessionAsync();
        await session.StartAsync(CancellationToken.None);

        File.Exists(CopyPath(".env")).Should().BeFalse();
        File.Exists(CopyPath(".claude/settings.local.json")).Should().BeFalse();
        File.ReadAllText(Path.Combine(_host, ".env")).Should().Be("TOKEN=from-the-person\n", "only the copy is pruned");
        session.Status.HostWinsConflicts.Should().Be(0, "a file leaving the copy by a rule is not a conflict");
    }

    [Fact]
    public async Task A_file_the_agent_creates_that_the_host_ignores_never_reaches_the_host_folder()
    {
        await using var session = await SessionAsync();
        await session.StartAsync(CancellationToken.None);

        Directory.CreateDirectory(CopyPath("logs"));
        Directory.CreateDirectory(CopyPath("tools/out")); // never pulled: the host ignores the whole folder
        await File.WriteAllTextAsync(CopyPath(".env.local"), "SECRET=agent\n");
        await File.WriteAllTextAsync(CopyPath("logs/run.log"), "x\n");
        await File.WriteAllTextAsync(CopyPath("tools/out/new.dll"), "x\n");

        // The ignored files were written first and the write-back loop is sequential, so once this marker is on the
        // host the pass that handled them is over. Not a delay.
        await File.WriteAllTextAsync(CopyPath("visible.txt"), "x\n");
        await WaitUntilAsync(() => File.Exists(Path.Combine(_host, "visible.txt")));
        await File.WriteAllTextAsync(CopyPath("visible-2.txt"), "x\n");
        await WaitUntilAsync(() => File.Exists(Path.Combine(_host, "visible-2.txt")));

        File.Exists(Path.Combine(_host, ".env.local")).Should().BeFalse();
        File.Exists(Path.Combine(_host, "logs", "run.log")).Should().BeFalse();
        File.Exists(Path.Combine(_host, "tools", "out", "new.dll")).Should().BeFalse();
        File.Exists(CopyPath(".env.local")).Should().BeTrue("an ignored file stays in the copy; the studio deletes nothing an agent wrote");
    }

    [Fact]
    public async Task A_file_created_on_the_host_that_the_host_ignores_is_not_copied()
    {
        await using var session = await SessionAsync();
        await session.StartAsync(CancellationToken.None);
        await WaitForLiveHostWatchAsync();

        Write(".env.production", "KEY=host\n");
        Write("build.log", "x\n");
        Write("src/Added.cs", "class A { }\n");

        await WaitUntilAsync(() => File.Exists(CopyPath("src/Added.cs")));
        Write("src/Added2.cs", "class B { }\n");
        await WaitUntilAsync(() => File.Exists(CopyPath("src/Added2.cs")));

        File.Exists(CopyPath(".env.production")).Should().BeFalse();
        File.Exists(CopyPath("build.log")).Should().BeFalse();
    }

    [Fact]
    public async Task The_host_answers_for_a_folder_that_does_not_exist_yet_and_for_what_is_under_it()
    {
        var host = await HostGitIgnore.LoadAsync(_bridge.CreateClient(), NullLogger.Instance, CancellationToken.None);

        host.Answers.Should().BeTrue();
        host.Lists(".env").Should().BeTrue();
        host.Lists("tools/out").Should().BeTrue("a wholly ignored folder is listed once, and answers as a folder too");
        host.Lists("tools/out/built.dll").Should().BeTrue();
        host.Lists("src/Program.cs").Should().BeFalse();

        (await host.IgnoresAsync("tools/fresh/out/a.dll", CancellationToken.None)).Should().BeTrue();
        (await host.IgnoresAsync("src/New.cs", CancellationToken.None)).Should().BeFalse();
        (await host.IgnoresAsync("-starts-with-a-hyphen.log", CancellationToken.None)).Should().BeTrue();
        (await host.IgnoresAsync("a \"quoted\" name.log", CancellationToken.None)).Should().BeTrue();
    }

    // A folder that is not a repository, and a host whose git cannot answer at all, are told apart by what git SAYS,
    // so these two are driven through a client whose git answers as scripted and everything else is the real bridge.
    // A real folder cannot stand in for the first: CI keeps its temp folder inside the job's own checkout, so any folder
    // made there IS in a repository and git rightly answers for it (runs 247 and 249, 2026-09-29). A workspace nested in
    // a larger repository following that repository's rules is correct behaviour, not the case under test.
    [Theory]
    [InlineData(128, "fatal: not a git repository (or any of the parent directories): .git\n")] // measured on the host, 2026-09-27
    [InlineData(1, "git: 'ls-files' is not a git command.\n")]
    public async Task A_host_whose_git_gives_no_rules_leaves_the_copy_to_the_repositorys_gitignore(int exitCode, string stderr)
    {
        var client = new ScriptedGitClient(_bridge, new Fravashi.Infrastructure.Bridge.BridgeGitResultDto(exitCode, string.Empty, stderr));

        var host = await HostGitIgnore.LoadAsync(client, NullLogger.Instance, CancellationToken.None);

        host.Answers.Should().BeFalse();
        (await host.IgnoresAsync(".env", CancellationToken.None)).Should().BeFalse();
        await using var session = new WorkspaceSyncSession(
            client, _bridge.WorkspaceId, _copyRoot, WorkspaceSyncIgnore.FromGitignoreText("node_modules/\n", host));
        await session.StartAsync(CancellationToken.None);
        File.Exists(CopyPath("src/Program.cs")).Should().BeTrue("the copy still works");
        File.Exists(CopyPath(".env")).Should().BeTrue("with no host rules the root .gitignore alone decides, as before");
    }

    /// <summary>The real bridge client with one change: every git call answers <paramref name="answer"/>.</summary>
    private sealed class ScriptedGitClient(HostedBridge bridge, Fravashi.Infrastructure.Bridge.BridgeGitResultDto answer)
        : Fravashi.Infrastructure.Bridge.BridgeClient(new HttpClient(), bridge.CreateContext())
    {
        public override Task<Fravashi.Infrastructure.Bridge.BridgeGitResultDto> GitAsync(
            IReadOnlyList<string> args, CancellationToken ct) => Task.FromResult(answer);
    }

    [Theory]
    [InlineData("plain/path.txt", "plain/path.txt")]
    [InlineData("\"a \\\"quoted\\\" name.log\"", "a \"quoted\" name.log")]
    [InlineData("\"tab\\there\"", "tab\there")]
    [InlineData("\"back\\\\slash\"", "back\\slash")]
    [InlineData("\"caf\\303\\251.log\"", "café.log")]
    public void Unquote_reads_back_what_git_printed(string printed, string expected) =>
        HostGitIgnore.Unquote(printed).Should().Be(expected);

    private async Task<WorkspaceSyncSession> SessionAsync()
    {
        var client = _bridge.CreateClient();
        var host = await HostGitIgnore.LoadAsync(client, NullLogger.Instance, CancellationToken.None);
        return new WorkspaceSyncSession(
            client, _bridge.WorkspaceId, _copyRoot, WorkspaceSyncIgnore.FromGitignoreText("node_modules/\n", host));
    }

    private WorkspaceSyncSession OldRulesSession() => new(
        _bridge.CreateClient(), _bridge.WorkspaceId, _copyRoot, WorkspaceSyncIgnore.FromGitignoreText("node_modules/\n"));

    private async Task WaitForLiveHostWatchAsync()
    {
        var sentinel = Path.Combine(_host, "src", "__watcharm__.probe");
        await WaitUntilAsync(() =>
        {
            File.WriteAllText(sentinel, Guid.NewGuid().ToString("N"));
            return File.Exists(CopyPath("src/__watcharm__.probe"));
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50); // poll interval inside this wait helper, not an arm-window guess (backlog 107)
        }

        condition().Should().BeTrue("the condition should hold within {0}ms", timeoutMs);
    }

    private string CopyPath(string relative) => Path.Combine(
        _copyRoot, _bridge.WorkspaceId, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_host, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _host,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        process.ExitCode.Should().Be(0, "git {0} should succeed: {1}", string.Join(' ', args), process.StandardError.ReadToEnd());
    }
}
