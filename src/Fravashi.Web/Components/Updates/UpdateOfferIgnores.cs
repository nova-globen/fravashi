using Fravashi.Application.Persistence;
using Fravashi.Application.Updates;

namespace Fravashi.Web.Components.Updates;

/// <summary>
/// The Updates card's Ignore (work item #358): an offer a person ignored stays hidden until a NEWER one is offered.
/// What was ignored is the offered version, kept in the settings table under one key per kind of update, so it
/// survives a restart and an upgrade of the studio, and nothing else is stored.
/// </summary>
public static class UpdateOfferIgnores
{
    /// <summary>The settings-table key prefix; one key per kind of update follows it.</summary>
    public const string KeyPrefix = "Updates:Ignored:";

    /// <summary>The kind for Fravashi itself (the signed update channel's offer).</summary>
    public const string Studio = "studio";

    /// <summary>The kind for one agent CLI, by its runtime's name.</summary>
    public static string Cli(string tool) => "cli-" + tool.ToLowerInvariant();

    /// <summary>The version ignored for this kind, or null.</summary>
    public static string? Ignored(IConfigurationStore? store, string kind) =>
        store?.Get(KeyPrefix + kind) is { Length: > 0 } version ? version : null;

    /// <summary>Records that <paramref name="version"/> of this kind is ignored.</summary>
    public static void Ignore(IConfigurationStore? store, string kind, string version) =>
        store?.Set(KeyPrefix + kind, version);

    /// <summary>Forgets every ignored offer, so each shows again.</summary>
    public static void ShowAll(IConfigurationStore? store, IEnumerable<string> kinds)
    {
        if (store is null)
        {
            return;
        }

        foreach (var kind in kinds)
        {
            store.Remove(KeyPrefix + kind);
        }
    }

    /// <summary>
    /// True when <paramref name="offered"/> is hidden by what was ignored: it is the version ignored, or not newer than
    /// it. A version that cannot be read as one is hidden only when it is exactly the one ignored, so an offer is never
    /// silently swallowed by a comparison that could not be made.
    /// </summary>
    public static bool Hides(string? ignored, string? offered)
    {
        if (ignored is null || string.IsNullOrWhiteSpace(offered))
        {
            return false;
        }

        if (string.Equals(ignored, offered, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return SemVer.TryParse(ignored, out _) && SemVer.TryParse(offered, out _) && !SemVer.IsNewer(offered, ignored);
    }
}
