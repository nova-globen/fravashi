using Fravashi.Domain.Framework;

namespace Fravashi.Application.Framework;

/// <summary>
/// Persistence for framework artifacts and their uploaded resources. One generic store serves every
/// <see cref="FrameworkArtifactKind"/> (skills ship in T2; agents/plugins/commands/hooks/MCP servers reuse it
/// in T3). Names are unique per kind - <see cref="Add"/> and <see cref="Update"/> throw
/// <see cref="DuplicateArtifactNameException"/> on a collision. Resource writes enforce the size caps in
/// <see cref="FrameworkArtifactLimits"/>, throwing <see cref="ResourceTooLargeException"/>.
/// </summary>
public interface IFrameworkArtifactStore
{
    /// <summary>Every artifact of a kind, newest first.</summary>
    IReadOnlyList<FrameworkArtifact> List(FrameworkArtifactKind kind);

    /// <summary>An artifact by id, or null.</summary>
    FrameworkArtifact? Get(string id);

    /// <summary>An artifact by (kind, name), or null.</summary>
    FrameworkArtifact? GetByName(FrameworkArtifactKind kind, string name);

    /// <summary>Stores a new artifact. Throws <see cref="DuplicateArtifactNameException"/> if the name is taken.</summary>
    void Add(FrameworkArtifact artifact);

    /// <summary>
    /// Updates an existing artifact (matched by <see cref="FrameworkArtifact.Id"/>). Throws
    /// <see cref="DuplicateArtifactNameException"/> if the new name collides with a different artifact.
    /// </summary>
    void Update(FrameworkArtifact artifact);

    /// <summary>Deletes an artifact and all its resources.</summary>
    void Remove(string id);

    /// <summary>The resources attached to an artifact, in upload order.</summary>
    IReadOnlyList<FrameworkResource> ListResources(string artifactId);

    /// <summary>A single resource by id (bytes included), or null.</summary>
    FrameworkResource? GetResource(string resourceId);

    /// <summary>Stores a resource. Throws <see cref="ResourceTooLargeException"/> if it exceeds the per-file cap.</summary>
    void AddResource(FrameworkResource resource);

    /// <summary>Deletes a single resource.</summary>
    void RemoveResource(string resourceId);
}

/// <summary>Upload size caps for framework resources. Enforced by the store and echoed in the UI.</summary>
public static class FrameworkArtifactLimits
{
    /// <summary>Maximum size of a single uploaded resource (icon image or skill file): 5 MiB.</summary>
    public const long MaxResourceBytes = 5 * 1024 * 1024;

    /// <summary>A human-readable form of <see cref="MaxResourceBytes"/> for messages.</summary>
    public const string MaxResourceLabel = "5 MB";
}

/// <summary>Thrown when an artifact name is already used within its kind.</summary>
public sealed class DuplicateArtifactNameException(FrameworkArtifactKind kind, string name)
    : Exception($"A {kind} named '{name}' already exists.")
{
    /// <summary>The conflicting kind.</summary>
    public FrameworkArtifactKind Kind { get; } = kind;

    /// <summary>The conflicting name.</summary>
    public string Name { get; } = name;
}

/// <summary>Thrown when an uploaded resource exceeds <see cref="FrameworkArtifactLimits.MaxResourceBytes"/>.</summary>
public sealed class ResourceTooLargeException(string fileName, long sizeBytes)
    : Exception($"'{fileName}' is {sizeBytes} bytes, over the {FrameworkArtifactLimits.MaxResourceLabel} limit.")
{
    /// <summary>The rejected file's name.</summary>
    public string FileName { get; } = fileName;

    /// <summary>The rejected file's size.</summary>
    public long SizeBytes { get; } = sizeBytes;
}
