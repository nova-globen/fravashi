using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Fravashi.Web.Services;

/// <summary>
/// Enumerates the real FluentUI icon set from the installed
/// <c>Microsoft.FluentUI.AspNetCore.Components.Icons</c> package by reflection, so the skill icon picker offers
/// the genuine icons (not a hand-maintained subset that would drift). We use the <b>Regular · Size20</b> family
/// - one canonical entry per icon name, the size the picker renders - which is ~2,900 icons in v4.14.3. An icon
/// renders from its <see cref="Icon.Content"/> (the raw SVG path); <see cref="TryGetSvg"/> returns it wrapped in
/// an <c>&lt;svg&gt;</c> so callers can drop it straight into markup.
/// </summary>
public sealed class FluentIconCatalog
{
    private readonly IReadOnlyList<string> _names;
    private readonly IReadOnlyDictionary<string, Type> _types;
    private readonly ConcurrentDictionary<string, string> _svgCache = new(StringComparer.OrdinalIgnoreCase);

    public FluentIconCatalog()
    {
        // Anchor on a known icon to get the Regular assembly, then take every instantiable Size20 icon type.
        var anchor = typeof(Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size20.Home);
        var assembly = anchor.Assembly;
        var declaringSize20 = anchor.DeclaringType!; // Icons.Regular.Size20 (the container class)

        Type[] all;
        try
        {
            all = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            all = ex.Types.Where(t => t is not null).ToArray()!;
        }

        var map = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in all)
        {
            if (type is { IsClass: true, IsAbstract: false }
                && type.DeclaringType == declaringSize20
                && typeof(Icon).IsAssignableFrom(type)
                && type.GetConstructor(Type.EmptyTypes) is not null)
            {
                map[type.Name] = type;
            }
        }

        _types = map;
        _names = map.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Every icon name, sorted. The full installed Regular·Size20 set.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>The number of icons in the catalog.</summary>
    public int Count => _names.Count;

    /// <summary>True when the named icon exists in the catalog.</summary>
    public bool Contains(string name) => !string.IsNullOrEmpty(name) && _types.ContainsKey(name);

    /// <summary>
    /// The names matching <paramref name="query"/> (case-insensitive substring; prefix matches rank first),
    /// capped at <paramref name="limit"/>. An empty query returns the first <paramref name="limit"/> names.
    /// </summary>
    public IReadOnlyList<string> Search(string? query, int limit = 60)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _names.Take(limit).ToArray();
        }

        var term = query.Trim();
        return _names
            .Where(n => n.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    /// <summary>
    /// The named icon rendered as a complete <c>&lt;svg&gt;</c> element (viewBox sized to the icon), or false
    /// when the name is unknown. Cached per name.
    /// </summary>
    public bool TryGetSvg(string? name, out string svg)
    {
        svg = string.Empty;
        if (string.IsNullOrEmpty(name) || !_types.TryGetValue(name, out var type))
        {
            return false;
        }

        svg = _svgCache.GetOrAdd(name, _ =>
        {
            var icon = (Icon)Activator.CreateInstance(type)!;
            var side = (int)icon.Size;
            return $"<svg viewBox=\"0 0 {side} {side}\" width=\"100%\" height=\"100%\" " +
                   $"fill=\"currentColor\" xmlns=\"http://www.w3.org/2000/svg\">{icon.Content}</svg>";
        });
        return true;
    }
}
