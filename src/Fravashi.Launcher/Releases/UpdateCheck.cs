namespace Fravashi.Launcher.Releases;

/// <summary>Whether - and how urgently - a newer product release applies to an install.</summary>
public enum UpdateKind
{
    /// <summary>No newer release for this variant + host architecture.</summary>
    None,

    /// <summary>A newer release exists; the user is offered it but is not forced.</summary>
    Available,

    /// <summary>
    /// A newer release the client <b>must</b> take - the manifest marks it critical, or the install is below
    /// the manifest's <c>minSupportedVersion</c> floor. T5 locks the app on this (ruling 2 / D03-2).
    /// </summary>
    Critical,

    /// <summary>
    /// The update channel could not be checked (backlog 230), so whether a newer release exists is not known. It is
    /// NEVER written to <c>update-state.json</c>: what the last successful check recorded stands, a critical flag
    /// included. <see cref="UpdateAvailability.CouldNotCheck"/> says why and since when.
    /// </summary>
    Unknown,
}

/// <summary>
/// The outcome of an update check: the kind, the target version, the image the launcher would pull for this
/// host, and the release-notes URL. <see cref="Image"/> is non-null whenever <see cref="Kind"/> is
/// <see cref="UpdateKind.Available"/> or <see cref="UpdateKind.Critical"/>.
/// </summary>
public sealed record UpdateAvailability(
    UpdateKind Kind,
    string? TargetVersion,
    ReleaseImage? Image,
    string? ReleaseNotesUrl)
{
    /// <summary>The "nothing to do" result.</summary>
    public static readonly UpdateAvailability None = new(UpdateKind.None, null, null, null);

    /// <summary>Why the channel could not be checked; set exactly when <see cref="Kind"/> is <see cref="UpdateKind.Unknown"/>.</summary>
    public UpdateCheckFailure? CouldNotCheck { get; init; }

    /// <summary>
    /// What the manifest's signature check found (backlog 242): <see cref="ManifestSignature.Verified"/> when this was
    /// evaluated from a manifest, the refusal when one arrived and was not used, and null when the channel could not
    /// be checked far enough to know (or when nobody said, as in a test calling <see cref="UpdateCheck.Evaluate"/>).
    /// </summary>
    public ManifestSignature? Signature { get; init; }

    /// <summary>The "could not check" result (backlog 230), a refused signature included (backlog 242).</summary>
    public static UpdateAvailability Unknown(UpdateCheckFailure failure) =>
        new(UpdateKind.Unknown, null, null, null)
        {
            CouldNotCheck = failure ?? throw new ArgumentNullException(nameof(failure)),
            Signature = failure.Signature,
        };
}

/// <summary>
/// A check of the update channel that could not be made (backlog 230), as recorded in <c>update-check.json</c>.
/// </summary>
/// <param name="Reason">Why, in words ("name not resolved", "it answered HTTP 503").</param>
/// <param name="FailingSince">The first failed check since the last successful one.</param>
/// <param name="LastTried">The latest failed check.</param>
public sealed record UpdateCheckFailure(string Reason, DateTimeOffset FailingSince, DateTimeOffset LastTried)
{
    /// <summary>
    /// Set when the manifest arrived and was refused for its signature (backlog 242), so it is said as that and not as
    /// an outage; <see cref="Reason"/> then holds the refusal sentence.
    /// </summary>
    public ManifestSignature? Signature { get; init; }

    /// <summary>What the last successful check recorded, which still stands; null when nothing was recorded.</summary>
    public UpdateStateStore.UpdateStateFile? Recorded { get; init; }
}

/// <summary>
/// Decides whether a manifest offers an update to a given install (ADR 0006 / D03-2 / D03-3). This is the
/// contract the T5 <c>fravashi</c> product-update path is built on: it compares <b>within the installed variant
/// only</b> (never crosses variants), matches the install's host <b>architecture</b> (so it only offers an
/// image the host can run natively - R1 / ADR 0009), and classifies the result as ordinary or critical.
/// Pure and side-effect-free, so it is fully unit-testable without a network or a runtime.
/// </summary>
public static class UpdateCheck
{
    /// <summary>Container OS the studio image targets. Kept a constant so callers do not string-literal it.</summary>
    public const string ContainerOs = "linux";

    /// <summary>
    /// Evaluates <paramref name="manifest"/> for an install of <paramref name="installedVariant"/> at
    /// <paramref name="installedVersion"/> running on <paramref name="arch"/>.
    /// </summary>
    /// <param name="manifest">
    /// A manifest whose signature has ALREADY been verified against the release key (backlog 242): the only producer
    /// of one in production is <see cref="ManifestFetch.Read"/>, which <see cref="HttpReleaseManifestSource"/> reaches
    /// only after <see cref="ReleaseManifestSignature.Verify"/> said so. This method checks nothing of the kind itself.
    /// </param>
    /// <param name="installedVersion">The install's current product version (e.g. from launcher.json).</param>
    /// <param name="installedVariant">The install's variant name (e.g. <c>full</c>). Update checks stay within it.</param>
    /// <param name="arch">The host CPU architecture - <c>amd64</c> or <c>arm64</c>.</param>
    /// <param name="os">The image OS to match; defaults to <see cref="ContainerOs"/>.</param>
    public static UpdateAvailability Evaluate(
        ReleaseManifest manifest,
        string installedVersion,
        string installedVariant,
        string arch,
        string os = ContainerOs)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        // Never offer an image from another variant (D03-3), and never offer one the host cannot run natively.
        var variant = manifest.FindVariant(installedVariant);
        var image = variant?.FindImage(os, arch);
        if (variant is null || image is null)
        {
            return UpdateAvailability.None;
        }

        // Unparseable versions on either side => don't claim an update (fail closed to "no update").
        if (!SemanticVersion.TryParse(manifest.Version, out var latest) ||
            !SemanticVersion.TryParse(installedVersion, out var installed))
        {
            return UpdateAvailability.None;
        }

        if (latest.CompareTo(installed) <= 0)
        {
            return UpdateAvailability.None;
        }

        var belowFloor =
            !string.IsNullOrWhiteSpace(manifest.MinSupportedVersion) &&
            SemanticVersion.TryParse(manifest.MinSupportedVersion, out var floor) &&
            installed.CompareTo(floor) < 0;

        var kind = manifest.Critical || belowFloor ? UpdateKind.Critical : UpdateKind.Available;
        return new UpdateAvailability(kind, manifest.Version, image, manifest.ReleaseNotesUrl);
    }
}
