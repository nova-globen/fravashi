using Fravashi.Bridge;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Bridge.Tests;

public sealed class BridgeGitServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fravashi-bridge-tests", Guid.NewGuid().ToString("N"));
    private readonly WorkspaceRegistry _registry = new();
    private readonly BridgeGitService _git;
    private readonly string _id;

    public BridgeGitServiceTests()
    {
        Directory.CreateDirectory(_root);
        _id = _registry.Register(_root).Id;
        _git = new BridgeGitService(_registry);
    }

    // Host git is a product prerequisite (decided 2026-08-01: preflight-required, not
    // bundled); the dev machine and any real host have it, so these tests run everywhere real.
    private static bool GitMissing => !BridgeGitService.IsGitAvailable();

    [Fact]
    public async Task Run_GitVersion_ReturnsExitZeroWithStdout()
    {
        if (GitMissing)
        {
            return; // xunit 2.x has no dynamic skip; a machine without git passes vacuously.
        }

        var result = await _git.RunAsync(_id, ["--version"], CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("git version");
    }

    [Fact]
    public async Task Run_IsRootedAtTheWorkspace()
    {
        if (GitMissing)
        {
            return; // xunit 2.x has no dynamic skip; a machine without git passes vacuously.
        }

        (await _git.RunAsync(_id, ["init"], CancellationToken.None)).ExitCode.Should().Be(0);
        File.WriteAllText(Path.Combine(_root, "file.txt"), "content");

        var status = await _git.RunAsync(_id, ["status", "--porcelain"], CancellationToken.None);

        status.ExitCode.Should().Be(0);
        status.Stdout.Should().Contain("file.txt");
    }

    [Fact]
    public async Task Run_EmptyArgs_Throws()
    {
        var act = () => _git.RunAsync(_id, [], CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Run_UnknownWorkspace_Throws()
    {
        var act = () => _git.RunAsync("deadbeefdeadbeef", ["--version"], CancellationToken.None);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ── Clone (plan 2c): the URL gate lives at the ENDPOINT; CloneAsync itself derives + runs. ──

    [Theory]
    [InlineData("https://forge.example.test/nova-globen/fravashi.git", true)]
    [InlineData("http://git.internal/acme/widgets", true)]
    [InlineData("ssh://git@forge.example.test/nova-globen/fravashi.git", true)]
    [InlineData("git@github.com:acme/widgets.git", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("-upload-pack=calc", false)]
    [InlineData("--mirror", false)]
    [InlineData("file:///c/somewhere", false)]
    [InlineData("ext::sh -c calc", false)]
    [InlineData(@"C:\local\path", false)]
    public void IsCloneableUrl_GatesTheTransportShapes(string url, bool expected) =>
        BridgeGitService.IsCloneableUrl(url).Should().Be(expected);

    [Theory]
    [InlineData("https://forge.example.test/nova-globen/fravashi.git", "fravashi")]
    [InlineData("git@github.com:acme/My Widgets.git", "My-Widgets")]
    [InlineData("https://host/o/repo/", "repo")]
    public void DeriveFolderName_SanitizesTheLastSegment(string url, string expected) =>
        BridgeGitService.DeriveFolderName(url).Should().Be(expected);

    [Fact]
    public async Task Clone_FromALocalBareRepo_CreatesTheFolderAndReturnsItsPath()
    {
        if (GitMissing)
        {
            return; // xunit 2.x has no dynamic skip; a machine without git passes vacuously.
        }

        var bare = SeedBareRepo("seed-repo.git");
        var parent = Path.Combine(_root, "clones");
        Directory.CreateDirectory(parent);

        var cloned = await _git.CloneAsync(bare, parent, CancellationToken.None);

        cloned.Should().Be(Path.Combine(parent, "seed-repo"));
        File.Exists(Path.Combine(cloned, "seed.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(cloned, ".git")).Should().BeTrue();
    }

    [Fact]
    public async Task Clone_IntoAnExistingFolderName_ThrowsIOException()
    {
        var parent = Path.Combine(_root, "clones2");
        Directory.CreateDirectory(Path.Combine(parent, "widgets"));

        var act = () => _git.CloneAsync("https://example.invalid/acme/widgets.git", parent, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task Clone_Failure_SurfacesGitsStderr()
    {
        if (GitMissing)
        {
            return; // xunit 2.x has no dynamic skip; a machine without git passes vacuously.
        }

        var parent = Path.Combine(_root, "clones3");
        Directory.CreateDirectory(parent);
        var missing = Path.Combine(_root, "no-such-repo.git");

        var act = () => _git.CloneAsync(missing, parent, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().NotBeNullOrWhiteSpace();
    }

    private string SeedBareRepo(string name)
    {
        var bare = Path.Combine(_root, name);
        var work = Path.Combine(_root, "seed-work");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(work);
        RunGit(bare, "init", "--bare", "-b", "main");
        RunGit(work, "init", "-b", "main");
        RunGit(work, "config", "user.name", "Test");
        RunGit(work, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(work, "seed.txt"), "seed\n");
        RunGit(work, "add", ".");
        RunGit(work, "commit", "-m", "seed");
        RunGit(work, "remote", "add", "origin", bare);
        RunGit(work, "push", "origin", "main");
        return bare;
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {process.StandardError.ReadToEnd()}");
        }
    }

    public void Dispose()
    {
        try
        {
            // git objects are read-only on Windows; clear the attribute so the temp tree deletes.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
