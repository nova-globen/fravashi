using Fravashi.Application.Framework;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Framework;

/// <summary>
/// Resolves a runtime's model list probe-then-catalog (D03-12): try the live <see cref="IModelListProbe"/>
/// first, and fall back to the dated <see cref="AgentModelCatalog"/> when the runtime cannot list live or the
/// probe fails. The result records which source won so the UI attributes it honestly.
/// </summary>
public sealed class ModelListResolver(IModelListProbe probe) : IModelListResolver
{
    public async Task<ModelListResult> ResolveAsync(
        AgentTool tool, CancellationToken cancellationToken = default)
    {
        var probed = await probe.TryListAsync(tool, cancellationToken).ConfigureAwait(false);
        if (probed is { Count: > 0 })
        {
            var models = probed.Select(id => new ModelOption(id)).ToList();
            return new ModelListResult(tool, models, ModelListSource.Probe, "Listed live by the CLI.");
        }

        var catalog = AgentModelCatalog.For(tool);
        var source = catalog.Count > 0 ? ModelListSource.Catalog : ModelListSource.None;
        return new ModelListResult(tool, catalog, source, AgentModelCatalog.ProvenanceFor(tool));
    }
}
