using Fravashi.Application.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// Registers the SQLite persistence layer: the connection factory, the config/provider/secret/onboarding
/// stores, and the configuration reloader. It does NOT register <see cref="ISecretProtector"/> - the
/// protector depends on ASP.NET Core Data Protection, which the host (Web) owns and wires up together with
/// the key-ring location on the data volume.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddFravashiPersistence(
        this IServiceCollection services,
        DataDirectory dataDirectory,
        string? vectorExtensionPath = null)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);

        services.AddSingleton(dataDirectory);
        services.AddSingleton(new SqliteStoreOptions(dataDirectory.DatabasePath, vectorExtensionPath));

        // The connection factory bootstraps the schema on first resolution (idempotent CREATE IF NOT EXISTS).
        services.AddSingleton<ISqliteConnectionFactory>(sp =>
        {
            var factory = new SqliteConnectionFactory(sp.GetRequiredService<SqliteStoreOptions>());
            SqliteSchema.EnsureCreated(factory);
            return factory;
        });

        services.AddSingleton<IConfigurationStore, SqliteConfigurationStore>();
        services.AddSingleton<IProviderConnectionStore, SqliteProviderConnectionStore>();
        services.AddSingleton<Fravashi.Application.Framework.IFrameworkArtifactStore, SqliteFrameworkArtifactStore>();
        services.AddSingleton<Fravashi.Application.Wiki.IWikiPageStore, SqliteWikiPageStore>();
        services.AddSingleton<ISecretStore, SqliteSecretStore>();
        services.AddSingleton<IOnboardingState, SqliteOnboardingState>();
        // Backlog 67: the person's scratchpad, one per install, in the database so a backup carries it.
        services.AddSingleton<Fravashi.Application.Scratchpad.IScratchpadStore, Fravashi.Infrastructure.Scratchpad.SqliteScratchpadStore>();
        services.AddSingleton<IWorkspaceStore, SqliteWorkspaceStore>();
        services.AddSingleton<IGitHostStore, SqliteGitHostStore>();

        // Work items (backlog 37, increment B): projects and the workspace binding, and the tracker resolver that
        // hands out a local board per project. The board stamps its rows with the shared clock.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<Fravashi.Application.Projects.IProjectStore, SqliteProjectStore>();
        services.AddSingleton<Fravashi.Application.Projects.IProjectProviders, SqliteProjectProviders>();
        // Item #373: how much each project holds, for the setup's "Join an existing project" list.
        services.AddSingleton<Fravashi.Application.Projects.IProjectContents, SqliteProjectContents>();
        // Item #373: a folder's setup in progress, one settings row per folder.
        services.AddSingleton<Fravashi.Application.Setup.ISetupDraftStore>(sp =>
            new Fravashi.Application.Setup.SetupDraftStore(sp.GetRequiredService<IConfigurationStore>()));
        // Backlog 298 (ADR 0031 decision 3): the per-project autopilot delivery choice.
        services.AddSingleton<Fravashi.Application.Projects.IProjectDelivery, SqliteProjectDelivery>();
        // Backlog 154: the variables a project hands its agents; secrets live in the secret store.
        services.AddSingleton<Fravashi.Application.Projects.IProjectEnvironment>(sp => new SqliteProjectEnvironment(
            sp.GetRequiredService<ISqliteConnectionFactory>(),
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<SqliteProjectEnvironment>>()));
        services.AddSingleton<Fravashi.Application.WorkItems.ITrackerResolver, Fravashi.Infrastructure.WorkItems.LocalTrackerResolver>();

        // Backlog 41: one signal for the process, so the board can show an agent's change as it lands rather
        // than after a reload nobody asked for.
        services.AddSingleton<Fravashi.Application.WorkItems.IWorkItemChanges, Fravashi.Application.WorkItems.WorkItemChanges>();

        // Work item #368: a project's releases, stored with its work items, and the act that records one (stamps its
        // items, then writes the notes as a Release wiki document). Publishing that document is never part of it.
        services.AddSingleton<Fravashi.Application.Releases.IReleaseStores>(sp => new Fravashi.Infrastructure.Releases.SqliteReleaseStores(
            sp.GetRequiredService<ISqliteConnectionFactory>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new Fravashi.Application.Releases.ReleaseRecorder(
            sp.GetRequiredService<Fravashi.Application.Releases.IReleaseStores>(),
            sp.GetRequiredService<Fravashi.Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Fravashi.Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<Fravashi.Application.WorkItems.IWorkItemChanges>()));

        // Backlog 39: the AUTHORED wiki. The v3 wiki_pages mirror and its sync service keep doing their own
        // job; this is the document store the graph is made of, and item 83 decides what flows between them.
        // Backlog 121: a project at a time. There is deliberately NO registration of IWikiLibrary itself: a
        // consumer either names a project (IWikiLibraries.For) or holds a document's id (IWikiDocuments), and a
        // bare library in the container would be a wiki of whichever project somebody guessed.
        services.AddSingleton<Fravashi.Application.Wiki.IWikiLibraries>(sp =>
            new Fravashi.Infrastructure.Wiki.SqliteWikiLibraries(
                sp.GetRequiredService<ISqliteConnectionFactory>(),
                sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<Fravashi.Application.Wiki.IWikiDocuments>(sp =>
            sp.GetRequiredService<Fravashi.Application.Wiki.IWikiLibraries>().Documents);

        // Backlog 49: importing a folder's markdown backlog and documents into its project, through the same ports.
        services.AddSingleton(sp => new Fravashi.Application.Migration.RepositoryImport(
            sp.GetRequiredService<Fravashi.Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Fravashi.Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<TimeProvider>()));
        // Backlog 40: the wiki found by MEANING. The model is a file baked into the image; on a host without
        // one the embedder reports itself unavailable and wiki_find matches words and says so. The model NAME
        // carries the pinned revision, and it is stored beside every vector, so a re-pinned model re-indexes
        // by itself instead of being compared against vectors it did not make.
        services.AddSingleton<Fravashi.Application.Embeddings.ITextEmbedder>(sp =>
        {
            var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            return new Fravashi.Infrastructure.Embeddings.OnnxTextEmbedder(
                configuration[Fravashi.Infrastructure.Embeddings.OnnxTextEmbedder.DirectoryKey],
                configuration["FRAVASHI_EMBED_MODEL"] is { Length: > 0 } named ? named : "unnamed-model",
                sp.GetService<Microsoft.Extensions.Logging.ILogger<Fravashi.Infrastructure.Embeddings.OnnxTextEmbedder>>());
        });
        services.AddSingleton<Fravashi.Application.Wiki.IWikiMeaningSearch>(sp =>
            new Fravashi.Infrastructure.Wiki.SqliteWikiMeaningIndex(
                sp.GetRequiredService<ISqliteConnectionFactory>(),
                sp.GetRequiredService<Fravashi.Application.Embeddings.ITextEmbedder>(),
                sp.GetRequiredService<Fravashi.Application.Wiki.IWikiDocuments>()));
        services.AddHostedService<Fravashi.Infrastructure.Wiki.WikiMeaningIndexer>();

        services.AddSingleton<Fravashi.Application.Wiki.IWikiSummaryStore>(sp =>
            new Fravashi.Infrastructure.Wiki.SqliteWikiSummaryStore(sp.GetRequiredService<ISqliteConnectionFactory>()));
        services.AddSingleton<Fravashi.Application.Wiki.IWikiPublicationStore>(sp =>
            new Fravashi.Infrastructure.Wiki.SqliteWikiPublicationStore(sp.GetRequiredService<ISqliteConnectionFactory>()));
        services.AddSingleton(sp => new Fravashi.Infrastructure.Wiki.WikiReferenceResolver(
            sp.GetRequiredService<Fravashi.Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Fravashi.Application.Wiki.IWikiLibraries>()));
        services.AddSingleton<Fravashi.Infrastructure.Rendering.WikiMarkdownRenderer>();

        // Backlog 82 (ADR 0018 decisions 5, 7 and 8): the provider mirroring sidecar, the durable write
        // outbox, and the two passes over them. Registered even with no provider configured, because the
        // local board is then the source of truth and every one of these is simply empty, which is what the
        // freshness indicator reports as "there is no provider behind this" rather than as a degraded state.
        services.AddSingleton<Fravashi.Application.Mirroring.IMirrorStore, Fravashi.Infrastructure.Mirroring.SqliteMirrorStore>();
        services.AddSingleton<Fravashi.Application.Mirroring.IOutboxStore, Fravashi.Infrastructure.Mirroring.SqliteOutboxStore>();
        services.AddSingleton(sp => new Fravashi.Application.Mirroring.OutboxDrain(
            sp.GetRequiredService<Fravashi.Application.Mirroring.IOutboxStore>(),
            sp.GetRequiredService<Fravashi.Application.Mirroring.IMirrorStore>(),
            sp.GetRequiredService<TimeProvider>()));

        // Backlog 146: a write the previous process left in flight becomes an unknown to look up, once, at start.
        services.AddHostedService<Fravashi.Infrastructure.Mirroring.OutboxRecoveryService>();
        // Backlog 47: what this board's provider statuses mean, from the mapping onboarding collected and
        // nothing consumed until now. Read at each use, so editing it in settings takes effect at once.
        // Registered ONCE and taken from here by everything that needs it. It was written out twice for a
        // few hours and the second reader was missed, which does not fail: it silently reports a board
        // nobody mapped, however carefully the person filled the mapping in.
        services.AddSingleton<Func<Fravashi.Application.Mirroring.ProviderStatusMap>>(
            sp => () => Fravashi.Application.Mirroring.StoredStatusMapping.Read(
                sp.GetRequiredService<Fravashi.Application.Persistence.IConfigurationStore>()));
        services.AddSingleton(sp => new Fravashi.Application.Mirroring.ReconcilePass(
            sp.GetRequiredService<Fravashi.Application.Mirroring.IMirrorStore>(),
            sp.GetRequiredService<Fravashi.Application.Mirroring.IOutboxStore>(),
            sp.GetRequiredService<TimeProvider>(),
            newId: null,
            statuses: sp.GetRequiredService<Func<Fravashi.Application.Mirroring.ProviderStatusMap>>()));
        services.AddSingleton(sp => new Fravashi.Application.Mirroring.GraphRepair(
            sp.GetRequiredService<Fravashi.Application.Mirroring.IMirrorStore>(),
            sp.GetRequiredService<Fravashi.Application.Wiki.IWikiLibraries>()));
        services.AddSingleton<IConfigurationReloader, ConfigurationRootReloader>();

        // Backlog 297 (ADR 0031 decisions 1, 6 and 10): the autopilot run's durable store. Registered now even
        // though nothing creates a run yet (#298 is the dispatch that will), the same way the mirroring stores
        // above are registered before a provider is configured: a port later slices resolve, not a feature with
        // its own UI.
        services.AddSingleton<Fravashi.Application.Autopilot.IAutopilotRunStore>(
            sp => new Fravashi.Infrastructure.Autopilot.SqliteAutopilotRunStore(sp.GetRequiredService<ISqliteConnectionFactory>()));

        return services;
    }
}
