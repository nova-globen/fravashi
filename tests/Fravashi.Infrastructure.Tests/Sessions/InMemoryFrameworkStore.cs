using Fravashi.Application.Framework;
using Fravashi.Domain.Framework;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>A minimal in-memory <see cref="IFrameworkArtifactStore"/> for the session projector tests.</summary>
internal sealed class InMemoryFrameworkStore : IFrameworkArtifactStore
{
    private readonly List<FrameworkArtifact> _artifacts = [];

    /// <summary>Stores an artifact and returns its id.</summary>
    public string Put(FrameworkArtifactKind kind, string name, string payloadJson)
    {
        var id = Guid.NewGuid().ToString("N");
        _artifacts.Add(new FrameworkArtifact(id, kind, name, payloadJson, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        return id;
    }

    public IReadOnlyList<FrameworkArtifact> List(FrameworkArtifactKind kind) =>
        _artifacts.Where(a => a.Kind == kind).ToList();

    public FrameworkArtifact? Get(string id) => _artifacts.FirstOrDefault(a => a.Id == id);

    public FrameworkArtifact? GetByName(FrameworkArtifactKind kind, string name) =>
        _artifacts.FirstOrDefault(a => a.Kind == kind && a.Name == name);

    public void Add(FrameworkArtifact artifact) => _artifacts.Add(artifact);

    public void Update(FrameworkArtifact artifact)
    {
        _artifacts.RemoveAll(a => a.Id == artifact.Id);
        _artifacts.Add(artifact);
    }

    public void Remove(string id) => _artifacts.RemoveAll(a => a.Id == id);

    public IReadOnlyList<FrameworkResource> ListResources(string artifactId) => [];

    public FrameworkResource? GetResource(string resourceId) => null;

    public void AddResource(FrameworkResource resource) => throw new NotSupportedException();

    public void RemoveResource(string resourceId) => throw new NotSupportedException();
}
