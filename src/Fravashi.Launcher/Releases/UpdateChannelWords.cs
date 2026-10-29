using System.Globalization;
using Fravashi.Domain;

namespace Fravashi.Launcher.Releases;

/// <summary>
/// The one sentence for an update channel that could not be checked (backlog 230), or whose manifest was refused for
/// its signature (backlog 242), so <c>fravashi status</c>,
/// <c>fravashi update</c>, the start and the upgrade operation (which <c>studio_version</c> relays) all say the same, and
/// none of them says "up to date" or "newest".
/// </summary>
public static class UpdateChannelWords
{
    public static string CouldNotCheck(UpdateCheckFailure failure, string installed, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(clock);

        // Backlog 242: a manifest that arrived and was refused for its signature is said as that, never as an outage.
        var text = failure.Signature is { } refused
            ? $"{Capitalized(ReleaseManifestSignature.NotSignedWords)} ({ReleaseManifestSignature.Why(refused)}, "
                + $"last tried {When(failure.LastTried, clock)}). {ProductName.Short} {installed} is installed; whether a newer one exists is unknown."
            : $"Could not check the update channel since {When(failure.FailingSince, clock)} ({failure.Reason}). "
                + $"{ProductName.Short} {installed} is installed; whether a newer one exists is unknown.";

        if (failure.Recorded is { TargetVersion: { } target } recorded)
        {
            if (string.Equals(recorded.Kind, nameof(UpdateKind.Critical), StringComparison.OrdinalIgnoreCase))
            {
                text += $" The last successful check ({When(recorded.CheckedUtc, clock)}) found a REQUIRED update to {target}: "
                    + $"{ProductName.Short} stays locked until it is installed, and `{ProductName.Command} update` installs it once the channel answers.";
            }
            else if (string.Equals(recorded.Kind, nameof(UpdateKind.Available), StringComparison.OrdinalIgnoreCase))
            {
                text += $" The last successful check ({When(recorded.CheckedUtc, clock)}) found {ProductName.Short} {target}.";
            }
        }

        return text;
    }

    /// <summary>The <c>fravashi status</c> word for the manifest's signature (backlog 242).</summary>
    public static string Manifest(UpdateAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        return availability.Signature switch
        {
            ManifestSignature.Verified => "verified - signed by this product's release key",
            ManifestSignature.Unsigned or ManifestSignature.BadSignature =>
                $"NOT USED - {ReleaseManifestSignature.Refusal(availability.Signature.Value)}",
            _ => $"not checked - {availability.CouldNotCheck?.Reason ?? "the update channel could not be checked"}",
        };
    }

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>A moment in the launcher's local time: "14:02" today, "2026-10-06 14:02" on another day.</summary>
    public static string When(DateTimeOffset at, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var zone = clock.LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone);
        return local.ToString(local.Date == today.Date ? "HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
