using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// <see cref="IWorkspaceTrust"/> over the <c>workspace_trust</c> table (schema step 34). One row per folder that
/// was ever set above Ask; no row is Ask. Reading fails toward Ask, the same direction the roles store fails
/// toward its most restrictive role: an unreadable answer is the one that asks.
/// </summary>
public sealed class SqliteWorkspaceTrust(
    ISqliteConnectionFactory factory,
    ILogger<SqliteWorkspaceTrust>? logger = null) : IWorkspaceTrust
{
    private readonly ILogger _logger = logger ?? NullLogger<SqliteWorkspaceTrust>.Instance;

    /// <inheritdoc />
    public event Action<string>? Changed;

    /// <inheritdoc />
    public WorkspaceTrust For(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return WorkspaceTrust.Ask;
        }

        try
        {
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT trust FROM workspace_trust WHERE workspace_id = $w;";
            command.Parameters.AddWithValue("$w", workspaceId);
            return command.ExecuteScalar() is string name ? WorkspaceTrustLevels.Parse(name) : WorkspaceTrust.Ask;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "The studio could not read the trust level of workspace {WorkspaceId}.", workspaceId);
            return WorkspaceTrust.Ask;
        }
    }

    /// <inheritdoc />
    public void Set(string workspaceId, WorkspaceTrust trust)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        if (trust == WorkspaceTrust.Ask)
        {
            command.CommandText = "DELETE FROM workspace_trust WHERE workspace_id = $w;";
        }
        else
        {
            command.CommandText =
                "INSERT INTO workspace_trust (workspace_id, trust) VALUES ($w, $t) "
                + "ON CONFLICT (workspace_id) DO UPDATE SET trust = $t;";
            command.Parameters.AddWithValue("$t", WorkspaceTrustLevels.Name(trust));
        }

        command.Parameters.AddWithValue("$w", workspaceId);
        command.ExecuteNonQuery();

        Changed?.Invoke(workspaceId);
    }
}
