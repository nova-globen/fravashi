using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Framework;

/// <summary>
/// A live probe that asks a runtime's CLI to list its available models. Returns null when the runtime has no
/// scriptable list-models command, or when the probe fails (offline, not installed, parse error) - never a
/// faked list. No runtime this version offers has such a command, so every call returns null today and the
/// resolver falls back to the catalog (D03-12); the seam stays because the answer is per runtime, not
/// per product (see docs/v2.md section 1 for the one that had one).
/// </summary>
public interface IModelListProbe
{
    /// <summary>The model ids the CLI reports, or null when unsupported/failed.</summary>
    Task<IReadOnlyList<string>?> TryListAsync(AgentTool tool, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the model list for a runtime in the order <b>probe → catalog</b> (D03-12): the live probe when the
/// runtime can list its models, else the dated curated catalog. The result carries its
/// <see cref="ModelListSource"/> so the UI can attribute it honestly.
/// </summary>
public interface IModelListResolver
{
    /// <summary>Resolves the available models for one runtime, probe-first with catalog fallback.</summary>
    Task<ModelListResult> ResolveAsync(AgentTool tool, CancellationToken cancellationToken = default);
}
