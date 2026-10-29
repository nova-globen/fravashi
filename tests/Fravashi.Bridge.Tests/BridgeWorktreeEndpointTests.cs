using System.Net;
using System.Net.Http.Json;
using Fravashi.Bridge;
using Microsoft.AspNetCore.Builder;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Bridge.Tests;

/// <summary>Endpoint-level coverage for the worktree routes (backlog 306); the behaviour itself is
/// <see cref="BridgeWorktreeServiceTests"/>'s job.</summary>
public sealed class BridgeWorktreeEndpointTests : IAsyncLifetime
{
    private const string Token = "integration-test-token";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fravashi-bridge-tests", Guid.NewGuid().ToString("N"));

    private readonly WorkspaceRegistry _registry = new();

    private WebApplication? _app;
    private HttpClient _authorized = null!;
    private string _repoRoot = null!;
    private string _id = "";

    private static bool GitMissing => !BridgeGitService.IsGitAvailable();

    public async Task InitializeAsync()
    {
        _repoRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repoRoot);
        if (!GitMissing)
        {
            RunGit(_repoRoot, "init", "-b", "main");
            RunGit(_repoRoot, "config", "user.name", "Test");
            RunGit(_repoRoot, "config", "user.email", "test@example.com");
            File.WriteAllText(Path.Combine(_repoRoot, "seed.txt"), "seed\n");
            RunGit(_repoRoot, "add", ".");
            RunGit(_repoRoot, "commit", "-m", "seed");
        }

        _id = _registry.Register(_repoRoot).Id;

        _app = BridgeApplication.Create(new BridgeServerOptions(Token, ["http://127.0.0.1:0"]), _registry);
        await _app.StartAsync();

        _authorized = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        _authorized.DefaultRequestHeaders.Add(BridgeApplication.TokenHeader, Token);
    }

    [Fact]
    public async Task CreateWorktree_RegistersItAndListsIt()
    {
        if (GitMissing)
        {
            return; // xunit 2.x has no dynamic skip; a machine without git passes vacuously.
        }

        var response = await _authorized.PostAsJsonAsync(
            $"/bridge/ws/{_id}/worktrees", new { item = 306, baseBranch = "main" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var worktree = await response.Content.ReadFromJsonAsync<BridgeWorktree>();
        worktree!.Branch.Should().Be("fravashi/worktree/item-306");

        var all = await _authorized.GetFromJsonAsync<List<BridgeWorkspace>>("/bridge/workspaces");
        all!.Select(w => w.Id).Should().Contain(worktree.Workspace.Id);
    }

    [Fact]
    public async Task CreateWorktree_OnAForeignExistingFolder_Is409()
    {
        if (GitMissing)
        {
            return; // xunit 2.x has no dynamic skip; a machine without git passes vacuously.
        }

        var target = Path.Combine(_root, "repo" + BridgeWorktreeService.ContainerSuffix, "306");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "not-ours.txt"), "a person's own folder");

        var response = await _authorized.PostAsJsonAsync(
            $"/bridge/ws/{_id}/worktrees", new { item = 306, baseBranch = "main" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PruneWorktree_UnknownItem_Is404()
    {
        if (GitMissing)
        {
            return; // xunit 2.x has no dynamic skip; a machine without git passes vacuously.
        }

        (await _authorized.DeleteAsync($"/bridge/ws/{_id}/worktrees/999")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PruneWorktree_ADirtyOne_Is409AndSurvives()
    {
        if (GitMissing)
        {
            return;
        }

        var created = await _authorized.PostAsJsonAsync(
            $"/bridge/ws/{_id}/worktrees", new { item = 306, baseBranch = "main" });
        var worktree = (await created.Content.ReadFromJsonAsync<BridgeWorktree>())!;
        File.WriteAllText(Path.Combine(worktree.Workspace.RootPath, "seed.txt"), "changed");

        var response = await _authorized.DeleteAsync($"/bridge/ws/{_id}/worktrees/306");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Directory.Exists(worktree.Workspace.RootPath).Should().BeTrue();
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

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _authorized?.Dispose();

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
