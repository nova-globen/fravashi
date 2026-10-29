using System.Diagnostics;
using Fravashi.Infrastructure.Bridge;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Bridge;

public sealed class BridgeGitCommandRunnerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fravashi-remote-tests", Guid.NewGuid().ToString("N"));
    private HostedBridge _bridge = null!;
    private BridgeGitCommandRunner _runner = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        RunGit("init");
        RunGit("config", "user.email", "test@example.test");
        RunGit("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_root, "tracked.txt"), "content");
        _bridge = await HostedBridge.StartAsync(_root);
        _runner = new BridgeGitCommandRunner(_bridge.CreateClient());
    }

    [Fact]
    public void IsAvailable_TrueForAHostGitWorkspace_AndCached()
    {
        _runner.IsAvailable().Should().BeTrue();
        _runner.IsAvailable().Should().BeTrue();
    }

    [Fact]
    public async Task Run_ExecutesOnTheHostWorkspace_AndIgnoresEnv()
    {
        var status = await _runner.RunAsync(
            ["status", "--porcelain"],
            new Dictionary<string, string> { ["GIT_AUTHOR_NAME"] = "ignored" },
            CancellationToken.None);

        status.ExitCode.Should().Be(0);
        status.StdOut.Should().Contain("tracked.txt");
    }

    private void RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
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
    }

    public async Task DisposeAsync()
    {
        await _bridge.DisposeAsync();
        try
        {
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
