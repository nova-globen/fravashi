using System.Diagnostics;
using System.IO.Compression;
using AwesomeAssertions;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Repository;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fravashi.Infrastructure.Tests;

/// <summary>Delete-to-trash (ruling 2026-07-26): archive first, delete second, never lose data.</summary>
public sealed class RepositoryTrashTests : IDisposable
{
    private readonly string _base;
    private readonly string _repo;
    private readonly string _dataDir;
    private readonly RepositoryTrash _trash;

    public RepositoryTrashTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "fravashi-trash-" + Guid.NewGuid().ToString("N")[..8]);
        _repo = Path.Combine(_base, "repo");
        _dataDir = Path.Combine(_base, "data");
        Directory.CreateDirectory(Path.Combine(_repo, "docs"));
        Directory.CreateDirectory(_dataDir);
        RunGit(_repo, "init", "-b", "feature/cleanup");
        RunGit(_repo, "config", "user.name", "Test");
        RunGit(_repo, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "seed");
        RunGit(_repo, "add", ".");
        RunGit(_repo, "commit", "-m", "seed"); // an unborn branch would name the archive HEAD
        File.WriteAllText(Path.Combine(_repo, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(_repo, "doomed.txt"), "doomed content");
        File.WriteAllText(Path.Combine(_repo, "docs", "inner.md"), "inner");

        var options = Options.Create(new RepositoryReaderOptions { Root = _repo });
        _trash = new RepositoryTrash(
            options,
            new RepositoryWriteGate(),
            new GitCliService(new LocalGitCommandRunner(options)),
            new DataDirectory(_dataDir));
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

    [Fact]
    public async Task Deleting_zips_files_and_folders_into_the_dated_branch_named_archive_then_removes_them()
    {
        var result = await _trash.DeleteToTrashAsync(["doomed.txt", "docs"]);

        result.Succeeded.Should().BeTrue(result.Error);
        result.ZipFileName.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}_\d{4}_\[feature-cleanup\]\.zip$");

        File.Exists(Path.Combine(_repo, "doomed.txt")).Should().BeFalse();
        Directory.Exists(Path.Combine(_repo, "docs")).Should().BeFalse();
        File.Exists(Path.Combine(_repo, "keep.txt")).Should().BeTrue();

        var zipPath = Path.Combine(_dataDir, "trash", result.ZipFileName!);
        File.Exists(zipPath).Should().BeTrue();
        using var zip = ZipFile.OpenRead(zipPath);
        zip.Entries.Select(e => e.FullName).Should().Contain(["doomed.txt", "docs/inner.md"]);
        using var reader = new StreamReader(zip.GetEntry("doomed.txt")!.Open());
        reader.ReadToEnd().Should().Be("doomed content");
    }

    [Fact]
    public async Task A_second_delete_in_the_same_minute_gets_a_suffixed_archive()
    {
        File.WriteAllText(Path.Combine(_repo, "one.txt"), "1");
        File.WriteAllText(Path.Combine(_repo, "two.txt"), "2");

        var first = await _trash.DeleteToTrashAsync(["one.txt"]);
        var second = await _trash.DeleteToTrashAsync(["two.txt"]);

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue();
        second.ZipFileName.Should().NotBe(first.ZipFileName);
    }

    [Fact]
    public async Task Escaping_paths_and_the_root_itself_are_refused()
    {
        (await _trash.DeleteToTrashAsync(["../outside.txt"])).Succeeded.Should().BeFalse();
        (await _trash.DeleteToTrashAsync([""])).Succeeded.Should().BeFalse();
        File.Exists(Path.Combine(_repo, "keep.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Restore_brings_the_deleted_files_and_folders_back_from_the_zip()
    {
        var deleted = await _trash.DeleteToTrashAsync(["doomed.txt", "docs"]);
        deleted.Succeeded.Should().BeTrue(deleted.Error);

        var restored = await _trash.RestoreAsync(deleted.ZipFileName!);

        restored.Succeeded.Should().BeTrue(restored.Error);
        File.ReadAllText(Path.Combine(_repo, "doomed.txt")).Should().Be("doomed content");
        File.Exists(Path.Combine(_repo, "docs", "inner.md")).Should().BeTrue();
    }

    [Fact]
    public async Task Restore_refuses_to_overwrite_a_file_that_exists_again()
    {
        var deleted = await _trash.DeleteToTrashAsync(["doomed.txt"]);
        File.WriteAllText(Path.Combine(_repo, "doomed.txt"), "recreated since");

        var restored = await _trash.RestoreAsync(deleted.ZipFileName!);

        restored.Succeeded.Should().BeFalse();
        restored.Error.Should().Contain("already exists");
        File.ReadAllText(Path.Combine(_repo, "doomed.txt")).Should().Be("recreated since");
    }

    [Fact]
    public async Task Restore_of_a_missing_archive_fails_honestly()
    {
        (await _trash.RestoreAsync("no-such.zip")).Succeeded.Should().BeFalse();
        (await _trash.RestoreAsync("..\\..\\evil.zip")).Succeeded.Should().BeFalse();
    }
}
