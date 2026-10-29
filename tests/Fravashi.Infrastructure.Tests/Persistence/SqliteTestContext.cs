using Fravashi.Infrastructure.Persistence;

namespace Fravashi.Infrastructure.Tests.Persistence;

/// <summary>
/// A throwaway file-backed SQLite database in a temp folder, with the schema created. File-backed (not
/// in-memory) so connections can open and close per operation exactly as the stores do in production.
/// </summary>
public sealed class SqliteTestContext : IDisposable
{
    private readonly string _directory;

    public SqliteTestContext(string? vectorExtensionPath = null)
    {
        _directory = Path.Combine(Path.GetTempPath(), "fravashi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "test.db");
        // Pooling: false so this context's connections do not sit in the process-wide SQLite pool.
        // xunit runs test classes in parallel, and SqliteConnection.ClearAllPools() is process-global:
        // a pooled connection here could be force-closed by another SQLite-backed class disposing at the
        // same instant. With pooling off each close releases its file handle immediately, so no
        // ClearAllPools call is needed and the temp-dir delete below does not race a lingering handle
        // (backlog 89).
        Factory = new SqliteConnectionFactory(new SqliteStoreOptions(databasePath, vectorExtensionPath, Pooling: false));
        DatabasePath = databasePath;
        SqliteSchema.EnsureCreated(Factory);
    }

    public SqliteConnectionFactory Factory { get; }

    public string DatabasePath { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the temp folder is disposable.
        }
    }
}

/// <summary>Reversible, non-encrypting protector for tests - proves the store never persists plaintext.</summary>
public sealed class ReversibleTestProtector : Fravashi.Application.Persistence.ISecretProtector
{
    public string Protect(string plaintext)
        => "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith("enc:", StringComparison.Ordinal))
        {
            throw new FormatException("Not produced by this protector.");
        }

        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[4..]));
    }
}
