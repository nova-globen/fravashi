using System.Diagnostics;

using Fravashi.Application.Onboarding.Auth;
using Fravashi.Domain;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// The studio's half of the bridge release (<c>docs/plans/rename-internal-names.md</c>, phase 2): every stored secret is
/// carried from the Data Protection name before the internal rename to the current one, once and in one transaction,
/// against a REAL key ring on disk; the stored credential envelope reads under its former marker; and the window's
/// browser storage keys are copied to their new names once, by bootstrap.js itself.
/// </summary>
public sealed class FormerNamesStudioTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fravashi-former-studio", Guid.NewGuid().ToString("N"));

    public FormerNamesStudioTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Keys => Path.Combine(_directory, "keys");

    /// <summary>The studio's secret-store protector, as Program.cs builds it: the current name, over the key ring on disk.</summary>
    private IDataProtector Current() =>
        DataProtectionProvider.Create(new DirectoryInfo(Keys), b => b.SetApplicationName("Fravashi"))
            .CreateProtector(DataProtectionSecretProtector.Purpose);

    private SqliteConnectionFactory Store()
    {
        var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(_directory, "store.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(factory);
        return factory;
    }

    private static void Put(SqliteConnectionFactory factory, string reference, string protectedValue)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO secrets (reference, protected_value, updated_utc) VALUES ($r, $v, '2026-10-01');";
        command.Parameters.AddWithValue("$r", reference);
        command.Parameters.AddWithValue("$v", protectedValue);
        command.ExecuteNonQuery();
    }

    private static string Stored(SqliteConnectionFactory factory, string reference)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT protected_value FROM secrets WHERE reference = $r;";
        command.Parameters.AddWithValue("$r", reference);
        return (string)command.ExecuteScalar()!;
    }

    [Fact]
    public void Every_secret_protected_under_the_former_name_reads_under_the_current_one_afterwards()
    {
        var factory = Store();
        var former = FormerKeyRingCarryOver.FormerProtector(Keys);
        Put(factory, "jira", former.Protect("jira-token"));
        Put(factory, "github", former.Protect("github-token"));
        Put(factory, "already", Current().Protect("already-current"));
        Put(factory, "garbage", "not a protected value at all");
        var said = new List<string>();

        var result = FormerKeyRingCarryOver.Run(factory, Current(), former, said.Add);

        Assert.Equal(["github", "jira"], result.Carried.Order().ToArray());
        Assert.Equal(["already"], result.Current);
        Assert.Equal(["garbage"], result.Unreadable);
        var secrets = new SqliteSecretStore(factory, new DataProtectionSecretProtector(
            DataProtectionProvider.Create(new DirectoryInfo(Keys), b => b.SetApplicationName("Fravashi"))));
        Assert.Equal("jira-token", secrets.Get("jira"));
        Assert.Equal("github-token", secrets.Get("github"));
        Assert.Equal("already-current", secrets.Get("already"));
        Assert.Equal("not a protected value at all", Stored(factory, "garbage")); // read under neither name: left as it is
        var line = Assert.Single(said);
        Assert.Contains("Carried 2", line);
        Assert.Contains("garbage", line);
    }

    [Fact]
    public void It_runs_once_and_a_second_start_reads_nothing()
    {
        var factory = Store();
        var former = FormerKeyRingCarryOver.FormerProtector(Keys);
        Put(factory, "jira", former.Protect("jira-token"));
        FormerKeyRingCarryOver.Run(factory, Current(), former, _ => { });
        var carried = Stored(factory, "jira");

        var again = FormerKeyRingCarryOver.Run(factory, Current(), former, _ => { });

        Assert.True(again.AlreadyDone);
        Assert.Equal(carried, Stored(factory, "jira")); // never protected twice
    }

    [Fact]
    public void Without_a_former_secret_the_store_is_left_as_it_was()
    {
        var factory = Store();
        var current = Current();
        Put(factory, "jira", current.Protect("jira-token"));
        var before = Stored(factory, "jira");
        var said = new List<string>();

        var result = FormerKeyRingCarryOver.Run(factory, current, FormerKeyRingCarryOver.FormerProtector(Keys), said.Add);

        Assert.Empty(result.Carried);
        Assert.Equal(before, Stored(factory, "jira"));
        Assert.Empty(said); // nothing happened, so nothing is said
    }

    [Fact]
    public void The_former_name_derives_other_keys_so_the_carry_over_is_needed_at_all()
    {
        // The reason this exists: the same key ring under a different application name cannot read the value.
        var former = FormerKeyRingCarryOver.FormerProtector(Keys);
        var value = former.Protect("token");

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => Current().Unprotect(value));
    }

    [Fact]
    public void A_credential_stored_under_the_former_envelope_marker_reads_as_that_credential()
    {
        var current = CredentialEnvelope.Serialize(new StoredCredential("access-1", "refresh-1", null, AuthMethods.Pat));
        var stored = current.Replace("\"fravashi.cred.v1\"", "\"" + FormerNames.CredentialEnvelopeMarker + "\"", StringComparison.Ordinal);
        Assert.Contains(FormerNames.CredentialEnvelopeMarker, stored); // the test must really hold the former marker

        var parsed = CredentialEnvelope.Parse(stored)!;

        Assert.Equal("access-1", parsed.AccessToken); // not the whole envelope read as a raw token
        Assert.Equal("refresh-1", parsed.RefreshToken);
    }

    // ---- the window's browser storage, run through bootstrap.js itself -------------------------------------------------

    private static string? Node()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "node.exe", "node" })
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string Bootstrap()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "src", "Fravashi.Web", "wwwroot", "bootstrap.js");
    }

    /// <summary>Runs bootstrap.js under node with a stand-in page and localStorage; returns the storage afterwards as JSON.</summary>
    private string RunBootstrap(string storageJson)
    {
        var harness = Path.Combine(_directory, "harness.js");
        File.WriteAllText(harness, $$"""
            const fs = require('fs');
            const vm = require('vm');
            const data = new Map(Object.entries({{storageJson}}));
            const localStorage = {
              get length() { return data.size; },
              key(i) { return Array.from(data.keys())[i] ?? null; },
              getItem(k) { return data.has(k) ? data.get(k) : null; },
              setItem(k, v) { data.set(k, String(v)); },
              removeItem(k) { data.delete(k); },
            };
            const attributes = { 'data-former-storage': '{{FormerNames.BrowserStoragePrefix}}' };
            const documentElement = {
              getAttribute: (n) => attributes[n] ?? null,
              setAttribute: (n, v) => { attributes[n] = v; },
              style: { setProperty: () => {} },
              classList: { add: () => {} },
            };
            const context = { window: {}, document: { documentElement }, localStorage };
            vm.runInNewContext(fs.readFileSync(process.argv[2], 'utf8'), context);
            process.stdout.write(JSON.stringify(Object.fromEntries(data)) + '\n' + JSON.stringify(attributes));
            """);
        var start = new ProcessStartInfo(Node()!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(harness);
        start.ArgumentList.Add(Bootstrap());
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    [Fact]
    public void Bootstrap_copies_every_former_storage_key_to_its_new_name_once_before_the_theme_is_read()
    {
        if (Node() is null)
        {
            return; // No node on this machine (the container image): the structure test below still holds the attribute.
        }

        var former = FormerNames.BrowserStoragePrefix;
        var output = RunBootstrap($$"""
            {
              "{{former}}-theme": "{\"mode\":\"dark\"}",
              "{{former}}-code-backup:local:src/a.cs": "unsaved buffer",
              "{{former}}-editor-fontsize": "15",
              "{{former}}-page-sidebar-collapsed": "1",
              "fravashi-editor-fontsize": "13",
              "unrelated": "x"
            }
            """);

        Assert.Contains("\"fravashi-theme\":\"{\\\"mode\\\":\\\"dark\\\"}\"", output);
        Assert.Contains("\"fravashi-code-backup:local:src/a.cs\":\"unsaved buffer\"", output); // unsaved editor buffers survive
        Assert.Contains("\"fravashi-editor-fontsize\":\"13\"", output); // a key already under its new name wins
        Assert.Contains("\"fravashi-former-storage-carried\":\"1\"", output);
        Assert.Contains($"\"{former}-theme\"", output); // the former keys are left as they are
        Assert.Contains("\"data-theme-mode\":\"dark\"", output); // the carried theme is applied before first paint
    }

    [Fact]
    public void Bootstrap_carries_over_only_once_so_a_removed_key_never_comes_back()
    {
        if (Node() is null)
        {
            return;
        }

        var former = FormerNames.BrowserStoragePrefix;
        var output = RunBootstrap($$"""
            { "{{former}}-code-backup:local:src/a.cs": "old buffer", "fravashi-former-storage-carried": "1" }
            """);

        Assert.DoesNotContain("fravashi-code-backup:local:src/a.cs", output);
    }

    [Fact]
    public void The_page_hands_bootstrap_the_former_prefix()
    {
        var app = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Bootstrap()))!, "Components", "App.razor"));
        Assert.Contains("data-former-storage=\"@Fravashi.Domain.FormerNames.BrowserStoragePrefix\"", app);
        var script = File.ReadAllText(Bootstrap());
        // The carry-over runs before anything reads a key.
        Assert.True(script.IndexOf("data-former-storage", StringComparison.Ordinal)
            < script.IndexOf("localStorage.getItem('fravashi-theme')", StringComparison.Ordinal));
    }
}
