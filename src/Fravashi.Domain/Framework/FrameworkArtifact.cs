namespace Fravashi.Domain.Framework;

/// <summary>
/// The kinds of framework artifact Fravashi stores and edits under Settings › Framework. Skills ship in T2;
/// the remaining kinds (agents, plugins, commands, hooks, MCP servers) land in T3 and reuse the same generic
/// store and resource model. The name is a product-neutral concept - a "framework artifact" - even though the
/// individual kinds mirror Claude-ecosystem authoring concepts (Claude is the canonical shape, ADR 0005).
/// </summary>
public enum FrameworkArtifactKind
{
    Skill,
    Agent,
    Plugin,
    Command,
    Hook,
    McpServer,
}

/// <summary>
/// A single stored framework artifact: a kebab-case <see cref="Name"/> unique within its <see cref="Kind"/>,
/// an opaque JSON <see cref="PayloadJson"/> carrying the kind-specific fields (e.g. a serialized
/// <see cref="SkillDefinition"/>), and create/update timestamps. This is the generic row the SQLite store
/// persists; typed access to the payload is the caller's job (see the kind-specific serializers). Uploaded
/// binaries live out-of-row as <see cref="FrameworkResource"/> records keyed by <see cref="Id"/>.
/// </summary>
/// <param name="Id">Stable opaque identity (a GUID string), independent of the renameable <see cref="Name"/>.</param>
/// <param name="Kind">Which artifact kind this row is.</param>
/// <param name="Name">Kebab-case name, unique per <see cref="Kind"/> - the identity a prompt references as <c>$name</c>.</param>
/// <param name="PayloadJson">The kind-specific fields, serialized to JSON.</param>
/// <param name="CreatedUtc">When the artifact was first stored.</param>
/// <param name="UpdatedUtc">When the artifact was last updated.</param>
public sealed record FrameworkArtifact(
    string Id,
    FrameworkArtifactKind Kind,
    string Name,
    string PayloadJson,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// A binary resource attached to a framework artifact (an uploaded icon image or a skill resource file).
/// Stored as bytes in a blob column, out of the artifact's JSON row so payloads stay small and text-diffable.
/// </summary>
/// <param name="Id">Stable opaque identity (a GUID string).</param>
/// <param name="ArtifactId">The owning <see cref="FrameworkArtifact.Id"/>.</param>
/// <param name="FileName">The original upload file name (display only; never used as a filesystem path).</param>
/// <param name="ContentType">The MIME type, when the upload provided one; otherwise null.</param>
/// <param name="Content">The raw bytes.</param>
/// <param name="CreatedUtc">When the resource was stored.</param>
public sealed record FrameworkResource(
    string Id,
    string ArtifactId,
    string FileName,
    string? ContentType,
    byte[] Content,
    DateTimeOffset CreatedUtc)
{
    /// <summary>The size of <see cref="Content"/> in bytes.</summary>
    public long SizeBytes => Content.LongLength;
}
