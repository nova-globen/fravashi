using System.Globalization;
using Fravashi.Application.Sessions;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// Assembles the governance facts from the stores that hold them (backlog 85).
/// <para>
/// The readiness checks are the point of this type. A gate that only asks whether a LINK exists is cleared
/// by binding to a cancelled item and an empty plan, which is a gate that teaches people to click past it.
/// So the item has to be open, the plan has to say something, and the plan has to mention the work.
/// </para>
/// <para>
/// Every read is wrapped: an unreadable store answers with no facts at all, which holds the gate. This is
/// the one place in the chain that touches four stores, so it is also the one most able to fail, and
/// failing open here would undo all of it.
/// </para>
/// </summary>
public sealed class SessionGovernance(
    ISessionWorkItems work,
    ISessionPlans plans,
    ITrackerResolver trackers,
    IWikiDocuments wiki,
    ISessionChangedFiles changed,
    ILogger<SessionGovernance>? logger = null) : ISessionGovernance
{
    private readonly ILogger _logger = logger ?? NullLogger<SessionGovernance>.Instance;

    /// <inheritdoc />
    public WorkGateFacts Facts(AgentSessionId session, string? attemptedPath)
    {
        try
        {
            return Assemble(session, attemptedPath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fail closed. Four stores is four things that can be unreadable, and a gate that opened when
            // the studio could not answer would be a gate that opened exactly when something was wrong.
            _logger.LogWarning(ex, "The studio could not work out what session {SessionId} is bound to.", session);
            return WorkGateFacts.None;
        }
    }

    /// <inheritdoc />
    public void RecordApprovedChange(AgentSessionId session, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            changed.Record(session, Normalise(path));
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            // A change that was approved and could not be counted is a budget that reads low, never high.
            _logger.LogWarning(ex, "The studio could not record an approved change for session {SessionId}.", session);
        }
    }

    /// <inheritdoc />
    public void Forget(AgentSessionId session) => changed.Forget(session);

    private WorkGateFacts Assemble(AgentSessionId session, string? attemptedPath)
    {
        var bound = work.For(session);
        if (bound.Count == 0)
        {
            return WorkGateFacts.None;
        }

        // Backlog 121: each item is read from the project its LINK was made in. The link row carries it, so a
        // gate never looks a session's item up in a graph the session was not bound in.
        var items = new List<WorkItem>();
        foreach (var entry in bound)
        {
            if (trackers.For(entry.Project).GetAsync(entry.Item).GetAwaiter().GetResult() is { } item)
            {
                items.Add(item);
            }
        }

        // An item the tracker no longer has is not workable, and neither is a closed one. "At least one"
        // rather than "all": a session working on two items, one of which is finished, is still working.
        var workable = items.Where(i => i.Status.IsOpen()).ToList();

        var linkedPlans = new List<WikiItem>();
        foreach (var entry in plans.For(session))
        {
            if (wiki.GetAsync(entry.WikiItemId).GetAwaiter().GetResult() is { } plan)
            {
                linkedPlans.Add(plan);
            }
        }

        var withContent = linkedPlans.Where(p => !string.IsNullOrWhiteSpace(p.Body)).ToList();

        // "Covers" means the plan names the work, in its typed links or in its prose. Either is the author
        // saying so; insisting on one form would make the other look like a mistake.
        var numbers = items.Select(i => i.Number).ToHashSet();
        var covering = withContent.Where(p => Covers(p, numbers)).ToList();

        var path = string.IsNullOrWhiteSpace(attemptedPath) ? null : Normalise(attemptedPath);

        return new WorkGateFacts(
            HasWork: true,
            WorkIsWorkable: workable.Count > 0,
            HasPlan: linkedPlans.Count > 0,
            PlanHasContent: withContent.Count > 0,
            PlanCoversWork: covering.Count > 0,
            InDeclaredScope: InScope(workable, path),
            WithinChangeBudget: WithinBudget(session, workable, path));
    }

    private static bool Covers(WikiItem plan, HashSet<int> numbers) =>
        plan.Links.Any(l => l.Kind == WikiLinkKind.WorkItem && l.Number is { } n && numbers.Contains(n))
        || plan.BodyReferences.Any(r => r.Kind == WikiReferenceKind.WorkItem && numbers.Contains(r.Number));

    /// <summary>
    /// Whether the change is inside what the work declares. Every workable item gets a say, and one of them
    /// allowing it is enough: a session doing two things may touch either one's area.
    /// </summary>
    private static bool InScope(IReadOnlyList<WorkItem> items, string? path)
    {
        var declaring = items.Where(i => i.ScopePatterns.Count > 0).ToList();
        if (declaring.Count == 0 || path is null)
        {
            // Nothing declared, or a call that names nowhere: there is nothing for it to be outside of.
            return true;
        }

        return declaring.Any(i => ScopeMatch.IsAllowed(i.ScopePatterns, path));
    }

    /// <summary>
    /// Whether changing this file keeps the session inside the SMALLEST budget its work declares. A file
    /// already changed costs nothing to change again: a budget is about how much of the workspace one
    /// attempt touches, not how many times it touches it.
    /// </summary>
    private bool WithinBudget(AgentSessionId session, IReadOnlyList<WorkItem> items, string? path)
    {
        var budgets = items
            .Select(i => i.ChangeBudget.MaxFiles)
            .Where(m => m is > 0)
            .Select(m => m!.Value)
            .ToList();

        if (budgets.Count == 0)
        {
            return true;
        }

        var ceiling = budgets.Min();
        if (path is not null && changed.Contains(session, path))
        {
            return true;
        }

        return changed.Count(session) + 1 <= ceiling;
    }

    /// <summary>One spelling for a path, so the same file is not counted twice.</summary>
    private static string Normalise(string path) =>
        path.Replace('\\', '/').Trim().TrimEnd('/');
}

/// <summary>The approved-change rows (schema step 18).</summary>
public sealed class SqliteSessionChangedFiles(
    ISqliteConnectionFactory factory,
    TimeProvider clock,
    ILogger<SqliteSessionChangedFiles>? logger = null) : ISessionChangedFiles
{
    private readonly ILogger _logger = logger ?? NullLogger<SqliteSessionChangedFiles>.Instance;

    /// <inheritdoc />
    public int Count(AgentSessionId session)
    {
        try
        {
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM agent_session_changed_files WHERE session_id = $s;";
            command.Parameters.AddWithValue("$s", Key(session));
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            // A count that cannot be read is reported as the largest it could be, so the budget holds.
            _logger.LogWarning(ex, "The studio could not count what session {SessionId} has changed.", session);
            return int.MaxValue;
        }
    }

    /// <inheritdoc />
    public bool Contains(AgentSessionId session, string path)
    {
        try
        {
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT EXISTS(SELECT 1 FROM agent_session_changed_files WHERE session_id = $s AND path = $p);";
            command.Parameters.AddWithValue("$s", Key(session));
            command.Parameters.AddWithValue("$p", path ?? string.Empty);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "The studio could not read what session {SessionId} has changed.", session);
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Paths(AgentSessionId session)
    {
        try
        {
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT path FROM agent_session_changed_files WHERE session_id = $s ORDER BY first_utc, rowid;";
            command.Parameters.AddWithValue("$s", Key(session));
            using var reader = command.ExecuteReader();

            var paths = new List<string>();
            while (reader.Read())
            {
                paths.Add(reader.GetString(0));
            }

            return paths;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            // An unreadable list is EMPTY here, not "everything": committing a guess would be worse than
            // committing nothing, and nothing is what the caller then does.
            _logger.LogWarning(ex, "The studio could not list what session {SessionId} changed.", session);
            return [];
        }
    }

    /// <inheritdoc />
    public void Record(AgentSessionId session, string path)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO agent_session_changed_files (session_id, path, first_utc) VALUES ($s, $p, $at) "
            + "ON CONFLICT (session_id, path) DO NOTHING;";
        command.Parameters.AddWithValue("$s", Key(session));
        command.Parameters.AddWithValue("$p", path ?? string.Empty);
        command.Parameters.AddWithValue("$at", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void Forget(AgentSessionId session)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_session_changed_files WHERE session_id = $s;";
        command.Parameters.AddWithValue("$s", Key(session));
        command.ExecuteNonQuery();
    }

    private static string Key(AgentSessionId session) => session.Value.ToString("D");
}
