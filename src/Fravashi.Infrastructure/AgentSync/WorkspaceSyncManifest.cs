using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fravashi.Infrastructure.AgentSync;

/// <summary>What the manifest knows about one path at its last sync.</summary>
/// <param name="HostStamp">The HOST last-write stamp the copy was last synced at.</param>
/// <param name="Hash">The content's SHA-256 (hex) as last synced, in either direction; null in a manifest from before
/// it was recorded.</param>
/// <param name="CopySize">The copy file's size right after that sync.</param>
/// <param name="CopyTicks">The copy file's last-write time (UTC ticks) right after that sync.</param>
public sealed record ManifestEntry(DateTimeOffset HostStamp, string? Hash = null, long? CopySize = null, long? CopyTicks = null);

/// <summary>
/// The per-workspace sync manifest (plan 2d): relative path -> the HOST last-write stamp the copy
/// was last synced at. It is the freshness precondition for write-backs and the skip test for
/// re-pulls. Persisted BESIDE the copy (never inside it, so it is never mistaken for content).
/// <para>
/// Since backlog 147 it also records what the copy held at that sync (a content hash, and the copy file's size and
/// time so an untouched file is recognised without reading it). Write-backs wait in memory, so a container that stops
/// between an agent's edit and its write-back used to leave the edit behind for good: with this, a start can tell a
/// file the agent changed from one it did not.
/// </para>
/// </summary>
public sealed class WorkspaceSyncManifest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, ManifestEntry> _entries = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly Lock _saveLock = new();

    public WorkspaceSyncManifest(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                Load(File.ReadAllText(path));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt/unreadable manifest just means a full re-pull; never fatal.
        }
    }

    private void Load(string text)
    {
        using var document = JsonDocument.Parse(text);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            // A manifest written before backlog 147 holds a bare stamp per path; it still reads, with nothing known
            // about the copy, which is exactly how such a path behaved before.
            _entries[property.Name] = property.Value.ValueKind == JsonValueKind.Object
                ? property.Value.Deserialize<ManifestEntry>(Json)!
                : new ManifestEntry(property.Value.GetDateTimeOffset());
        }
    }

    public DateTimeOffset? Get(string relativePath) =>
        _entries.TryGetValue(relativePath, out var entry) ? entry.HostStamp : null;

    /// <summary>Everything recorded about the path, or null.</summary>
    public ManifestEntry? Entry(string relativePath) => _entries.TryGetValue(relativePath, out var entry) ? entry : null;

    /// <summary>Records a sync of the path: the host's stamp, and what the copy file at <paramref name="copyFile"/> now holds.</summary>
    public void Set(string relativePath, DateTimeOffset hostStamp, byte[] content, string copyFile)
    {
        long? size = null;
        long? ticks = null;
        try
        {
            var info = new FileInfo(copyFile);
            if (info.Exists)
            {
                size = info.Length;
                ticks = info.LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the file's facts the next start hashes it instead; nothing is lost.
        }

        _entries[relativePath] = new ManifestEntry(hostStamp, HashOf(content), size, ticks);
    }

    public void Remove(string relativePath) => _entries.TryRemove(relativePath, out _);

    public IReadOnlyCollection<string> Paths => [.. _entries.Keys];

    public int Count => _entries.Count;

    /// <summary>The hash this manifest records content by.</summary>
    public static string HashOf(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    /// <summary>
    /// Whether the copy file still holds what was synced: true or false when that can be told, null when the manifest
    /// has no record of the content (a path synced before backlog 147, or never synced).
    /// </summary>
    public bool? CopyUnchanged(string relativePath, string copyFile)
    {
        if (Entry(relativePath) is not { Hash: { } hash } entry)
        {
            return null;
        }

        var info = new FileInfo(copyFile);
        if (!info.Exists)
        {
            return false;
        }

        if (entry.CopySize == info.Length && entry.CopyTicks == info.LastWriteTimeUtc.Ticks)
        {
            return true;
        }

        try
        {
            return string.Equals(HashOf(File.ReadAllBytes(copyFile)), hash, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // cannot be told; the path behaves as it did before backlog 147
        }
    }

    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(new Dictionary<string, ManifestEntry>(_entries), Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Lost stamps only cost a re-pull on the next start.
            }
        }
    }
}
