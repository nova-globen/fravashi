using System.Text.Json;

namespace Fravashi.Bridge;

/// <summary>
/// Durable backing for <see cref="WorkspaceRegistry"/>'s grant list, written beside
/// <c>bridge.token</c> and <c>launcher.json</c> in the per-install config directory (backlog 14).
/// <para>
/// Only PATHS are stored, never ids: the id is a hash of the canonical path, so re-registering a
/// remembered folder recomputes exactly the id the studio's recents already hold, and a stored id
/// can never go stale against a changed hashing rule.
/// </para>
/// <para>
/// Persisting the grant list does not widen the trust boundary. Anyone holding the bridge token can
/// already POST /bridge/workspaces for any folder that exists on the host, and every remembered path
/// is re-validated through the normal registration path on load (a folder that is positively gone is
/// pruned, never re-granted; one that is merely out of reach stays remembered but ungranted).
/// </para>
/// </summary>
public sealed class WorkspaceRegistryStore(string filePath)
{
    public const string FileName = "workspaces.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath => filePath;

    /// <summary>
    /// The remembered folder paths, or an empty list when nothing was ever written. A missing,
    /// unreadable or corrupt file means "nothing remembered": startup must never fail on it.
    /// </summary>
    public IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return [];
            }

            var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(filePath), Options);
            return paths is null ? [] : [.. paths.Where(p => !string.IsNullOrWhiteSpace(p))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// Replaces the remembered list. Best effort: an unpersistable store still serves the current
    /// process, it simply forgets across a restart.
    /// </summary>
    public void Save(IReadOnlyList<string> paths)
    {
        try
        {
            if (Path.GetDirectoryName(filePath) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(filePath, JsonSerializer.Serialize(paths, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException)
        {
        }
    }
}
