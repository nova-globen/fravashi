using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Persistence;

namespace Fravashi.Infrastructure.WorkItems;

/// <summary>
/// The tracker resolver of backlog item 37 (canonical artifact model plan section 3.1): every project's source of
/// truth is the local board. Item 47 is where a project gains a provider-backed tracker instead.
/// </summary>
public sealed class LocalTrackerResolver(ISqliteConnectionFactory factory, TimeProvider clock) : ITrackerResolver
{
    private readonly ISqliteConnectionFactory _factory = factory;
    private readonly TimeProvider _clock = clock;

    /// <inheritdoc />
    public ITracker For(ProjectId project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new SqliteLocalBoard(project, _factory, _clock);
    }
}
