using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// One step in the ordered schema-migration chain: the delta that takes the database from
/// <c>Version - 1</c> to <see cref="Version"/>. <see cref="Apply"/> runs inside a transaction the
/// <see cref="SchemaMigrator"/> owns, and the version marker is stamped in that same transaction, so a
/// step that throws rolls back whole and never leaves the marker ahead of the shape (backlog 60).
/// </summary>
/// <remarks>
/// A step must be idempotent-safe: it may run against a database whose recorded version is inaccurate
/// (a pre-marker database, or a fresh install replaying the whole chain), so table creation guards with
/// <c>IF NOT EXISTS</c> and column additions probe the table first. The list is append-only: a shipped
/// step's SQL is never edited, because databases in the field have already run it; a new shape is a new
/// step with the next version number.
/// </remarks>
public sealed record SchemaMigration(int Version, Action<SqliteConnection, SqliteTransaction> Apply);
