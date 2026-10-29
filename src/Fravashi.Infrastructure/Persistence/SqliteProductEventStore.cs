using System.Globalization;
using Fravashi.Application.Telemetry;
using Fravashi.Domain.Telemetry;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IProductEventStore"/> over the <c>product_events</c> table (schema v10, backlog 74).
/// Stamps use the round-trip "O" format like every other timestamp column here, and the enums are
/// stored by NAME so a bundle is readable without the code that wrote it.
/// <para>
/// Reading is defensive in one direction only: an unrecognised kind or outcome name is skipped rather
/// than guessed at, and a workspace column that is not a hash reads back as no workspace
/// (<see cref="WorkspaceHash.Parse"/>). A hand-edited row therefore cannot put text into a bundle.
/// </para>
/// </summary>
public sealed class SqliteProductEventStore(ISqliteConnectionFactory factory) : IProductEventStore
{
    private readonly ISqliteConnectionFactory _factory = factory;

    public void Append(ProductEvent productEvent)
    {
        ArgumentNullException.ThrowIfNull(productEvent);

        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO product_events (kind, occurred_utc, outcome, workspace_hash, duration_ms, count) " +
            "VALUES ($kind, $at, $outcome, $ws, $duration, $count);";
        command.Parameters.AddWithValue("$kind", productEvent.Kind.ToString());
        command.Parameters.AddWithValue(
            "$at", productEvent.OccurredUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$outcome", productEvent.Outcome.ToString());
        command.Parameters.AddWithValue(
            "$ws", productEvent.Workspace.HasValue ? productEvent.Workspace.Value : (object)DBNull.Value);
        command.Parameters.AddWithValue("$duration", (object?)productEvent.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$count", (object?)productEvent.Count ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ProductEvent> Recent(int max = 2000)
    {
        var events = new List<ProductEvent>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT kind, occurred_utc, outcome, workspace_hash, duration_ms, count " +
            "FROM product_events ORDER BY id DESC LIMIT $max;";
        command.Parameters.AddWithValue("$max", Math.Max(0, max));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Read(reader) is { } recorded)
            {
                events.Add(recorded);
            }
        }

        return events;
    }

    /// <summary>
    /// Backlog 36. The <c>(kind, occurred_utc)</c> index carries both halves of this predicate. The stamp
    /// bound is applied in SQL with a day of slack and then tested exactly in C#: <c>occurred_utc</c> is
    /// round-trip text carrying its own offset, so its string order is only its chronological order while
    /// every row shares an offset, and a boundary is not a thing to decide on that assumption.
    /// </summary>
    public IReadOnlyList<ProductEvent> Since(ProductEventKind kind, DateTimeOffset fromUtc, int max = 20000)
    {
        var events = new List<ProductEvent>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT kind, occurred_utc, outcome, workspace_hash, duration_ms, count " +
            "FROM product_events WHERE kind = $kind AND occurred_utc >= $from " +
            "ORDER BY id DESC LIMIT $max;";
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue(
            "$from",
            fromUtc.ToUniversalTime().AddDays(-1).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$max", Math.Max(0, max));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Read(reader) is { } recorded && recorded.OccurredUtc >= fromUtc)
            {
                events.Add(recorded);
            }
        }

        return events;
    }

    public long Count()
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM product_events;";
        return command.ExecuteScalar() is long count ? count : 0;
    }

    private static ProductEvent? Read(SqliteDataReader reader)
    {
        if (!Enum.TryParse<ProductEventKind>(reader.GetString(0), ignoreCase: false, out var kind)
            || !Enum.TryParse<ProductEventOutcome>(reader.GetString(2), ignoreCase: false, out var outcome)
            || !DateTimeOffset.TryParse(
                reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
        {
            return null;
        }

        return new ProductEvent(
            kind,
            at,
            outcome,
            WorkspaceHash.Parse(reader.IsDBNull(3) ? null : reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5));
    }
}
