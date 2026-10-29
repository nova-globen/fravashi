using System.Collections.Frozen;
using Microsoft.AspNetCore.StaticAssets;

namespace Fravashi.Web.Services;

/// <summary>
/// Backlog 283 (plan @101): the one answer to "is this path a static asset", asked by every place that lets an asset
/// past a gate or tells a page from an asset: <see cref="StudioGates"/> (and so both the gate middleware and
/// <c>Components/GateGuard.razor</c>), <see cref="StudioNotFound"/> and <see cref="StudioError"/>.
/// <para>
/// Each of those used to ask <c>Path.HasExtension</c>, so a page whose catch-all route ends in a file name
/// (<c>/code/README.md</c>, <c>/git/history/src/Program.cs</c>) passed every closed gate as if it were a stylesheet. A
/// path is a static asset here only when it is EXACTLY the route of an endpoint MapStaticAssets mapped (its metadata
/// carries a <see cref="StaticAssetDescriptor"/>): the files the app really serves, the vendored Monaco and icon trees
/// and the framework's own scripts included. Its shape never counts, so no page route is exempted by looking like a file.
/// </para>
/// <para>
/// Compared case-insensitively, as routing matches them, against the path as Kestrel decodes it (an encoded slash stays
/// encoded, so it can never spell an asset's route). Read from the app's endpoints on first ask, when every endpoint is
/// mapped; a singleton, so the circuit, which has no HttpContext, asks the same set the request pipeline does.
/// </para>
/// </summary>
public sealed class StaticAssetRoutes
{
    private readonly Lazy<FrozenSet<string>> _routes;

    /// <summary>The routes MapStaticAssets mapped, read from the app's endpoints the first time one is asked about.</summary>
    public StaticAssetRoutes(EndpointDataSource endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        _routes = new Lazy<FrozenSet<string>>(() => Normalised(
            endpoints.Endpoints
                .Select(endpoint => endpoint.Metadata.GetMetadata<StaticAssetDescriptor>()?.Route)
                .OfType<string>()));
    }

    /// <summary>A fixed set of routes (with or without their leading slash), for tests.</summary>
    public StaticAssetRoutes(IEnumerable<string> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var set = Normalised(routes);
        _routes = new Lazy<FrozenSet<string>>(() => set);
    }

    /// <summary>Every route, each with its leading slash.</summary>
    public IReadOnlyCollection<string> All => _routes.Value;

    /// <summary>True only when <paramref name="path"/> is exactly the route of a static asset the app serves.</summary>
    public bool Serves(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _routes.Value.Contains(path);
    }

    private static FrozenSet<string> Normalised(IEnumerable<string> routes) =>
        routes.Select(route => route.StartsWith('/') ? route : "/" + route)
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
