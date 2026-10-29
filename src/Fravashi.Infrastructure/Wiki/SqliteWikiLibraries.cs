using Fravashi.Application.Wiki;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Persistence;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// <see cref="IWikiLibraries"/> over the studio's database (backlog 121). A project's wiki is a view with no
/// state of its own, so one is made per request rather than kept: there is nothing to cache and nothing to go
/// stale when a project is created.
/// </summary>
public sealed class SqliteWikiLibraries(ISqliteConnectionFactory factory, TimeProvider clock) : IWikiLibraries
{
    /// <inheritdoc />
    public IWikiLibrary For(ProjectId project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new SqliteWikiLibrary(factory, clock, project);
    }

    /// <inheritdoc />
    public IWikiDocuments Documents { get; } = new SqliteWikiLibrary(factory, clock, project: null);
}
