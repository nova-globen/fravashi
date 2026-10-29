using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// Strips WSL/DrvFs Unix-ownership metadata (the <c>$LXUID</c>/<c>$LXGID</c>/<c>$LXMOD</c> NTFS extended
/// attributes) from the files in the bind-mounted data directory, host-side, so the hardened non-root Podman
/// container can write them.
/// <para>
/// Why this is needed: rootless Podman on Windows bind-mounts the data dir through the Podman machine's 9p /
/// DrvFs mount. That mount <b>honours</b> a file's baked <c>$LX</c> ownership (e.g. uid 0 / mode 0644, written
/// when the studio ran as root under the earlier <c>wslc</c> runtime) but <b>cannot rewrite</b> it - a chown is
/// a silent no-op, and both the <c>:U</c> mount suffix and an entrypoint <c>chown</c> fail with EPERM. Such a
/// file appears inside the uid-1000 container as owned by <c>65534</c> (nobody) mode 0644 - read-only - so
/// SQLite fails schema creation with "attempt to write a readonly database" and the studio crash-loops. The
/// <c>--userns=keep-id</c> run arg (see <see cref="PodmanRuntime"/>) does not help either: it maps the app to
/// the host user, but these legacy files are owned by machine <c>root</c>, still outside the map.
/// </para>
/// <para>
/// The fix: rewrite each file's <b>content</b> into a freshly created NTFS file (which carries none of the
/// <c>$LX</c> attributes), then move it into place. Only the Windows file owner can do this - it can read even
/// a <c>0600</c> root-owned file because the NTFS ACL grants the owner full control regardless of the advisory
/// <c>$LX</c> mode - which is exactly why this runs in the launcher (Windows) and not in the container. After
/// the rewrite the file presents through the 9p mount with the mount's default (writable) ownership. Idempotent
/// and gated by a marker file so it runs once per data directory; must be called only while no container holds
/// the data dir open (the launcher calls it before creating or starting the container, never while running).
/// </para>
/// </summary>
public static class PodmanDataDirNormalizer
{
    /// <summary>Marker written into the data dir once normalization has run, so it does not repeat.</summary>
    public const string MarkerFileName = ".podman-ownership-normalized";

    /// <summary>True when this data dir has already been normalized (the marker is present).</summary>
    public static bool AlreadyNormalized(string dataDir) =>
        File.Exists(Path.Combine(dataDir, MarkerFileName));

    /// <summary>
    /// The durable, app-owned state on <c>/data</c> that must be writable for the studio to start and persist:
    /// the SQLite database (and its WAL/SHM sidecars) plus the ASP.NET Data Protection key ring under
    /// <c>keys/</c>. These are exactly the files the Dockerfile calls out as the container's persistent state,
    /// and the only ones that carry the wslc-era root ownership. The tool-managed subtrees (<c>home/</c>,
    /// <c>.npm-global/</c>) are deliberately left alone - the agent CLIs fill them with symlinks, sockets and
    /// temp files that must not be rewritten and are recreatable anyway.
    /// </summary>
    private const string KeyRingDirectoryName = "keys";

    /// <summary>
    /// Rewrites the durable app-owned files on <paramref name="dataDir"/> (the SQLite DB at the top level and the
    /// key ring under <c>keys/</c>) to strip their <c>$LX</c> ownership metadata, then writes the marker. Returns
    /// the number of files rewritten. A no-op returning 0 if the directory does not exist. Per-file best-effort:
    /// a symlink/reparse point is skipped (rewriting it would replace the link with its target's bytes), and a
    /// file that cannot be read/replaced is skipped rather than aborting the pass - the marker is still written
    /// so the one-time normalization does not retry forever. The marker file itself is never rewritten.
    /// </summary>
    public static int Normalize(string dataDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        if (!Directory.Exists(dataDir))
        {
            return 0;
        }

        // (a) Every top-level regular file - the SQLite DB (fravashi.db + -wal/-shm) and the launcher-written
        //     json records live here. Not recursive: the tool-managed subtrees are skipped.
        var rewritten = NormalizeFilesIn(dataDir, SearchOption.TopDirectoryOnly);

        // (b) The Data Protection key ring under keys/ (the other durable, app-owned state on /data).
        var keyRing = Path.Combine(dataDir, KeyRingDirectoryName);
        if (Directory.Exists(keyRing))
        {
            rewritten += NormalizeFilesIn(keyRing, SearchOption.AllDirectories);
        }

        File.WriteAllText(
            Path.Combine(dataDir, MarkerFileName),
            $"{ProductName.Short} normalized these data files' ownership metadata for the rootless Podman bind mount. " +
            "Safe to delete; it will be recreated on the next launch.\n");
        return rewritten;
    }

    /// <summary>Rewrites the eligible files directly in (or, per <paramref name="option"/>, under) a directory.</summary>
    private static int NormalizeFilesIn(string directory, SearchOption option)
    {
        var rewritten = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", option))
        {
            if (string.Equals(Path.GetFileName(file), MarkerFileName, StringComparison.Ordinal))
            {
                continue;
            }

            // A symlink/reparse point must not be rewritten (it would replace the link with its target's bytes).
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (TryRewriteStrippingMetadata(file))
            {
                rewritten++;
            }
        }

        return rewritten;
    }

    /// <summary>Rewrites a file best-effort; returns false (skipping it) if it cannot be read or replaced.</summary>
    private static bool TryRewriteStrippingMetadata(string path)
    {
        try
        {
            RewriteStrippingMetadata(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies a file's content into a fresh sibling (no <c>$LX</c> attributes) and moves it over the original.
    /// The move is a same-directory rename, so the replacement is atomic on NTFS; the temp is cleaned up on
    /// any failure so a partial copy never lingers.
    /// </summary>
    private static void RewriteStrippingMetadata(string path)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"File '{path}' has no parent directory.");
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.ddnorm-{Guid.NewGuid():N}");
        try
        {
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; the original file is untouched, so a leftover temp is harmless.
        }
    }
}
