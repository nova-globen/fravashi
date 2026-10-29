using Fravashi.Application.Persistence;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// <see cref="ISecretStore"/> over the <c>secrets</c> table. Plaintext is protected via
/// <see cref="ISecretProtector"/> before it touches the database and unprotected on the way out, so the
/// stored column never holds a readable token.
/// </summary>
public sealed class SqliteSecretStore : ISecretStore
{
    private readonly ISqliteConnectionFactory _factory;
    private readonly ISecretProtector _protector;

    public SqliteSecretStore(ISqliteConnectionFactory factory, ISecretProtector protector)
    {
        _factory = factory;
        _protector = protector;
    }

    public void Set(string reference, string plaintext)
    {
        var protectedValue = _protector.Protect(plaintext);

        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO secrets (reference, protected_value, updated_utc) " +
            "VALUES ($ref, $val, $updated) " +
            "ON CONFLICT(reference) DO UPDATE SET protected_value = excluded.protected_value, updated_utc = excluded.updated_utc;";
        command.Parameters.AddWithValue("$ref", reference);
        command.Parameters.AddWithValue("$val", protectedValue);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public string? Get(string reference)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT protected_value FROM secrets WHERE reference = $ref;";
        command.Parameters.AddWithValue("$ref", reference);
        return command.ExecuteScalar() is string protectedValue
            ? _protector.Unprotect(protectedValue)
            : null;
    }

    public void Remove(string reference)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM secrets WHERE reference = $ref;";
        command.Parameters.AddWithValue("$ref", reference);
        command.ExecuteNonQuery();
    }
}
