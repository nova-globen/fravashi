using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Plan 2e Task 3: the per-session settings file and approval script the CLI is pointed at. The shapes
/// asserted here are the ones the real CLI honored in the 2026-08-04 container spikes: a PreToolUse
/// hook with a catch-all matcher (it fired for Read as well as Write) and a per-hook timeout far above
/// the 60s default (a 90s stall was honored under timeout 600).
/// <para>
/// That the script really denies when the studio is unreachable is proved by RUNNING it in the
/// container, not by reading it; see the plan's Task 3 evidence.
/// </para>
/// </summary>
public class SessionHookWriterTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "fravashi-hooks-" + Guid.NewGuid().ToString("N"));

    private readonly AgentSessionId _session = AgentSessionId.New();

    private SessionHookWriter Writer() => new(_dataDirectory, "http://127.0.0.1:8080/api/agent/approval");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Writing_lays_down_a_settings_file_and_a_script_under_the_session_folder()
    {
        var files = Writer().Write(_session, "s3cret");

        File.Exists(files.SettingsPath).Should().BeTrue();
        File.Exists(files.ScriptPath).Should().BeTrue();
        files.Directory.Should().Be(Path.Combine(_dataDirectory, "agent-sessions", _session.ToString()));
    }

    [Fact]
    public void The_settings_file_registers_a_catch_all_pretooluse_hook_that_runs_the_script()
    {
        var files = Writer().Write(_session, "s3cret");

        using var document = JsonDocument.Parse(File.ReadAllText(files.SettingsPath));
        var entry = document.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0];
        // A catch-all matcher, because the read-only allowlist lives in reviewable C# (decision 5),
        // not in a regex nobody can audit.
        entry.GetProperty("matcher").GetString().Should().Be("*");

        var hook = entry.GetProperty("hooks")[0];
        hook.GetProperty("type").GetString().Should().Be("command");
        hook.GetProperty("command").GetString().Should().Contain(files.ScriptPath);
    }

    /// <summary>
    /// Backlog 174: a session is started with none of what the signed-in ACCOUNT carries. Measured 2026-09-30 in the
    /// container: the account's mail, drive and calendar connectors were CONNECTED in every agent session, beside four
    /// plugins and their skills synced from claude.ai.
    /// </summary>
    [Fact]
    public void The_settings_keep_the_accounts_connectors_plugins_and_skills_out_of_the_session()
    {
        var files = Writer().Write(_session, "s3cret");

        using var document = JsonDocument.Parse(File.ReadAllText(files.SettingsPath));
        var root = document.RootElement;
        root.GetProperty("disableClaudeAiConnectors").GetBoolean().Should().BeTrue();
        root.GetProperty("syncClaudeAiPlugins").GetBoolean().Should().BeFalse();
        root.GetProperty("syncClaudeAiSkills").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// Backlog 179: seen in the fifth trial, the pinned CLI's commit guidance put a model trailer on an agent's
    /// commit in a repository that forbids one. The settings hide the vendor's attribution, so the repository's own
    /// conventions are what the message follows.
    /// </summary>
    [Fact]
    public void The_settings_leave_attribution_to_the_repository()
    {
        var files = Writer().Write(_session, "s3cret");

        using var document = JsonDocument.Parse(File.ReadAllText(files.SettingsPath));
        var attribution = document.RootElement.GetProperty("attribution");
        attribution.GetProperty("commit").GetString().Should().BeEmpty();
        attribution.GetProperty("pr").GetString().Should().BeEmpty();
    }

    [Fact]
    public void A_script_that_ends_without_a_clean_exit_is_turned_into_a_blocking_refusal_by_the_command()
    {
        // Backlog 115: measured against the pinned CLI, a hook that fails any other way than exit 2 is NOT a
        // refusal, so the command itself turns every non-zero end of the script into exit 2 with a reason.
        var files = Writer().Write(_session, "s3cret");

        using var document = JsonDocument.Parse(File.ReadAllText(files.SettingsPath));
        var command = document.RootElement
            .GetProperty("hooks").GetProperty("PreToolUse")[0]
            .GetProperty("hooks")[0]
            .GetProperty("command").GetString();

        command.Should().Be(
            $"sh '{files.ScriptPath}' || {{ echo '{SessionHookWriter.CrashedHookRefusal}' >&2; exit 2; }}");
        SessionHookWriter.CrashedHookRefusal.Should().NotContain("'", "it sits inside a single-quoted shell word");
    }

    [PosixShellTheory]
    [InlineData("crashes", "#!/bin/sh\nexit 1\n", 2, "")]
    [InlineData("is killed", "#!/bin/sh\nkill -9 $$\n", 2, "")]
    [InlineData("prints half an answer and dies", "#!/bin/sh\nprintf partial\nexit 3\n", 2, "partial")]
    [InlineData("is missing", null, 2, "")]
    [InlineData("answers cleanly", "#!/bin/sh\nprintf '{\"hookSpecificOutput\":{}}'\n", 0, "{\"hookSpecificOutput\":{}}")]
    public void The_hook_command_run_by_a_real_shell_refuses_with_exit_2_whenever_the_script_does_not_end_cleanly(
        string what, string? body, int expectedExit, string expectedStdout)
    {
        // Executed, not read: the command goes through /bin/sh -c exactly as the CLI runs it, with the
        // session's script replaced by one that fails the way this case names. Exit 2 is the one failure
        // the pinned CLI treats as blocking (ClaudeCliContractTests pins that against the real binary).
        var files = Writer().Write(_session, "s3cret");
        if (body is null)
        {
            File.Delete(files.ScriptPath);
        }
        else
        {
            File.WriteAllText(files.ScriptPath, body);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(files.SettingsPath));
        var command = document.RootElement
            .GetProperty("hooks").GetProperty("PreToolUse")[0]
            .GetProperty("hooks")[0]
            .GetProperty("command").GetString()!;

        var info = new System.Diagnostics.ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(command);

        using var process = System.Diagnostics.Process.Start(info)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000).Should().BeTrue();

        process.ExitCode.Should().Be(expectedExit, $"the script {what}");
        stdout.Should().Be(expectedStdout);
        if (expectedExit == 2)
        {
            stderr.Should().Contain(SessionHookWriter.CrashedHookRefusal, "the CLI shows a blocking hook's stderr as the reason");
        }
        else
        {
            stderr.Should().NotContain(SessionHookWriter.CrashedHookRefusal);
        }
    }

    [Fact]
    public void The_hook_may_wait_longer_than_the_studio_will_hold_the_call()
    {
        // Otherwise the CLI would kill the hook first and the user's answer would arrive too late.
        var files = Writer().Write(_session, "s3cret");

        using var document = JsonDocument.Parse(File.ReadAllText(files.SettingsPath));
        var timeout = document.RootElement
            .GetProperty("hooks").GetProperty("PreToolUse")[0]
            .GetProperty("hooks")[0]
            .GetProperty("timeout").GetInt32();

        timeout.Should().BeGreaterThan((int)AgentApprovalBroker.DefaultTimeout.TotalSeconds);
    }

    [Fact]
    public void The_script_carries_the_sessions_own_secret_and_the_studios_url()
    {
        var files = Writer().Write(_session, "s3cret");

        var script = File.ReadAllText(files.ScriptPath);
        script.Should().Contain(AgentApprovalEndpoint.SecretHeader);
        script.Should().Contain("s3cret");
        script.Should().Contain("http://127.0.0.1:8080/api/agent/approval");
    }

    [Fact]
    public void The_script_denies_rather_than_allows_when_anything_goes_wrong()
    {
        var files = Writer().Write(_session, "s3cret");

        var script = File.ReadAllText(files.ScriptPath);
        script.Should().Contain("\"permissionDecision\":\"deny\"");
        script.Should().NotContain("allow", "the script must never invent an approval of its own");
    }

    [Fact]
    public void Rewriting_a_session_replaces_the_previous_secret_rather_than_leaving_both()
    {
        var writer = Writer();
        var files = writer.Write(_session, "first-secret");

        writer.Write(_session, "second-secret");

        var script = File.ReadAllText(files.ScriptPath);
        script.Should().Contain("second-secret");
        script.Should().NotContain("first-secret");
    }

    [Fact]
    public void Removing_a_session_takes_its_secret_off_disk()
    {
        var writer = Writer();
        var files = writer.Write(_session, "s3cret");

        writer.Remove(_session);

        Directory.Exists(files.Directory).Should().BeFalse();
    }

    [Fact]
    public void Removing_a_session_keeps_the_cli_conversation_it_could_be_resumed_from()
    {
        // Owner report 2026-09-15: the whole folder went, and with it claude/projects, so every ended
        // conversation answered a resume with "No conversation found with session ID".
        var writer = Writer();
        var files = writer.Write(_session, "s3cret");
        var configHome = Path.Combine(files.Directory, "claude");
        var conversation = Path.Combine(configHome, "projects", "-workspaces-w1", "conversation.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(conversation)!);
        File.WriteAllText(conversation, "{}\n");
        Directory.CreateDirectory(Path.Combine(configHome, "skills", "test-skill"));
        File.WriteAllText(Path.Combine(configHome, "skills", "test-skill", "SKILL.md"), "skill");
        File.WriteAllText(Path.Combine(configHome, ".credentials.json"), "token");

        writer.Remove(_session);

        File.Exists(conversation).Should().BeTrue("the conversation is the CLI's, and resuming needs it");
        File.Exists(files.ScriptPath).Should().BeFalse("the secret still leaves with the session");
        File.Exists(files.SettingsPath).Should().BeFalse();
        File.Exists(Path.Combine(configHome, ".credentials.json")).Should().BeFalse("the seeded credential copy goes too");
        Directory.Exists(Path.Combine(configHome, "skills")).Should().BeFalse("the projection is rebuilt at the next start");
    }

    [Fact]
    public void Removing_a_session_that_was_never_written_is_not_an_error()
    {
        var act = () => Writer().Remove(AgentSessionId.New());

        act.Should().NotThrow();
    }

    [Fact]
    public void The_script_only_forwards_a_real_decision_document()
    {
        // Found by running the script in the container: without this guard a 502 proxy page was
        // passed through to the CLI verbatim.
        var files = Writer().Write(_session, "s3cret");

        var script = File.ReadAllText(files.ScriptPath);
        script.Should().Contain("hookSpecificOutput").And.Contain("permissionDecision");
    }

    [Fact]
    public void The_script_is_written_with_unix_line_endings_because_the_container_runs_it()
    {
        var files = Writer().Write(_session, "s3cret");

        File.ReadAllText(files.ScriptPath).Should().NotContain("\r");
    }

    [Theory]
    [InlineData(null, null, "http://127.0.0.1:8080/api/agent/approval")]
    [InlineData("8080", null, "http://127.0.0.1:8080/api/agent/approval")]
    [InlineData("5090;5091", null, "http://127.0.0.1:5090/api/agent/approval")]
    [InlineData("8080", "http://localhost:5090/api/agent/approval", "http://localhost:5090/api/agent/approval")]
    public void The_hook_posts_to_the_port_the_studio_actually_serves(string? ports, string? configured, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_HTTP_PORTS"] = ports,
                ["Sessions:ApprovalUrl"] = configured,
            })
            .Build();

        SessionsServiceCollectionExtensions.ApprovalUrlFor(configuration).Should().Be(expected);
    }

    [Fact]
    public void The_session_folder_is_readable_only_by_the_studio_user()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The container is Linux; file modes are meaningless on the dev host.
        }

        var files = Writer().Write(_session, "s3cret");

        File.GetUnixFileMode(files.Directory).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

/// <summary>
/// A theory that needs a POSIX <c>/bin/sh</c> to execute the hook command (backlog 115). It skips with the
/// reason where there is none (a Windows host), set in the constructor because <c>Assert.Skip</c> needs
/// xunit v3. CI runs on Linux, where it executes.
/// </summary>
internal sealed class PosixShellTheoryAttribute : TheoryAttribute
{
    public PosixShellTheoryAttribute()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            Skip = "Needs a POSIX /bin/sh to execute the hook command as the CLI does; the live contract tier "
                + "covers the same command against the real CLI on any host.";
        }
    }
}
