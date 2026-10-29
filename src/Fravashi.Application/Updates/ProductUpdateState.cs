namespace Fravashi.Application.Updates;

/// <summary>How a pending product update applies to the running app (Type B, ADR 0006 / D03-2).</summary>
public enum ProductUpdateSeverity
{
    /// <summary>No newer product release is pending for this install.</summary>
    None,

    /// <summary>A newer product release is available - the app shows a dismissible banner but is not locked.</summary>
    Available,

    /// <summary>The manifest marks the update critical (or the install is below the floor) - the app locks until updated.</summary>
    Critical,
}

/// <summary>
/// The app's view of a pending <b>product</b> update (Type B). This is the app side of the ADR 0006 contract:
/// the <c>fravashi</c> launcher - the only actor that can replace the image - fetches the signed manifest, runs the
/// pure <c>UpdateCheck.Evaluate</c>, and writes the result into the mounted data directory
/// (<c>update-state.json</c>). The app <b>reads</b> that file; it never re-evaluates criticality itself. Absent
/// file =&gt; <see cref="None"/> (no release channel configured yet - degrade honestly, never lock).
/// </summary>
/// <param name="Severity">Whether an update is pending and whether it locks the app.</param>
/// <param name="CurrentVersion">The version the launcher saw installed, for display.</param>
/// <param name="TargetVersion">The version the update would move to, for display.</param>
/// <param name="ReleaseNotesUrl">The release-notes URL, when the manifest carried one.</param>
/// <param name="CheckedUtc">When the launcher last evaluated the manifest, or null if unknown.</param>
public sealed record ProductUpdateState(
    ProductUpdateSeverity Severity,
    string? CurrentVersion,
    string? TargetVersion,
    string? ReleaseNotesUrl,
    DateTimeOffset? CheckedUtc)
{
    /// <summary>The "no update pending / no channel" state.</summary>
    public static readonly ProductUpdateState None = new(ProductUpdateSeverity.None, null, null, null, null);

    /// <summary>True when the app must lock to the update-required screen (manifest-critical, D03-2).</summary>
    public bool IsLocked => Severity == ProductUpdateSeverity.Critical;

    /// <summary>
    /// Since when the launcher has failed to check the update channel, or null when its last check got an answer
    /// (backlog 230, <c>update-check.json</c>). It never changes <see cref="Severity"/>: what the last successful
    /// check recorded stands, a critical lock included.
    /// </summary>
    public DateTimeOffset? ChannelUnreachableSince { get; init; }

    /// <summary>Why the channel could not be checked, in words ("name not resolved"), when <see cref="ChannelUnreachableSince"/> is set.</summary>
    public string? ChannelUnreachableReason { get; init; }

    /// <summary>
    /// True when <see cref="Severity"/> was recorded from a manifest whose signature the launcher verified against the
    /// release key (backlog 242). False for no record, or one written by a launcher from before the check.
    /// </summary>
    public bool ManifestVerified { get; init; }

    /// <summary>
    /// Set when the launcher's latest check found a manifest it did not use because of its signature (backlog 242):
    /// <see cref="ManifestSignatureRefusal.Unsigned"/> or <see cref="ManifestSignatureRefusal.BadSignature"/>. Like an
    /// unreachable channel it never changes <see cref="Severity"/>.
    /// </summary>
    public ManifestSignatureRefusal? ManifestRefused { get; init; }
}

/// <summary>Why the launcher did not use the update channel's manifest (backlog 242).</summary>
public enum ManifestSignatureRefusal
{
    /// <summary>No signature was published beside it.</summary>
    Unsigned,

    /// <summary>The signature beside it is not the release key's over its bytes.</summary>
    BadSignature,
}

/// <summary>
/// Reads the launcher-written product-update state (see <see cref="ProductUpdateState"/>). The critical-lock
/// middleware and the dashboard banner consume this; the value is cached with a short TTL so a request-time read
/// is cheap while still picking up a fresh <c>fravashi update</c>/<c>fravashi status</c> evaluation within seconds.
/// </summary>
public interface IProductUpdateStatus
{
    /// <summary>The current product-update state, from the launcher-written file (or <see cref="ProductUpdateState.None"/>).</summary>
    ProductUpdateState Current { get; }
}
