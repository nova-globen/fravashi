using System.Security.Cryptography;
using System.Text;

namespace Fravashi.Infrastructure.Integrity;

/// <summary>
/// The app-side view of the launcher-written expected-shape record (F3 / ADR 0007). This mirrors the launcher's
/// <c>IntegrityRecord</c> shape and its canonicalization/HMAC byte-for-byte so the two - which live in separate
/// assemblies - agree on the signature. The canonical form is built from the PARSED model, so JSON formatting
/// differences between the launcher's serializer and this reader cannot change the signature. The live in-container
/// scenario is the real cross-assembly proof; unit tests pin each side's canonicalization.
/// </summary>
public sealed record IntegrityRecordModel
{
    public int SchemaVersion { get; init; } = 1;
    public string Runtime { get; init; } = "";
    public DateTimeOffset RecordedUtc { get; init; }
    public IReadOnlyList<IntegrityComponentModel> Components { get; init; } = [];
    public string Hmac { get; init; } = "";
}

/// <summary>One checked component's expected shape (the studio app, or T9's VPN sidecar).</summary>
public sealed record IntegrityComponentModel
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "app";
    public string ImageRef { get; init; } = "";
    public string ImageDigest { get; init; } = "";
    public string Variant { get; init; } = "";
    public string Version { get; init; } = "";
    public string ContainerName { get; init; } = "";
    public int HostPort { get; init; }
    public int ContainerPort { get; init; }
    public bool Loopback { get; init; }
    public int NonRootUid { get; init; }
    public IReadOnlyList<IntegrityMountModel> Mounts { get; init; } = [];
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
}

/// <summary>One expected mount point and its kind.</summary>
public sealed record IntegrityMountModel(string Path, string Kind);

/// <summary>
/// Deterministic canonicalization + HMAC verification for the integrity record, kept byte-for-byte identical to
/// the launcher's <c>IntegrityCanonical</c>. Env keys are sorted ordinal so store order cannot change the signature.
/// </summary>
public static class IntegrityCanonical
{
    /// <summary>Env var carrying the base64 HMAC key the launcher injected (mirror of the launcher constant).</summary>
    public const string KeyEnvVar = "FRAVASHI_INTEGRITY_KEY";

    /// <summary>Env var carrying the pinned image digest the launcher injected.</summary>
    public const string DigestEnvVar = "FRAVASHI_IMAGE_DIGEST";

    /// <summary>Builds the canonical string over every field EXCEPT the HMAC itself.</summary>
    public static string Build(IntegrityRecordModel record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var sb = new StringBuilder();
        sb.Append('v').Append(record.SchemaVersion).Append('\n');
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
    public static string Sign(IntegrityRecordModel record, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Build(record)));
        return Convert.ToHexStringLower(mac);
    }

    /// <summary>Constant-time verify of the record's HMAC under <paramref name="key"/>.</summary>
    public static bool Verify(IntegrityRecordModel record, byte[] key)
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
