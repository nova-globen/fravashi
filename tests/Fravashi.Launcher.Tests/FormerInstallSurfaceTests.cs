using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Launcher.Backup;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// The smaller places the bridge release (<c>docs/plans/rename-internal-names.md</c>, phase 2) reads a former name: the
/// first-run hint marker, a backup archive from before the internal rename, the update channel's and the website's
/// former addresses, and the named primitives an old resident holds. The old names come from <see cref="FormerInstall"/>.
/// </summary>
public sealed class FormerInstallSurfaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fravashi-former-surface", Guid.NewGuid().ToString("N"));

    public FormerInstallSurfaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- the first-run hint ----------------------------------------------------------------------------------------

    [Fact]
    public void The_former_hint_marker_counts_as_the_hint_already_shown()
    {
        File.WriteAllText(Path.Combine(_dir, FormerInstall.FirstRunHintMarker), "shown");
        var said = new List<string>();

        FirstRunHint.ShowOnce(Path.Combine(_dir, "launcher.json"), said.Add);

        said.Should().BeEmpty();
        File.Exists(Path.Combine(_dir, FirstRunHint.MarkerName)).Should().BeFalse("nothing new is written for a hint already shown");
    }

    [Fact]
    public void Without_any_marker_the_hint_is_shown_once_under_the_new_name()
    {
        var said = new List<string>();

        FirstRunHint.ShowOnce(Path.Combine(_dir, "launcher.json"), said.Add);
        FirstRunHint.ShowOnce(Path.Combine(_dir, "launcher.json"), said.Add);

        said.Should().ContainSingle();
        File.Exists(Path.Combine(_dir, FirstRunHint.MarkerName)).Should().BeTrue();
        FirstRunHint.MarkerName.Should().NotBe(FormerInstall.FirstRunHintMarker);
    }

    // ---- a backup from before the rename ---------------------------------------------------------------------------

    private string FormerArchive(string databaseContent)
    {
        var archivePath = Path.Combine(_dir, "former-backup.zip");
        var bytes = Encoding.UTF8.GetBytes(databaseContent);
        var manifest = new
        {
            format = BackupManifest.SupportedFormat,
            kind = FormerInstall.BackupKind,
            schemaVersion = 48,
            databaseEntry = FormerInstall.DatabaseFileName,
            files = new[]
            {
                new
                {
                    path = FormerInstall.DatabaseFileName,
                    sizeBytes = (long)bytes.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                },
            },
        };
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry(BackupArchive.ManifestEntryName).Open()))
        {
            writer.Write(JsonSerializer.Serialize(manifest));
        }

        using (var stream = archive.CreateEntry(FormerInstall.DatabaseFileName).Open())
        {
            stream.Write(bytes);
        }

        return archivePath;
    }

    [Fact]
    public void An_archive_taken_before_the_rename_is_accepted_and_restores_as_the_current_database()
    {
        var archive = FormerArchive("restored-rows");
        var data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, BackupRestore.DatabaseFileName), "the-current");
        File.WriteAllText(Path.Combine(data, BackupRestore.FormerDatabaseFileName), "a-stale-former-copy");

        var inspection = BackupArchive.Inspect(archive);
        inspection.Ok.Should().BeTrue(inspection.Problem);

        var swap = BackupRestore.Apply(archive, inspection.Manifest!, data, "20261027-120000");

        swap.Ok.Should().BeTrue(swap.Problem);
        File.ReadAllText(Path.Combine(data, BackupRestore.DatabaseFileName)).Should().Be("restored-rows");
        File.Exists(Path.Combine(data, BackupRestore.FormerDatabaseFileName))
            .Should().BeFalse("a database under the former name is moved aside, never left to be opened instead");
        var kept = Path.Combine(data, BackupRestore.BackupsFolderName, BackupRestore.PreRestorePrefix + "20261027-120000");
        File.ReadAllText(Path.Combine(kept, BackupRestore.DatabaseFileName)).Should().Be("the-current");
        File.ReadAllText(Path.Combine(kept, BackupRestore.FormerDatabaseFileName)).Should().Be("a-stale-former-copy");
    }

    [Fact]
    public void Both_kinds_are_the_product_s_and_nothing_else_is()
    {
        BackupManifest.IsProductKind(BackupManifest.ExpectedKind).Should().BeTrue();
        BackupManifest.IsProductKind(FormerInstall.BackupKind).Should().BeTrue();
        BackupManifest.IsProductKind("someone-else-backup").Should().BeFalse();
        BackupManifest.IsProductKind(null).Should().BeFalse();
    }

    // ---- an old executable must refuse the new image -----------------------------------------------------------------

    private static string RepositoryFile(params string[] relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fravashi.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine([directory!.FullName, .. relative]));
    }

    [Fact]
    public void The_image_names_a_variant_no_install_has_under_the_label_an_old_executable_reads()
    {
        // v0.9.102's ProductUpdater.RefuseLabels reads the variant from the former label and refuses an image whose
        // variant is not the install's, before the running studio is touched; with no such label it refuses nothing. So
        // the image must carry it, with a value that is none of the release's variants, or an old `fravashi update` would
        // run this image under the old container and environment names.
        var label = RepositoryFile("Dockerfile").Split('\n')
            .Select(l => l.Trim())
            .SingleOrDefault(l => l.StartsWith($"LABEL {FormerInstall.ImageVariantLabel}=", StringComparison.Ordinal));
        label.Should().NotBeNull("the Dockerfile must set the former variant label");
        var value = label![(label.IndexOf('=') + 1)..].Trim('"');

        using var variants = JsonDocument.Parse(RepositoryFile("setup", "release", "variants.json"));
        var names = variants.RootElement.GetProperty("variants").EnumerateArray().Select(v => v.GetProperty("name").GetString()).ToList();
        names.Should().Contain("full");
        names.Should().NotContain(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
        value.Should().NotContain("$", "a build argument would make it the build's own variant");
    }

    // ---- an old resident -------------------------------------------------------------------------------------------

    [Fact]
    public void The_former_primitives_are_asked_beside_the_current_ones()
    {
        TrayControlNames.FormerSingleInstanceMutex.Should().Be(FormerInstall.SingleInstanceMutex)
            .And.NotBe(TrayControlNames.SingleInstanceMutex);
        TrayControlNames.FormerExitEvent.Should().Be(FormerInstall.ExitEvent).And.NotBe(TrayControlNames.ExitEvent);
    }

    [Fact]
    public void A_named_mutex_and_event_are_found_by_name_and_a_missing_one_is_not()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = @"Local\Fravashi.Tests." + Guid.NewGuid().ToString("N");
        WindowsTrayControl.MutexExists(name).Should().BeFalse();
        WindowsTrayControl.Signal(name + ".Event").Should().BeFalse();

        using var mutex = new Mutex(initiallyOwned: true, name, out _);
        using var exit = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".Event");

        WindowsTrayControl.MutexExists(name).Should().BeTrue();
        WindowsTrayControl.Signal(name + ".Event").Should().BeTrue();
        exit.WaitOne(0).Should().BeTrue("the signal reached the event");
    }
}
