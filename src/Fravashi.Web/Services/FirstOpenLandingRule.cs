using System.Text.Json;

namespace Fravashi.Web.Services;

/// <summary>
/// When a window on a folder that is not set up lands on the setup (item #373, section 5): on the window's first load,
/// whatever page that load asked for, and on the start dialog's "open this folder" (<c>/code?ws=</c>) for a folder the
/// window has not loaded before. Never on a link followed inside a window that is already open, and never on the setup
/// itself. What the window has loaded is kept in its sessionStorage under <see cref="StorageKey"/>: a JSON array of the
/// folder ids, present (even empty) once the window has loaded any page.
/// </summary>
public static class FirstOpenLandingRule
{
    /// <summary>The window's sessionStorage key.</summary>
    public const string StorageKey = "fravashi.window.folders";

    /// <summary>True when the window lands on the setup instead of <paramref name="path"/>.</summary>
    /// <param name="visited">The folders this window has loaded before; null when this is its first load.</param>
    /// <param name="folderId">The folder this load is for.</param>
    /// <param name="path">The page asked for, without its query, starting with a slash.</param>
    public static bool ShouldLand(IReadOnlyCollection<string>? visited, string folderId, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(folderId);
        ArgumentNullException.ThrowIfNull(path);
        if (IsSetupPath(path))
        {
            return false;
        }

        if (visited is null)
        {
            return true;
        }

        if (visited.Contains(folderId, StringComparer.Ordinal))
        {
            return false;
        }

        // The window was open on another folder, or on none: only "open this folder" is a first open; any other page is a
        // link the person followed (a session row on Home, an item in Waiting for you) and goes where it points.
        return path.TrimEnd('/') == "/code";
    }

    /// <summary>True for the setup's own pages.</summary>
    public static bool IsSetupPath(string path) =>
        path.Equals("/setup", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/setup/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The window's stored list, or null when it has none (or it cannot be read).</summary>
    public static IReadOnlyCollection<string>? Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(stored) is { } ids ? ids.Where(i => !string.IsNullOrEmpty(i)).ToArray() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The list to store after this load: what was there, with this load's folder.</summary>
    public static string Remember(IReadOnlyCollection<string>? visited, string? folderId)
    {
        var ids = new List<string>(visited ?? []);
        if (folderId is { Length: > 0 } && !ids.Contains(folderId, StringComparer.Ordinal))
        {
            ids.Add(folderId);
        }

        return JsonSerializer.Serialize(ids);
    }

    /// <summary>
    /// The setup, told the page the window was loaded on so its "Not now" goes there (without the window's <c>ws</c>,
    /// which the window adds to every load itself).
    /// </summary>
    /// <param name="baseRelative">The loaded address relative to the studio's base, as NavigationManager gives it.</param>
    public static string SetupTarget(string baseRelative)
    {
        ArgumentNullException.ThrowIfNull(baseRelative);
        var path = "/" + baseRelative.Split('?', '#')[0];
        return path.Length > 1 && !IsSetupPath(path)
            ? $"/setup?from={Uri.EscapeDataString(path)}"
            : "/setup";
    }
}
