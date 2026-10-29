namespace Fravashi.Launcher.Releases;

/// <summary>
/// The release manifest published to the public releases repo (ADR 0006). It is the single source of truth
/// for "what versions exist", consumed by the install scripts (anonymous first install) and by the
/// <c>fravashi</c> launcher's update check (T5). One manifest describes <b>one version tag</b> carrying
/// <b>multiple named variant assets</b> (ruling 3 / D03-3), each variant carrying one image per
/// architecture (multi-arch, R1 / ADR 0009) plus the self-contained launcher builds per RID.
/// </summary>
/// <remarks>
/// Backward compatibility mirrors <see cref="LauncherConfig"/>: every field carries a default, and the parser
/// tolerates unknown properties, so a newer manifest read by an older launcher (or vice-versa) deserializes
/// cleanly. Trust is two steps (backlog 242): the manifest's own bytes are verified against the release key
/// (<see cref="ReleaseManifestSignature"/>, ECDSA P-256 over <c>release-manifest.sig</c>) before this record is ever
/// deserialized, and then each asset is verified against the manifest's <see cref="ReleaseImage.Sha256"/> before use.
/// </remarks>
public sealed record ReleaseManifest
{
    /// <summary>Manifest format version, so consumers can evolve the shape. Current: 1.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Product identifier - always <c>fravashi</c>. Present so a consumer can reject a foreign manifest.</summary>
    public string Product { get; init; } = "";

    /// <summary>The release version this manifest describes (e.g. <c>1.0.0</c>), matching the git tag without the <c>v</c>.</summary>
    public string Version { get; init; } = "";

    /// <summary>When the release was published (UTC).</summary>
    public DateTimeOffset? ReleasedAt { get; init; }

    /// <summary>Human-facing release notes URL (the GitHub release page in the public repo).</summary>
    public string? ReleaseNotesUrl { get; init; }

    /// <summary>
    /// Manifest-only criticality (ruling 2 / D03-2): when <c>true</c>, a client on an older version must
    /// update, and the app locks until it does. <b>Only this manifest can set it</b> - no upstream CLI / npm /
    /// OS package can escalate itself to critical.
    /// </summary>
    public bool Critical { get; init; }

    /// <summary>
    /// Optional forced-update floor: any installed version <em>below</em> this is treated as critical even when
    /// <see cref="Critical"/> is <c>false</c>, so a security floor can be enforced without flagging every newer
    /// release critical. Absent = no floor.
    /// </summary>
    public string? MinSupportedVersion { get; init; }

    /// <summary>The named variants in this release (e.g. <c>full</c>, later <c>corp-vpn</c>).</summary>
    public IReadOnlyList<ReleaseVariant> Variants { get; init; } = [];

    /// <summary>The variant with this name (case-insensitive), or <c>null</c>. Update checks never cross variants (D03-3).</summary>
    public ReleaseVariant? FindVariant(string name) =>
        Variants.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One named variant asset set in a release. Variants diverge in features/image but share the version tag.</summary>
public sealed record ReleaseVariant
{
    /// <summary>Variant name (kebab, product-neutral) - the identity an install carries and update checks match on.</summary>
    public string Name { get; init; } = "";

    /// <summary>Optional human label for the variant (e.g. "Fravashi (full)").</summary>
    public string? DisplayName { get; init; }

    /// <summary>The container image per architecture (multi-arch - one per host CPU, R1 / ADR 0009).</summary>
    public IReadOnlyList<ReleaseImage> Images { get; init; } = [];

    /// <summary>The self-contained launcher builds per RID (win-x64, linux-x64, osx-arm64, …).</summary>
    public IReadOnlyList<ReleaseLauncher> Launchers { get; init; } = [];

    /// <summary>The image for the given OS + architecture (case-insensitive), or <c>null</c> if this host has none.</summary>
    public ReleaseImage? FindImage(string os, string arch) =>
        Images.FirstOrDefault(i =>
            string.Equals(i.Os, os, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.Arch, arch, StringComparison.OrdinalIgnoreCase));

    /// <summary>The launcher build for the given RID (case-insensitive), or <c>null</c>.</summary>
    public ReleaseLauncher? FindLauncher(string rid) =>
        Launchers.FirstOrDefault(l => string.Equals(l.Rid, rid, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A single container image asset: the <c>save</c>d tar the runtime <c>load</c>s (R1's proven path).</summary>
public sealed record ReleaseImage
{
    /// <summary>Image OS - <c>linux</c> for the studio container.</summary>
    public string Os { get; init; } = "linux";

    /// <summary>CPU architecture - <c>amd64</c> or <c>arm64</c> (matched to the host so each runs native, ADR 0009).</summary>
    public string Arch { get; init; } = "";

    /// <summary>The release asset file name (e.g. <c>fravashi-1.0.0-full-arm64.tar</c>).</summary>
    public string Asset { get; init; } = "";

    /// <summary>Anonymous download URL for the asset in the public releases repo.</summary>
    public string? AssetUrl { get; init; }

    /// <summary>
    /// The asset in slices, in order, when it is too large for one attachment (backlog 157): the forge resets a
    /// single upload of a few hundred MB, so a release attaches the gzipped image as 64 MB <c>.partNN.gz</c>
    /// slices. Joined in this order they are byte for byte the asset that <see cref="Sha256"/> and
    /// <see cref="SizeBytes"/> describe, so what is verified is always the whole, never a slice. Empty when the
    /// asset is one file at <see cref="AssetUrl"/>.
    /// </summary>
    public IReadOnlyList<string> PartUrls { get; init; } = [];

    /// <summary>Asset size in bytes (for progress + a sanity check before load).</summary>
    public long SizeBytes { get; init; }

    /// <summary>Lower-case hex SHA-256 of the asset - verified before the tar is ever loaded.</summary>
    public string Sha256 { get; init; } = "";

    /// <summary>
    /// The loaded image's content digest (<c>sha256:…</c>). Under a tar-loaded image this is the config
    /// image <c>Id</c> (ADR 0009: <c>RepoDigests</c> is empty for tar-loaded images); T8 pins launch on it.
    /// </summary>
    public string ImageDigest { get; init; } = "";

    /// <summary>The image reference the tar loads as (e.g. <c>fravashi:1.0.0-full</c>).</summary>
    public string? ImageRef { get; init; }
}

/// <summary>A self-contained launcher build for one RID (the <c>fravashi</c> exe, plus the tray on Windows).</summary>
public sealed record ReleaseLauncher
{
    /// <summary>.NET RID - <c>win-x64</c>, <c>linux-x64</c>, <c>osx-arm64</c>, …</summary>
    public string Rid { get; init; } = "";

    /// <summary>The release asset file name (a zip of the published launcher/tray).</summary>
    public string Asset { get; init; } = "";

    /// <summary>Anonymous download URL for the asset in the public releases repo.</summary>
    public string? AssetUrl { get; init; }

    /// <summary>Lower-case hex SHA-256 of the asset - verified before it is unpacked.</summary>
    public string Sha256 { get; init; } = "";
}
