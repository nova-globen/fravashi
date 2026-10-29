namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// Settings for the SQLite persistence layer.
/// </summary>
/// <param name="DatabasePath">Absolute path to the SQLite database file.</param>
/// <param name="VectorExtensionPath">
/// Optional path to a loadable SQLite vector extension (e.g. sqlite-vec's <c>vec0</c>). This is the
/// reserved <b>vector seam</b> (ADR 0003 V6): when set, <see cref="SqliteConnectionFactory"/> enables and
/// loads the extension on every connection. It is intentionally NOT integrated now - vector search is a
/// later step - but the load point exists so adopting it requires no change to callers or schema code.
/// </param>
/// <param name="Pooling">
/// Whether connections to this database are pooled (the ADO.NET default is <c>true</c>, which is what
/// production wants: a pooled physical handle avoids reopening the file per operation). Tests set this to
/// <c>false</c> so each connection fully releases its file handle on close and no process-global
/// <c>SqliteConnection.ClearAllPools()</c> is needed to reclaim it (backlog 89).
/// </param>
public sealed record SqliteStoreOptions(string DatabasePath, string? VectorExtensionPath = null, bool Pooling = true);
