using System.Text.Json;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;

namespace Fravashi.Application.Mirroring;

/// <summary>
/// Reads the board status mapping onboarding stored (backlog 47). It has been written to the settings row
/// <c>Board:StatusMapping</c> since item 37 and read by nothing, so a board that followed a provider had no
/// way to know what the provider's own status names meant.
/// <para>
/// A row that is missing, blank or unreadable is NOT an error: it means nobody mapped anything, and the map
/// falls back to the shipped catalog and the provider's own category. An install can sync before anybody
/// opens the mapping step, which is the common case.
/// </para>
/// </summary>
public static class StoredStatusMapping
{
    /// <summary>The settings row onboarding writes, named here so both ends agree on the spelling.</summary>
    public const string Key = "Board:StatusMapping";

    /// <summary>The map for what is stored now.</summary>
    public static ProviderStatusMap Read(IConfigurationStore configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return ProviderStatusMap.From(Parse(configuration.Get(Key)));
    }

    private static BoardStatusMapping? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BoardStatusMapping>(json);
        }
        catch (JsonException)
        {
            // Unreadable is the same as absent: the catalog and the provider's category still work, and a
            // sync that refused to run because a settings row was malformed would help nobody.
            return null;
        }
    }
}
