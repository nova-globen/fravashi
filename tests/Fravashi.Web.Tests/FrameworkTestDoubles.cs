using Fravashi.Application.Framework;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Framework;

namespace Fravashi.Web.Tests;

/// <summary>In-memory <see cref="IFrameworkArtifactStore"/> for the Skills component tests.</summary>
internal sealed class FakeFrameworkArtifactStore : IFrameworkArtifactStore
{
    public Dictionary<string, FrameworkArtifact> Artifacts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FrameworkResource> Resources { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<FrameworkArtifact> List(FrameworkArtifactKind kind) =>
        Artifacts.Values.Where(a => a.Kind == kind).OrderByDescending(a => a.CreatedUtc).ToList();

    public FrameworkArtifact? Get(string id) => Artifacts.GetValueOrDefault(id);

    public FrameworkArtifact? GetByName(FrameworkArtifactKind kind, string name) =>
        Artifacts.Values.FirstOrDefault(a => a.Kind == kind && a.Name == name);

    public void Add(FrameworkArtifact artifact)
    {
        if (GetByName(artifact.Kind, artifact.Name) is not null)
        {
            throw new DuplicateArtifactNameException(artifact.Kind, artifact.Name);
        }

        Artifacts[artifact.Id] = artifact;
    }

    public void Update(FrameworkArtifact artifact)
    {
        var clash = GetByName(artifact.Kind, artifact.Name);
        if (clash is not null && clash.Id != artifact.Id)
        {
            throw new DuplicateArtifactNameException(artifact.Kind, artifact.Name);
        }

        Artifacts[artifact.Id] = artifact;
    }

    public void Remove(string id)
    {
        Artifacts.Remove(id);
        foreach (var r in Resources.Values.Where(r => r.ArtifactId == id).ToList())
        {
            Resources.Remove(r.Id);
        }
    }

    public IReadOnlyList<FrameworkResource> ListResources(string artifactId) =>
        Resources.Values.Where(r => r.ArtifactId == artifactId).OrderBy(r => r.CreatedUtc).ToList();

    public FrameworkResource? GetResource(string resourceId) => Resources.GetValueOrDefault(resourceId);

    public void AddResource(FrameworkResource resource)
    {
        if (resource.SizeBytes > FrameworkArtifactLimits.MaxResourceBytes)
        {
            throw new ResourceTooLargeException(resource.FileName, resource.SizeBytes);
        }

        Resources[resource.Id] = resource;
    }

    public void RemoveResource(string resourceId) => Resources.Remove(resourceId);
}

/// <summary>A model resolver that returns the curated catalog synchronously - enough to drive the form UI.</summary>
internal sealed class FakeModelListResolver : IModelListResolver
{
    public Task<ModelListResult> ResolveAsync(AgentTool tool, CancellationToken cancellationToken = default)
    {
        var models = AgentModelCatalog.For(tool);
        return Task.FromResult(new ModelListResult(tool, models, ModelListSource.Catalog, "test-catalog"));
    }
}
