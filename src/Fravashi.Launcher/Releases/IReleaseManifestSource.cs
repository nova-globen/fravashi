namespace Fravashi.Launcher.Releases;

/// <summary>
/// Fetches the release manifest (ADR 0006) for the launcher's update check (T5). The real implementation does an
/// anonymous HTTPS GET of the channel's manifest and of its signature beside it, and hands back a manifest only when
/// that signature is the release key's (backlog 242); it is behind an interface so the product-update flow is
/// unit-tested with a fake manifest (no network in committed tests).
/// </summary>
public interface IReleaseManifestSource
{
    /// <summary>
    /// The manifest, or why it could not be had. Never "nothing newer": that is <see cref="UpdateCheck"/>'s answer
    /// on a manifest that was read, and an unreachable channel answering it is the fail-open backlog 230 closed.
    /// </summary>
    Task<ManifestFetch> FetchAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// What asking the update channel gave: a manifest that was verified and read, or the reason none is used (backlog 230,
/// 242).
/// </summary>
/// <param name="Manifest">The manifest, when it was fetched, its signature verified, and read.</param>
/// <param name="CouldNotCheck">Why there is no manifest, in words ("name not resolved", "it answered HTTP 503").</param>
public sealed record ManifestFetch(ReleaseManifest? Manifest, string? CouldNotCheck)
{
    /// <summary>
    /// What its signature check found (backlog 242): <see cref="ManifestSignature.Verified"/> with a manifest,
    /// <see cref="ManifestSignature.Unsigned"/> or <see cref="ManifestSignature.BadSignature"/> for one refused, and null
    /// when the channel could not be checked far enough to know.
    /// </summary>
    public ManifestSignature? Signature { get; init; }

    /// <summary>A manifest whose signature verified. Nothing else may be handed to the update check.</summary>
    public static ManifestFetch Read(ReleaseManifest manifest) =>
        new(manifest ?? throw new ArgumentNullException(nameof(manifest)), null) { Signature = ManifestSignature.Verified };

    public static ManifestFetch Failed(string reason) => new(null, string.IsNullOrWhiteSpace(reason) ? "no reason was given" : reason);

    /// <summary>A manifest that arrived but is not used, because of its signature (backlog 242).</summary>
    public static ManifestFetch Refused(ManifestSignature outcome)
    {
        if (outcome == ManifestSignature.Verified)
        {
            throw new ArgumentException("A verified manifest is read, not refused.", nameof(outcome));
        }

        return new(null, ReleaseManifestSignature.Refusal(outcome)) { Signature = outcome };
    }
}

/// <summary>
/// Downloads a release asset (the image tar, or one slice of it) to a local path (T5 product update). Behind an
/// interface so the update flow is tested without the multi-GB download; the real implementation streams over HTTPS.
/// A failure leaves nothing at the destination and says whether trying again could help (backlog 230).
/// </summary>
public interface IReleaseAssetDownloader
{
    Task<AssetDownload> DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default);
}

/// <summary>One download's outcome (backlog 230).</summary>
/// <param name="Path">The downloaded file, when it arrived whole.</param>
/// <param name="Failure">Why it did not, in words.</param>
/// <param name="Retryable">
/// True when the network or the server failed in a way that may pass (a reset, a short body, a 5xx); false for a
/// refusal (404, 403) or the caller's own cancellation, where trying again only repeats the answer.
/// </param>
public sealed record AssetDownload(string? Path, string? Failure, bool Retryable)
{
    public static AssetDownload Done(string path) => new(path, null, false);

    public static AssetDownload Failed(string failure, bool retryable) => new(null, failure, retryable);
}
