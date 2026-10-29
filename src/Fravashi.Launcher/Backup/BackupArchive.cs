using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Fravashi.Domain;

namespace Fravashi.Launcher.Backup;

/// <summary>
/// The outcome of inspecting a backup archive: either a manifest that has been verified in full, or the
/// one sentence explaining why the archive will not be restored. There is no third state and no partial
/// answer, because everything here runs BEFORE anything on disk is touched.
/// </summary>
/// <param name="Manifest">The parsed manifest when the archive checked out; null otherwise.</param>
/// <param name="Problem">Why the archive was refused; null when it checked out.</param>
public sealed record BackupInspection(BackupManifest? Manifest, string? Problem)
{
    public bool Ok => Manifest is not null && Problem is null;

    public static BackupInspection Refused(string problem) => new(null, problem);

    public static BackupInspection Accepted(BackupManifest manifest) => new(manifest, null);
}

/// <summary>
/// Reads and VERIFIES a backup archive written by the studio (backlog 79).
/// <para>
/// Every check happens here, before the restore moves a single file, because the failure that matters is
/// not "the restore did not work" but "the restore replaced a working install with a broken one". So a
/// tampered hash, an entry the manifest does not account for, a path that climbs out of the data
/// directory, and a schema version this build cannot open are all refusals with the previous state
/// untouched, not errors discovered half way through.
/// </para>
/// </summary>
public static class BackupArchive
{
    /// <summary>The manifest entry at the root of the archive.</summary>
    public const string ManifestEntryName = "manifest.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Opens <paramref name="archivePath"/>, parses its manifest, and verifies every recorded file against
    /// it. Never throws for a bad archive: a malformed zip is an answer, not an exception the caller has to
    /// translate.
    /// </summary>
    public static BackupInspection Inspect(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return BackupInspection.Refused("no archive was named.");
        }

        if (!File.Exists(archivePath))
        {
            return BackupInspection.Refused($"there is no file at '{archivePath}'.");
        }

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            return Verify(archive);
        }
        catch (InvalidDataException)
        {
            return BackupInspection.Refused("it is not a readable zip archive.");
        }
        catch (IOException ex)
        {
            return BackupInspection.Refused($"it could not be read ({ex.GetType().Name}).");
        }
        catch (UnauthorizedAccessException)
        {
            return BackupInspection.Refused("it could not be read (access denied).");
        }
    }

    private static BackupInspection Verify(ZipArchive archive)
    {
        var manifestEntry = archive.GetEntry(ManifestEntryName);
        if (manifestEntry is null)
        {
            return BackupInspection.Refused(
                $"it has no manifest.json, so it is not a {ProductName.Short} backup.");
        }

        BackupManifest? manifest;
        try
        {
            using var stream = manifestEntry.Open();
            manifest = JsonSerializer.Deserialize<BackupManifest>(stream, ReadOptions);
        }
        catch (JsonException ex)
        {
            return BackupInspection.Refused($"its manifest.json is not valid JSON ({ex.Message}).");
        }

        if (manifest is null)
        {
            return BackupInspection.Refused("its manifest.json is empty.");
        }

        if (!BackupManifest.IsProductKind(manifest.Kind))
        {
            return BackupInspection.Refused(
                $"its manifest says kind '{manifest.Kind ?? "(none)"}', not '{BackupManifest.ExpectedKind}'.");
        }

        if (manifest.Format != BackupManifest.SupportedFormat)
        {
            return BackupInspection.Refused(
                $"it is in backup format {manifest.Format}; this {ProductName.Short} understands format " +
                $"{BackupManifest.SupportedFormat}. Update {ProductName.Short} and try again.");
        }

        if (manifest.SchemaVersion > BackupManifest.KnownSchemaVersion)
        {
            return BackupInspection.Refused(
                $"it was taken by a NEWER {ProductName.Short} (database schema {manifest.SchemaVersion}; this one " +
                $"knows up to {BackupManifest.KnownSchemaVersion}). Update {ProductName.Short} first, then restore it. " +
                "Restoring it now would leave the product with a database it cannot open.");
        }

        if (manifest.Files.Count == 0)
        {
            return BackupInspection.Refused("its manifest lists no files.");
        }

        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                return BackupInspection.Refused("its manifest lists a file with no path.");
            }

            if (!IsSafeRelativePath(file.Path))
            {
                return BackupInspection.Refused(
                    $"its manifest lists '{file.Path}', which does not stay inside the data directory.");
            }

            if (!listed.Add(file.Path))
            {
                return BackupInspection.Refused($"its manifest lists '{file.Path}' twice.");
            }

            var entry = archive.GetEntry(file.Path);
            if (entry is null)
            {
                return BackupInspection.Refused($"'{file.Path}' is in the manifest but not in the archive.");
            }

            if (entry.Length != file.SizeBytes)
            {
                return BackupInspection.Refused(
                    $"'{file.Path}' is {entry.Length} bytes in the archive and {file.SizeBytes} in the manifest.");
            }

            var actual = HashOf(entry);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return BackupInspection.Refused(
                    $"'{file.Path}' does not match its recorded SHA256, so the archive is damaged or altered.");
            }
        }

        // Anything in the archive the manifest does not account for would be unpacked unverified, which is
        // exactly the hole hashing every listed file is meant to close.
        foreach (var entry in archive.Entries)
        {
            if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.Ordinal))
            {
                continue;
            }

            // Folder entries carry no content and no hash; a zip may or may not contain them.
            if (entry.FullName.EndsWith('/') && entry.Length == 0)
            {
                continue;
            }

            if (!listed.Contains(entry.FullName))
            {
                return BackupInspection.Refused(
                    $"'{entry.FullName}' is in the archive but not in the manifest, so it cannot be verified.");
            }
        }

        if (string.IsNullOrWhiteSpace(manifest.DatabaseEntry) || !listed.Contains(manifest.DatabaseEntry))
        {
            return BackupInspection.Refused("it carries no database, so there is nothing to restore.");
        }

        return BackupInspection.Accepted(manifest);
    }

    /// <summary>
    /// True when <paramref name="path"/> is a relative, forward-slash path that cannot leave the folder it
    /// is unpacked into. Checked as WRITTEN rather than after any normalization: the point is to refuse the
    /// string, not to find out where a cleverly written one would have landed.
    /// </summary>
    internal static bool IsSafeRelativePath(string path)
    {
        if (path.Length == 0 || path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        // A backslash is an ordinary filename character on Linux and a separator on Windows, so a path
        // carrying one means two different things on the two halves of this product. The writer never
        // emits one; refusing it here keeps the archive's meaning the same everywhere.
        if (path.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    private static string HashOf(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var hasher = SHA256.Create();
        return Convert.ToHexString(hasher.ComputeHash(stream)).ToLowerInvariant();
    }
}
