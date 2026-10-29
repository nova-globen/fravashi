using Fravashi.Application.Wiki;
using Fravashi.Domain.Projects;

namespace Fravashi.Web.Tests;

/// <summary>
/// For a test with ONE project (backlog 121): whichever project is asked for, it is this library. The product
/// never does this; it asks <c>SqliteWikiLibraries</c> for the project a window or a session really belongs to.
/// </summary>
internal sealed class OneWiki(IWikiLibrary library) : IWikiLibraries
{
    public IWikiLibrary For(ProjectId project) => library;

    public IWikiDocuments Documents => library;
}

/// <summary>An install with the one seeded project, for a page test that is not about projects.</summary>
internal sealed class OneProjectStore : Fravashi.Application.Projects.IProjectStore
{
    private static readonly Fravashi.Application.Projects.ProjectRecord Only =
        new(ProjectId.Default, "Default", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    public IReadOnlyList<Fravashi.Application.Projects.ProjectRecord> List() => [Only];

    public Fravashi.Application.Projects.ProjectRecord? Get(ProjectId id) => id == Only.Id ? Only : null;

    public Fravashi.Application.Projects.ProjectRecord Create(string name) => throw new NotSupportedException();

    public bool Rename(ProjectId id, string name) => false;

    public bool BindWorkspace(string workspaceId, ProjectId project) => false;

    public IReadOnlyList<Fravashi.Application.Persistence.WorkspaceRecord> WorkspacesOf(ProjectId project) => [];
}

/// <summary>
/// The real <c>ProviderSync</c> over a test's own database (backlog 47), following <paramref name="gateway"/> or,
/// when it is null, no provider at all, which is what every page test had before a provider could be followed.
/// </summary>
internal static class TestProviderSync
{
    public static Fravashi.Application.Mirroring.ProviderSync Over(
        Fravashi.Infrastructure.Persistence.ISqliteConnectionFactory factory,
        Fravashi.Application.Mirroring.IProviderGateway? gateway = null,
        Fravashi.Application.Onboarding.BoardStatusMapping? mapping = null,
        Fravashi.Application.Network.ProviderOutages? outages = null)
    {
        var mirror = new Fravashi.Infrastructure.Mirroring.SqliteMirrorStore(factory);
        var outbox = new Fravashi.Infrastructure.Mirroring.SqliteOutboxStore(factory);
        return new Fravashi.Application.Mirroring.ProviderSync(
            new Following(gateway),
            new Fravashi.Infrastructure.WorkItems.LocalTrackerResolver(factory, TimeProvider.System),
            outbox,
            mirror,
            new Fravashi.Application.Mirroring.OutboxDrain(outbox, mirror, TimeProvider.System),
            new Fravashi.Application.Mirroring.ReconcilePass(
                mirror, outbox, TimeProvider.System, null, () => Fravashi.Application.Mirroring.ProviderStatusMap.From(mapping)),
            TimeProvider.System,
            statuses: () => Fravashi.Application.Mirroring.ProviderStatusMap.From(mapping),
            outages: outages);
    }

    private sealed class Following(Fravashi.Application.Mirroring.IProviderGateway? gateway)
        : Fravashi.Application.Mirroring.IProviderGateways
    {
        public Fravashi.Application.Mirroring.IProviderGateway? For(ProjectId project) => gateway;
    }
}

/// <summary>A provider that accepts every creation and remembers it, named the way a person would see it.</summary>
internal sealed class AcceptingProvider : Fravashi.Application.Mirroring.IProviderGateway
{
    private readonly Dictionary<string, Fravashi.Application.Mirroring.ProviderItem> _byOperation = [];

    public string Connection => "Jira";

    /// <summary>Keys this provider already holds, as key to title, for an import test.</summary>
    public Dictionary<string, string> Holds { get; } = [];

    public bool CanList { get; set; } = true;

    public Fravashi.Application.Mirroring.ProviderCapabilities Capabilities => new(true, true, true, CanList);

    public Task<Fravashi.Application.Mirroring.ProviderResult<Fravashi.Application.Mirroring.ProviderPage>> ListAsync(
        string? cursor, CancellationToken ct = default) =>
        Task.FromResult(Fravashi.Application.Mirroring.ProviderResult<Fravashi.Application.Mirroring.ProviderPage>.Ok(
            new Fravashi.Application.Mirroring.ProviderPage(
                [.. Holds.Select(h => new Fravashi.Application.Mirroring.ProviderItem(
                    h.Key,
                    "v1",
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["Title"] = h.Value,
                        ["Description"] = "As the provider has it.",
                    },
                    "To Do",
                    "new"))],
                null)));

    public List<Fravashi.Application.Mirroring.ProviderItem> Created { get; } = [];

    public Task<Fravashi.Application.Mirroring.ProviderResult<Fravashi.Application.Mirroring.ProviderItem>> CreateAsync(
        string payload, string operationId, CancellationToken ct = default)
    {
        var fields = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(payload)!;
        var item = new Fravashi.Application.Mirroring.ProviderItem("DD-" + (Created.Count + 1), "v1", fields, "To Do");
        _byOperation[operationId] = item;
        Created.Add(item);
        return Task.FromResult(Fravashi.Application.Mirroring.ProviderResult<Fravashi.Application.Mirroring.ProviderItem>.Ok(item));
    }

    public Task<Fravashi.Application.Mirroring.ProviderResult<Fravashi.Application.Mirroring.ProviderItem>> GetAsync(
        string key, CancellationToken ct = default) =>
        Task.FromResult(_byOperation.Values.FirstOrDefault(i => i.Key == key) is { } item
            ? Fravashi.Application.Mirroring.ProviderResult<Fravashi.Application.Mirroring.ProviderItem>.Ok(item)
            : Fravashi.Application.Mirroring.ProviderResult<Fravashi.Application.Mirroring.ProviderItem>.Gone(key + " is not there."));

    public Task<Fravashi.Application.Mirroring.ProviderResult<Fravashi.Application.Mirroring.ProviderItem?>> FindByOperationIdAsync(
        string operationId, CancellationToken ct = default) =>
        Task.FromResult(Fravashi.Application.Mirroring.ProviderResult<Fravashi.Application.Mirroring.ProviderItem?>.Ok(
            _byOperation.GetValueOrDefault(operationId)));

    public Task<Fravashi.Application.Mirroring.ProviderResult<Fravashi.Application.Mirroring.ProviderItem>> UpdateAsync(
        string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) => GetAsync(key, ct);

    public Task<Fravashi.Application.Mirroring.ProviderResult<Fravashi.Application.Mirroring.ProviderItem>> TransitionAsync(
        string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) => GetAsync(key, ct);
}
