using System.Text.Json;

namespace Fravashi.Launcher.Releases;

/// <summary>
/// Writes the evaluated product-update state into the mounted data directory (<c>&lt;dataDir&gt;/update-state.json</c>)
/// - the SINGLE SOURCE OF TRUTH the app reads to drive the critical lock + the available banner (ADR 0006 / D03-2).
/// The launcher is the only actor that can update the image, so it is the only writer: on every <c>fravashi</c> run,
/// <c>fravashi status</c>, and <c>fravashi update</c> it fetches the manifest, verifies its signature against the release key
/// (backlog 242), runs <see cref="UpdateCheck"/>, and records the outcome here. Only a VERIFIED manifest is ever
/// recorded, which is what <see cref="UpdateStateFile.ManifestVerified"/> says to the app. The app never re-derives criticality; it reads this file. Field names are camelCase
/// to match the app-side reader's web-JSON conventions and are intentionally stable across versions.
/// <para>
/// Only a check that READ a manifest writes here (backlog 230). A check that could not reach the channel is
/// recorded in <see cref="UpdateCheckFailureStore"/> instead and leaves this file exactly as it was, because the
/// app reads anything but "Critical" as unlocked.
/// </para>
/// </summary>
public sealed class UpdateStateStore
{
    /// <summary>The file name inside the data directory (matches the app-side reader).</summary>
    public const string FileName = "update-state.json";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly TimeProvider _clock;

    public UpdateStateStore(string dataDir, TimeProvider? clock = null)
    {
        _path = Path.Combine(dataDir, FileName);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Writes the evaluated state whole (through a temp file and a move), so the app never reads half of it.</summary>
    public void Write(UpdateAvailability availability, string currentVersion)
    {
        if (availability.Kind == UpdateKind.Unknown)
        {
            // Backlog 230: "could not check" written here read as "nothing pending" and lifted the critical lock.
            throw new ArgumentException("A check that could not reach the update channel is never recorded as the update state.", nameof(availability));
        }

        var state = new UpdateStateFile(
            availability.Kind.ToString(),
            currentVersion,
            availability.TargetVersion,
            availability.ReleaseNotesUrl,
            _clock.GetUtcNow(),
            ManifestVerified: true);

        WholeFile.Write(_path, JsonSerializer.Serialize(state, Json));
    }

    /// <summary>What the last successful check recorded, or null when nothing (readable) was.</summary>
    public UpdateStateFile? TryRead()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<UpdateStateFile>(File.ReadAllText(_path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The on-disk shape shared with the app-side reader (<c>FileProductUpdateStatus</c>).</summary>
    /// <param name="ManifestVerified">
    /// True when this was recorded from a manifest whose signature verified (backlog 242); always so from a build that
    /// verifies, and absent (false) in a file an older build wrote.
    /// </param>
    public sealed record UpdateStateFile(
        string Kind,
        string? CurrentVersion,
        string? TargetVersion,
        string? ReleaseNotesUrl,
        DateTimeOffset CheckedUtc,
        bool ManifestVerified = false);
}

/// <summary>
/// Records that the update channel could not be checked (backlog 230) in <c>&lt;dataDir&gt;/update-check.json</c>,
/// beside the update state and never in it: when it started failing, when it was last tried, and why. A successful
/// check deletes it. The app reads it to say "could not check for updates since 14:02 (name not resolved)".
/// </summary>
public sealed class UpdateCheckFailureStore
{
    /// <summary>The file name inside the data directory (matches the app-side reader).</summary>
    public const string FileName = "update-check.json";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;

    public UpdateCheckFailureStore(string dataDir) => _path = Path.Combine(dataDir, FileName);

    /// <summary>
    /// Records a failed check at <paramref name="now"/>, keeping when the failing began. <paramref name="signature"/> is
    /// set when the manifest arrived and was refused for its signature (backlog 242).
    /// </summary>
    public UpdateCheckFailure Record(string reason, DateTimeOffset now, ManifestSignature? signature = null)
    {
        var since = TryRead()?.FailingSinceUtc ?? now;
        var file = new UpdateCheckFile(since, now, reason, SignatureWord(signature));
        WholeFile.Write(_path, JsonSerializer.Serialize(file, Json));
        return new UpdateCheckFailure(reason, since, now) { Signature = signature };
    }

    /// <summary>The stable word the app reads for a refused signature: <c>unsigned</c> or <c>bad</c>.</summary>
    public static string? SignatureWord(ManifestSignature? signature) => signature switch
    {
        ManifestSignature.Unsigned => "unsigned",
        ManifestSignature.BadSignature => "bad",
        _ => null,
    };

    /// <summary>Forgets the failures: the channel answered.</summary>
    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private UpdateCheckFile? TryRead()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<UpdateCheckFile>(File.ReadAllText(_path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The on-disk shape shared with the app-side reader (<c>FileProductUpdateStatus</c>).</summary>
    /// <param name="Signature">
    /// <c>unsigned</c> or <c>bad</c> when the manifest arrived and was refused for its signature (backlog 242), else null.
    /// </param>
    public sealed record UpdateCheckFile(DateTimeOffset FailingSinceUtc, DateTimeOffset LastTriedUtc, string Reason, string? Signature = null);
}

/// <summary>Writes a file whole: to a temp file beside it, then moved over it, so a reader sees the old or the new.</summary>
internal static class WholeFile
{
    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
