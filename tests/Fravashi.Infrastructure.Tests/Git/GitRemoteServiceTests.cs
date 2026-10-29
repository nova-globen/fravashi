using System.Diagnostics;
using AwesomeAssertions;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Repository;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fravashi.Infrastructure.Tests;

/// <summary>
/// Exercises fetch/pull/push against a REAL local bare origin (file transport, no credentials), so
/// the full network plumbing runs without touching any actual remote.
/// </summary>
public sealed class GitRemoteServiceTests : IDisposable
{
    private readonly string _base;
    private readonly string _work;
    private readonly string _origin;

    public GitRemoteServiceTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "fravashi-remote-" + Guid.NewGuid().ToString("N")[..8]);
        _origin = Path.Combine(_base, "origin.git");
        _work = Path.Combine(_base, "work");
        Directory.CreateDirectory(_origin);
        Directory.CreateDirectory(_work);
        RunGit(_origin, "init", "--bare", "-b", "main");
        RunGit(_work, "init", "-b", "main");
        RunGit(_work, "config", "user.name", "Test");
        RunGit(_work, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(_work, "a.txt"), "one\n");
        RunGit(_work, "add", ".");
        RunGit(_work, "commit", "-m", "seed");
        RunGit(_work, "remote", "add", "origin", _origin);
        RunGit(_work, "push", "-u", "origin", "main");
    }

    public void Dispose()
    {
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
            // Temp cleanup only.
        }
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
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

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {process.StandardError.ReadToEnd()}");
        }
    }

    private static string GitOut(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    private sealed class EmptyConnections : IProviderConnectionStore
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

    private sealed class EmptySecrets : ISecretStore
    {
        public void Set(string reference, string plaintext)
        {
        }

        public string? Get(string reference) => null;

        public void Remove(string reference)
        {
        }
    }

    private sealed class EmptySettings : IConfigurationStore
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

    private sealed class EmptyHosts : IGitHostStore
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

    private static GitHostAccess EmptyAccess() =>
        new(new EmptyHosts(), new EmptySecrets(), new EmptyConnections(), new EmptySettings());

    private GitRemoteService Service() =>
        new(new LocalGitCommandRunner(Options.Create(new RepositoryReaderOptions { Root = _work })),
            new RepositoryWriteGate(),
            EmptyAccess());

    [Fact]
    public async Task Remotes_are_listed_with_their_urls()
    {
        var remotes = await Service().GetRemotesAsync();

        remotes.Should().ContainSingle(r => r.Name == "origin");
        remotes[0].FetchUrl.Should().Contain("origin.git");
    }

    [Fact]
    public async Task Push_sends_local_commits_to_origin()
    {
        File.WriteAllText(Path.Combine(_work, "b.txt"), "two\n");
        RunGit(_work, "add", ".");
        RunGit(_work, "commit", "-m", "second");

        var outcome = await Service().PushAsync();

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        GitOut(_origin, "log", "-1", "--format=%s").Should().Be("second");
    }

    [Fact]
    public async Task Fetch_and_ff_pull_bring_origin_commits_in()
    {
        // A second clone pushes a commit; the work repo must fetch and fast-forward onto it.
        var other = Path.Combine(_base, "other");
        RunGit(_base, "clone", _origin, other);
        RunGit(other, "config", "user.name", "Other");
        RunGit(other, "config", "user.email", "other@example.com");
        File.WriteAllText(Path.Combine(other, "c.txt"), "three\n");
        RunGit(other, "add", ".");
        RunGit(other, "commit", "-m", "from other");
        RunGit(other, "push", "origin", "main");

        var service = Service();
        (await service.FetchAsync()).Succeeded.Should().BeTrue();
        var pull = await service.PullAsync();

        pull.Succeeded.Should().BeTrue(pull.Error);
        File.Exists(Path.Combine(_work, "c.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task A_diverged_branch_never_merges_silently()
    {
        // Divergence: other pushes X while work commits Y locally.
        var other = Path.Combine(_base, "other2");
        RunGit(_base, "clone", _origin, other);
        RunGit(other, "config", "user.name", "Other");
        RunGit(other, "config", "user.email", "other@example.com");
        File.WriteAllText(Path.Combine(other, "x.txt"), "x\n");
        RunGit(other, "add", ".");
        RunGit(other, "commit", "-m", "remote side");
        RunGit(other, "push", "origin", "main");

        File.WriteAllText(Path.Combine(_work, "y.txt"), "y\n");
        RunGit(_work, "add", ".");
        RunGit(_work, "commit", "-m", "local side");

        var pull = await Service().PullAsync();

        pull.Succeeded.Should().BeFalse();
        pull.Error.Should().Contain("nothing is merged silently");
        GitOut(_work, "log", "-1", "--format=%s").Should().Be("local side");
    }
}
