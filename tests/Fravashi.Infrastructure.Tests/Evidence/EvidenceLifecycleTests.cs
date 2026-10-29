using System.Text;
using AwesomeAssertions;
using Fravashi.Application.Evidence;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Evidence;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Evidence;

/// <summary>
/// Backlog 75: every evidence class the studio keeps today is listed with what it holds, and a delete removes it
/// from DISK. The database class is held to the strict reading: the deleted text must not be findable in the
/// database file or its write-ahead log afterwards, not merely absent from a query.
/// </summary>
public class EvidenceLifecycleTests : IDisposable
{
    private const string Marker = "EVIDENCE-MARKER-7f3c1a9e-this-text-must-be-gone";

    private readonly string _data =
        Path.Combine(Path.GetTempPath(), "fravashi-evidence-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteSessionStore _store;
    private readonly string _home;
    private bool _running;

    public EvidenceLifecycleTests()
    {
        Directory.CreateDirectory(_data);
        _factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_data, DataDirectory.DatabaseFileName), Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _store = new SqliteSessionStore(_factory);
        _home = Path.Combine(_data, "home", ".claude");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private EvidenceLifecycle Lifecycle() => new(
        new DataDirectory(_data),
        _factory,
        _store,
        new CliTranscriptScrubber(_data, _home),
        anySessionRunning: () => _running);

    private string Plant(string relative, int bytes = 100)
    {
        var path = Path.Combine(_data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(new string('x', bytes)));
        return path;
    }

    private EvidenceClassReport Report(EvidenceClass evidenceClass) =>
        Lifecycle().Inventory().Single(r => r.Class == evidenceClass);

    // ---- the inventory ----------------------------------------------------------------------------------------

    [Fact]
    public void Every_class_is_listed_once_with_a_stated_retention_redaction_and_delete_effect()
    {
        var reports = Lifecycle().Inventory();

        reports.Select(r => r.Class).Should().Equal(Enum.GetValues<EvidenceClass>());
        foreach (var report in reports)
        {
            report.Name.Should().NotBeNullOrWhiteSpace();
            report.Location.Should().NotBeNullOrWhiteSpace();
            report.Retention.Should().NotBeNullOrWhiteSpace();
            report.Redaction.Should().NotBeNullOrWhiteSpace();
            report.DeleteEffect.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void The_inventory_counts_and_measures_what_is_on_disk()
    {
        Plant("diagnostics/fravashi-diagnostics-20260901-000000.zip", 300);
        Plant("diagnostics/fravashi-diagnostics-20260902-000000.zip", 200);
        Plant("backups/fravashi-backup-20260901-000000.zip", 1000);
        Plant("backups/pre-restore-20260902-000000/fravashi.db", 50);
        Plant("trash/2026-09-01_1200_[main].zip", 70);
        Plant("logs/studio-2026-09-14.log", 40);
        Plant("agent-sessions/abc/settings.json", 10);
        Plant("home/.claude/projects/-w/aaaa.jsonl", 25);
        Plant("home/.claude/history.jsonl", 5);

        Report(EvidenceClass.DiagnosticBundles).Should().Match<EvidenceClassReport>(r => r.ItemCount == 2 && r.SizeBytes == 500);
        Report(EvidenceClass.Backups).Should().Match<EvidenceClassReport>(r => r.ItemCount == 2 && r.SizeBytes == 1050);
        Report(EvidenceClass.TrashArchives).Should().Match<EvidenceClassReport>(r => r.ItemCount == 1 && r.SizeBytes == 70);
        Report(EvidenceClass.Logs).Should().Match<EvidenceClassReport>(r => r.ItemCount == 1 && r.SizeBytes == 40);
        Report(EvidenceClass.SessionArtifacts).Should().Match<EvidenceClassReport>(r => r.ItemCount == 1 && r.SizeBytes == 10);
        Report(EvidenceClass.AgentCliConversations).Should().Match<EvidenceClassReport>(r => r.ItemCount == 2 && r.SizeBytes == 30);
    }

    [Fact]
    public void An_empty_data_directory_lists_every_class_as_empty_rather_than_failing()
    {
        foreach (var report in Lifecycle().Inventory())
        {
            report.ItemCount.Should().Be(0, "{0} holds nothing yet", report.Class);
        }
    }

    // ---- deleting files ---------------------------------------------------------------------------------------

    [Fact]
    public void Deleting_diagnostic_bundles_removes_every_bundle_from_disk()
    {
        var a = Plant("diagnostics/fravashi-diagnostics-20260901-000000.zip", 300);
        var b = Plant("diagnostics/fravashi-diagnostics-20260902-000000.zip", 200);

        var result = Lifecycle().Delete(EvidenceClass.DiagnosticBundles);

        result.Completed.Should().BeTrue();
        result.ItemsRemoved.Should().Be(2);
        result.BytesRemoved.Should().Be(500);
        File.Exists(a).Should().BeFalse();
        File.Exists(b).Should().BeFalse();
        Report(EvidenceClass.DiagnosticBundles).ItemCount.Should().Be(0);
    }

    [Fact]
    public void Deleting_backups_removes_the_archives_and_the_pre_restore_folders()
    {
        var zip = Plant("backups/fravashi-backup-20260901-000000.zip", 1000);
        Plant("backups/pre-restore-20260902-000000/keys/key.xml", 50);

        var result = Lifecycle().Delete(EvidenceClass.Backups);

        result.Completed.Should().BeTrue();
        File.Exists(zip).Should().BeFalse();
        Directory.Exists(Path.Combine(_data, "backups", "pre-restore-20260902-000000")).Should().BeFalse();
    }

    [Fact]
    public void Deleting_trash_and_logs_removes_their_files()
    {
        var trash = Plant("trash/2026-09-01_1200_[main].zip");
        var log = Plant("logs/launcher-2026-09-01.log");

        Lifecycle().Delete(EvidenceClass.TrashArchives).Completed.Should().BeTrue();
        Lifecycle().Delete(EvidenceClass.Logs).Completed.Should().BeTrue();

        File.Exists(trash).Should().BeFalse();
        File.Exists(log).Should().BeFalse();
    }

    [Fact]
    public void Deleting_cli_conversations_removes_the_projects_and_the_prompt_history_in_the_owned_home()
    {
        var conversation = Plant("home/.claude/projects/-w/aaaa.jsonl");
        var sidechain = Plant("home/.claude/projects/-w/aaaa/subagents/x.jsonl");
        var history = Plant("home/.claude/history.jsonl");
        var credential = Plant("home/.claude/.credentials.json");

        var result = Lifecycle().Delete(EvidenceClass.AgentCliConversations);

        result.Completed.Should().BeTrue();
        File.Exists(conversation).Should().BeFalse();
        File.Exists(sidechain).Should().BeFalse();
        File.Exists(history).Should().BeFalse();
        File.Exists(credential).Should().BeTrue("the CLI's sign-in is not evidence and must survive");
    }

    [Fact]
    public void Verification_artifacts_are_listed_with_their_policy_and_deleting_them_removes_every_run_folder()
    {
        // Backlog 224: a design check's report and screenshots, one folder per run.
        Plant("verification/20261007-120000-abcdef01/report.md", 30);
        var picture = Plant("verification/20261007-120000-abcdef01/screen-0.wide.00-at-rest.build.png", 70);

        var report = Report(EvidenceClass.VerificationArtifacts);
        report.Should().Match<EvidenceClassReport>(r => r.ItemCount == 2 && r.SizeBytes == 100);
        report.Redaction.Should().Contain("redactor").And.Contain("screenshots");

        Lifecycle().Delete(EvidenceClass.VerificationArtifacts).Completed.Should().BeTrue();

        File.Exists(picture).Should().BeFalse();
        Directory.Exists(Path.Combine(_data, "verification", "20261007-120000-abcdef01")).Should().BeFalse();
    }

    [Fact]
    public void Deleting_session_artifacts_removes_every_session_folder()
    {
        var artifact = Plant("agent-sessions/abc/claude/projects/-w/bbbb.jsonl");

        Lifecycle().Delete(EvidenceClass.SessionArtifacts).Completed.Should().BeTrue();

        Directory.Exists(Path.Combine(_data, "agent-sessions", "abc")).Should().BeFalse();
        File.Exists(artifact).Should().BeFalse();
    }

    [Theory]
    [InlineData(EvidenceClass.AgentTranscripts)]
    [InlineData(EvidenceClass.AgentCliConversations)]
    [InlineData(EvidenceClass.SessionArtifacts)]
    public void A_session_shaped_class_is_not_deleted_while_a_session_runs(EvidenceClass evidenceClass)
    {
        var artifact = Plant("agent-sessions/abc/settings.json");
        var conversation = Plant("home/.claude/projects/-w/aaaa.jsonl");
        _store.Save(new SessionRecord(AgentSessionId.New(), "w1", AgentTool.Claude, "t", null, AgentSessionState.Running, DateTimeOffset.UtcNow, null));
        _running = true;

        Report(evidenceClass).DeleteBlockedReason.Should().Be(EvidenceLifecycle.SessionRunningReason);
        var result = Lifecycle().Delete(evidenceClass);

        result.Completed.Should().BeFalse();
        result.ItemsRemoved.Should().Be(0);
        File.Exists(artifact).Should().BeTrue();
        File.Exists(conversation).Should().BeTrue();
        _store.Recent(10).Should().HaveCount(1);
    }

    // ---- deleting transcripts from the database ---------------------------------------------------------------

    [Fact]
    public void Deleting_transcripts_leaves_no_trace_of_their_text_in_the_database_file_or_its_log()
    {
        for (var i = 0; i < 20; i++)
        {
            var id = AgentSessionId.New();
            _store.Save(new SessionRecord(id, "w1", AgentTool.Claude, "t", "p", AgentSessionState.Completed, DateTimeOffset.UtcNow, null));
            _store.Append(id, [new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, Marker + " " + i + new string('y', 2000))]);
        }

        // The control: before the delete the text really is on disk, so the assertion below can fail.
        OnDisk(Marker).Should().BeTrue("the planted transcript must be readable from the files first");

        var result = Lifecycle().Delete(EvidenceClass.AgentTranscripts);

        result.Completed.Should().BeTrue(result.Message);
        result.ItemsRemoved.Should().Be(20);
        _store.Recent(100).Should().BeEmpty();
        OnDisk(Marker).Should().BeFalse("a deleted row must not survive in free pages or the write-ahead log");
    }

    [Fact]
    public void Transcript_rows_whose_session_row_is_already_gone_are_deleted_too()
    {
        using (var connection = _factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO agent_session_events (session_id, kind, payload, created_utc) VALUES ('orphan', 'Message', $p, '2026-09-01');";
            command.Parameters.AddWithValue("$p", Marker);
            command.ExecuteNonQuery();
        }

        Lifecycle().Delete(EvidenceClass.AgentTranscripts).Completed.Should().BeTrue();

        OnDisk(Marker).Should().BeFalse();
    }

    private bool OnDisk(string text)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        foreach (var file in Directory.EnumerateFiles(_data, DataDirectory.DatabaseFileName + "*"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            if (buffer.ToArray().AsSpan().IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
