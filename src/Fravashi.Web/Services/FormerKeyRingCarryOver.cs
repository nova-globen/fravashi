using System.Globalization;
using System.Security.Cryptography;
using Fravashi.Domain;
using Fravashi.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Fravashi.Web.Services;

/// <summary>What <see cref="FormerKeyRingCarryOver.Run"/> did.</summary>
/// <param name="AlreadyDone">True when an earlier start had done it, so nothing was read.</param>
/// <param name="Carried">The references protected again under the current name.</param>
/// <param name="Current">The references that already read under the current name and were left alone.</param>
/// <param name="Unreadable">The references that read under neither name: left exactly as they were, and reported.</param>
public sealed record FormerKeyRingResult(
    bool AlreadyDone, IReadOnlyList<string> Carried, IReadOnlyList<string> Current, IReadOnlyList<string> Unreadable);

/// <summary>
/// The bridge release's carry-over of every stored secret from the Data Protection application name and purpose the
/// studio had before the internal rename (<see cref="FormerNames.DataProtectionApplicationName"/>,
/// <see cref="FormerNames.CredentialsPurpose"/>) to the current ones. A changed application name changes every derived
/// key, so without this every token in the secret store would silently stop decrypting at the first provider call.
/// <para>
/// It runs at startup, after the container is built and before anything reads a secret (the secret bridge is attached
/// after it). Each value that already reads under the current name is left alone; each that reads under the former one
/// is protected again under the current one; all of it is written in ONE transaction together with the marker that says
/// it was done, so it runs once and a failure part way changes nothing. A value that reads under neither name is left
/// untouched and reported. The written values are read back under the current name before the step is called done.
/// The cleanup release (plan phase 5) deletes this class.
/// </para>
/// </summary>
public static class FormerKeyRingCarryOver
{
    /// <summary>The settings-table key that records when the carry-over was done.</summary>
    public const string DoneKey = "__former_key_ring_carried_utc";

    /// <summary>The protector for <paramref name="purpose"/> under the FORMER application name, over the same key ring.</summary>
    public static IDataProtector FormerProtector(string keysPath, string purpose = FormerNames.CredentialsPurpose) =>
        DataProtectionProvider.Create(
                new DirectoryInfo(keysPath),
                builder => builder.SetApplicationName(FormerNames.DataProtectionApplicationName))
            .CreateProtector(purpose);

    /// <summary>Carries every stored secret over, once. Throws only when the database itself cannot be written.</summary>
    /// <param name="factory">The studio's database.</param>
    /// <param name="current">The secret store's protector under the current name and purpose.</param>
    /// <param name="former">The same purpose under the former name (<see cref="FormerProtector"/>).</param>
    /// <param name="log">Where the outcome is said.</param>
    /// <param name="clock">The clock the done marker is stamped with.</param>
    public static FormerKeyRingResult Run(
        ISqliteConnectionFactory factory, IDataProtector current, IDataProtector former, Action<string> log, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(former);
        ArgumentNullException.ThrowIfNull(log);

        using var connection = factory.Open();
        if (!TableExists(connection, "secrets") || !TableExists(connection, "settings"))
        {
            return new FormerKeyRingResult(false, [], [], []);
        }

        if (ReadSetting(connection, DoneKey) is not null)
        {
            return new FormerKeyRingResult(true, [], [], []);
        }

        var stored = new List<(string Reference, string Value)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT reference, protected_value FROM secrets ORDER BY reference;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                stored.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var carried = new List<(string Reference, string Value)>();
        var alreadyCurrent = new List<string>();
        var unreadable = new List<string>();
        foreach (var (reference, value) in stored)
        {
            if (TryUnprotect(current, value) is not null)
            {
                alreadyCurrent.Add(reference);
            }
            else if (TryUnprotect(former, value) is { } plaintext)
            {
                carried.Add((reference, current.Protect(plaintext)));
            }
            else
            {
                unreadable.Add(reference);
            }
        }

        using (var transaction = connection.BeginTransaction())
        {
            foreach (var (reference, value) in carried)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE secrets SET protected_value = $val WHERE reference = $ref;";
                update.Parameters.AddWithValue("$val", value);
                update.Parameters.AddWithValue("$ref", reference);
                update.ExecuteNonQuery();
            }

            using var done = connection.CreateCommand();
            done.Transaction = transaction;
            done.CommandText =
                "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            done.Parameters.AddWithValue("$k", DoneKey);
            done.Parameters.AddWithValue("$v", (clock ?? TimeProvider.System).GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            done.ExecuteNonQuery();
            transaction.Commit();
        }

        // Verified, not assumed: every value written reads back under the current name.
        foreach (var (reference, _) in carried)
        {
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT protected_value FROM secrets WHERE reference = $ref;";
            check.Parameters.AddWithValue("$ref", reference);
            if (check.ExecuteScalar() is not string written || TryUnprotect(current, written) is null)
            {
                log($"Secret '{reference}' was carried over to the new key name but does not read back; enter it again in Settings.");
            }
        }

        if (carried.Count > 0 || unreadable.Count > 0)
        {
            log($"Carried {carried.Count} stored secret(s) over from the key name before the rename to {ProductName.Short}; "
                + $"{alreadyCurrent.Count} already used the new one."
                + (unreadable.Count == 0
                    ? string.Empty
                    : $" {unreadable.Count} could not be read under either name and were left as they are "
                        + $"({string.Join(", ", unreadable)}); enter those again in Settings."));
        }

        return new FormerKeyRingResult(false, carried.Select(c => c.Reference).ToList(), alreadyCurrent, unreadable);
    }

    private static string? TryUnprotect(IDataProtector protector, string value)
    {
        try
        {
            return protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $t;";
        command.Parameters.AddWithValue("$t", table);
        return command.ExecuteScalar() is not null;
    }

    private static string? ReadSetting(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $k;";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }
}
