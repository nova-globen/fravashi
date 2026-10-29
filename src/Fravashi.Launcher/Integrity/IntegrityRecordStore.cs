using System.Text.Json;

namespace Fravashi.Launcher.Integrity;

/// <summary>
/// Writes the HMAC-signed expected-shape record into the mounted data directory
/// (<c>&lt;dataDir&gt;/integrity-record.json</c>) - the app side reads it (<c>FileIntegrityRecord</c>) to verify its
/// own running shape (F3 / ADR 0007). Mirrors <c>UpdateStateStore</c> / <c>SandboxPostureStore</c>: the launcher is
/// the sole writer, on every <c>fravashi</c> run (and re-written on status), so the record self-heals a stale copy.
/// Field names are camelCase (web JSON) and intentionally stable across versions to match the app-side reader.
/// </summary>
public sealed class IntegrityRecordStore
{
    /// <summary>The file name inside the data directory (matches the app-side reader).</summary>
    public const string FileName = "integrity-record.json";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;

    public IntegrityRecordStore(string dataDir) => _path = Path.Combine(dataDir, FileName);

    /// <summary>Signs <paramref name="record"/> with <paramref name="key"/> and writes it (whole-file write).</summary>
    public void Write(IntegrityRecord record, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(key);

        var signed = record with { Hmac = IntegrityCanonical.Sign(record, key) };
        Directory.CreateDirectory(_path[.._path.LastIndexOf(Path.DirectorySeparatorChar)]);
        File.WriteAllText(_path, JsonSerializer.Serialize(signed, Json));
    }
}
