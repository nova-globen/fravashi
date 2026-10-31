using System.Text.Json.Serialization;

namespace Fravashi.Launcher.Backup;

/// <summary>
/// The manifest the studio writes into a backup archive, as the launcher reads it (backlog 79).
/// <para>
/// The launcher takes no project references by design (it is the cross-platform, dependency-free half of
/// the product), so this is a second declaration of a shared FILE FORMAT rather than a shared type. The
/// round-trip test drives a real archive from the studio's writer through this reader, so the two cannot
/// drift apart silently.
/// </para>
/// </summary>
public sealed record BackupManifest
{
    /// <summary>The manifest layout number. A higher one is refused rather than half-understood.</summary>
    [JsonPropertyName("format")]
    public int Format { get; init; }

    /// <summary>What the archive claims to be; anything else is not a Fravashi backup.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("createdUtc")]
    public string? CreatedUtc { get; init; }

    [JsonPropertyName("productVersion")]
    public string? ProductVersion { get; init; }

    [JsonPropertyName("productVariant")]
    public string? ProductVariant { get; init; }

    /// <summary>The database schema version inside the archive.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    /// <summary>The archive entry holding the database, so a restore can name it in its report.</summary>
    [JsonPropertyName("databaseEntry")]
    public string? DatabaseEntry { get; init; }

    [JsonPropertyName("providers")]
    public IReadOnlyList<BackupManifestProvider> Providers { get; init; } = [];

    [JsonPropertyName("files")]
    public IReadOnlyList<BackupManifestFile> Files { get; init; } = [];

    [JsonPropertyName("notes")]
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The manifest layout this launcher understands.</summary>
    public const int SupportedFormat = 1;

    /// <summary>The value <see cref="Kind"/> must carry.</summary>
    public const string ExpectedKind = "fravashi-backup";

    /// <summary>
    /// The kind an archive taken before the internal rename carries (the bridge release): accepted as this product's too,
    /// so a backup taken on an older install restores. Removed in the cleanup release.
    /// </summary>
    public const string FormerKind = FormerInstall.BackupKind;

    /// <summary>Whether <paramref name="kind"/> names a backup of this product, by either name.</summary>
    public static bool IsProductKind(string? kind) =>
        string.Equals(kind, ExpectedKind, StringComparison.Ordinal) || string.Equals(kind, FormerKind, StringComparison.Ordinal);

    /// <summary>
    /// The highest database schema version this build of the product knows how to open. A backup taken by
    /// a NEWER Fravashi is refused: the studio migrates a database forward and has no path back, so
    /// unpacking a newer one would hand the product a database it can only fail on, after the previous one
    /// had already been moved aside.
    /// <para>
    /// Held equal to the studio's own <c>SqliteSchema.CurrentVersion</c> by <c>BackupRoundTripTests</c>,
    /// which is the only place the two halves can be compared: the launcher cannot see the schema code.
    /// </para>
    /// </summary>
    public const int KnownSchemaVersion = 50;
}

/// <summary>One configured provider recorded at backup time - what to check after moving machines.</summary>
public sealed record BackupManifestProvider
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("hasCredential")]
    public bool HasCredential { get; init; }
}

/// <summary>One file in the archive, with the hash the restore verifies before it moves anything.</summary>
public sealed record BackupManifestFile
{
    /// <summary>Path relative to the data directory, with <c>/</c> separators (the archive mirrors it).</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }
}
