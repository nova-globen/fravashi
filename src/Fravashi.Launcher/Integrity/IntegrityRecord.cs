using System.Security.Cryptography;
using System.Text;

namespace Fravashi.Launcher.Integrity;

/// <summary>
/// The launcher-written <b>expected-shape record</b> (F3 / ADR 0007). The launcher is the only actor that runs
/// the container, so it is the only actor that knows - at the moment it starts the container - the image digest
/// it pinned, the runtime it drove, and the exact shape (mounts, env, port, non-root uid) it created. It records
/// that expectation into <c>&lt;dataDir&gt;/integrity-record.json</c>; the app's integrity <c>BackgroundService</c>
/// reads it back and verifies its own running shape against it.
/// <para>
/// This is tamper-<b>evidence</b>, not prevention (ADR 0007): the record is HMAC-signed with a key the launcher
/// holds host-side and injects into the container as an env var, so a casual edit to the record file in
/// <c>/data</c> is detected. It cannot stop a host <em>admin</em> - who can read the key via a container inspect
/// and re-sign - from forging it; that is the documented boundary of the scoped tier. The record is a list of
/// checked <see cref="IntegrityComponent"/>s so T9 can add the VPN sidecar as another component without changing
/// the format.
/// </para>
/// </summary>
public sealed record IntegrityRecord
{
    /// <summary>Record format version, so the reader can evolve the shape. Current: 1.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The container runtime the launcher drove (<c>wslc</c>/<c>docker</c>/<c>podman</c>).</summary>
    public string Runtime { get; init; } = "";

    /// <summary>When the launcher recorded this expectation (UTC).</summary>
    public DateTimeOffset RecordedUtc { get; init; }

    /// <summary>The checked components. T8 records one (<c>studio</c>); T9 appends the VPN sidecar.</summary>
    public IReadOnlyList<IntegrityComponent> Components { get; init; } = [];

    /// <summary>Lower-case hex HMAC-SHA256 over the canonical form of every field above. Empty until signed.</summary>
    public string Hmac { get; init; } = "";
}

/// <summary>One checked unit of the running shape - the studio app container, or (T9) the VPN sidecar.</summary>
public sealed record IntegrityComponent
{
    /// <summary>Stable component id (<c>studio</c>, later <c>vpn-sidecar</c>).</summary>
    public string Id { get; init; } = "";

    /// <summary>Component kind driving which checks apply (<c>app</c> or <c>sidecar</c>).</summary>
    public string Kind { get; init; } = IntegrityComponentKinds.App;

    /// <summary>The image reference the component runs (e.g. <c>fravashi:latest</c>).</summary>
    public string ImageRef { get; init; } = "";

    /// <summary>The pinned image content digest (<c>sha256:…</c>) - the config <c>Id</c> for tar-loaded images (R1).</summary>
    public string ImageDigest { get; init; } = "";

    /// <summary>The variant the launcher believes is installed (matched against the image-baked <c>FRAVASHI_VARIANT</c>).</summary>
    public string Variant { get; init; } = "";

    /// <summary>The version the launcher believes is installed (matched against <c>FRAVASHI_VERSION</c> when concrete).</summary>
    public string Version { get; init; } = "";

    /// <summary>The container name the launcher created.</summary>
    public string ContainerName { get; init; } = "";

    /// <summary>The reserved host port.</summary>
    public int HostPort { get; init; }

    /// <summary>The container-internal port the app serves on.</summary>
    public int ContainerPort { get; init; }

    /// <summary>True when the publish is loopback-only (<c>127.0.0.1</c>).</summary>
    public bool Loopback { get; init; }

    /// <summary>The non-root uid the container process runs as (1000).</summary>
    public int NonRootUid { get; init; }

    /// <summary>The expected writable mounts (bind / volume / tmpfs).</summary>
    public IReadOnlyList<IntegrityMount> Mounts { get; init; } = [];

    /// <summary>The environment variables the app should observe, as key→value.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
}

/// <summary>One expected mount point and its kind.</summary>
/// <param name="Path">Container path (e.g. <c>/data</c>).</param>
/// <param name="Kind">Mount kind (<c>bind</c>, <c>volume</c>, <c>tmpfs</c>).</param>
public sealed record IntegrityMount(string Path, string Kind);

/// <summary>Stable component-kind tokens.</summary>
public static class IntegrityComponentKinds
{
    /// <summary>The studio application container itself.</summary>
    public const string App = "app";

    /// <summary>A sidecar container (T9's VPN tunnel).</summary>
    public const string Sidecar = "sidecar";
}

/// <summary>Stable mount-kind tokens.</summary>
public static class IntegrityMountKinds
{
    public const string Bind = "bind";
    public const string Volume = "volume";
    public const string Tmpfs = "tmpfs";
}

/// <summary>
/// The env var names the launcher injects into the container to carry the integrity secret and the pinned digest.
/// They are part of the runtime shape the launcher controls; the app reads them to verify the record.
/// </summary>
public static class IntegrityEnv
{
    /// <summary>Base64 HMAC key the launcher generates + holds host-side and injects here so the app can verify the record.</summary>
    public const string KeyVar = "FRAVASHI_INTEGRITY_KEY";

    /// <summary>The pinned image digest, injected so the app can cross-check it against the HMAC-signed record.</summary>
    public const string DigestVar = "FRAVASHI_IMAGE_DIGEST";
}

/// <summary>
/// Deterministic canonicalization + HMAC for the integrity record, DUPLICATED byte-for-byte on the app side
/// (<c>Fravashi.Infrastructure.Integrity</c>) so both compute the same signature without sharing an assembly.
/// The canonical form is built from the PARSED model (not the JSON text), so it is immune to JSON formatting /
/// property-order / whitespace drift between the two serializers. Env keys are sorted ordinal so store order
/// cannot change the signature.
/// </summary>
public static class IntegrityCanonical
{
    /// <summary>Builds the canonical string over every field EXCEPT the HMAC itself.</summary>
    public static string Build(IntegrityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var sb = new StringBuilder();
        sb.Append("v").Append(record.SchemaVersion).Append('\n');
        sb.Append("runtime=").Append(record.Runtime).Append('\n');
        sb.Append("recordedUtc=").Append(record.RecordedUtc.ToUniversalTime().ToString("O")).Append('\n');

        foreach (var c in record.Components)
        {
            sb.Append("component=").Append(c.Id).Append('\n');
            sb.Append(" kind=").Append(c.Kind).Append('\n');
            sb.Append(" imageRef=").Append(c.ImageRef).Append('\n');
            sb.Append(" imageDigest=").Append(c.ImageDigest).Append('\n');
            sb.Append(" variant=").Append(c.Variant).Append('\n');
            sb.Append(" version=").Append(c.Version).Append('\n');
            sb.Append(" containerName=").Append(c.ContainerName).Append('\n');
            sb.Append(" hostPort=").Append(c.HostPort).Append('\n');
            sb.Append(" containerPort=").Append(c.ContainerPort).Append('\n');
            sb.Append(" loopback=").Append(c.Loopback ? "true" : "false").Append('\n');
            sb.Append(" nonRootUid=").Append(c.NonRootUid).Append('\n');
            foreach (var m in c.Mounts)
            {
                sb.Append(" mount=").Append(m.Path).Append('|').Append(m.Kind).Append('\n');
            }

            foreach (var kv in c.Env.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                sb.Append(" env=").Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>Lower-case hex HMAC-SHA256 of the canonical form under <paramref name="key"/>.</summary>
    public static string Sign(IntegrityRecord record, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Build(record)));
        return Convert.ToHexStringLower(mac);
    }

    /// <summary>Constant-time verify of <paramref name="record"/>'s <see cref="IntegrityRecord.Hmac"/> under <paramref name="key"/>.</summary>
    public static bool Verify(IntegrityRecord record, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrEmpty(record.Hmac) || key.Length == 0)
        {
            return false;
        }

        var expected = Sign(record, key);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(record.Hmac));
    }
}
