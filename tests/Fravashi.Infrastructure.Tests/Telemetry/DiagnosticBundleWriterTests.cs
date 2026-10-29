using System.Globalization;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Fravashi.Application.Sandbox;
using Fravashi.Application.Telemetry;
using Fravashi.Application.Updates;
using Fravashi.Domain.Telemetry;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Telemetry;
using Fravashi.Infrastructure.Tests.Persistence;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Telemetry;

/// <summary>
/// Backlog 74: the diagnosis a user can hand us. Two things are held here.
/// <para>
/// First, the CONTENTS: the bundle has to carry enough to diagnose a field report (which build, what
/// the launcher could harden, what shape the database is in, the recorded events, the recent logs), or
/// there is no point writing it.
/// </para>
/// <para>
/// Second, and the reason this file exists at all, the REDACTION. The logs are the risky part: an agent
/// can print an environment dump into one, and a bundle that quietly carries a live API key to whoever
/// the user sends it to is worse than no bundle. A real key shape is planted in a real log file on
/// disk and the assertion is that it is not in the written zip.
/// </para>
/// </summary>
public class DiagnosticBundleWriterTests : IDisposable
{
    // Not a real key: the sk-ant- prefix plus enough material chars to clear the redactor's floor,
    // which is exactly the shape the incident behind the redactor involved.
    private const string PlantedKey = "sk-" + "ant-api03-Fake0123456789Fake0123456789Fake0123456789FakeAA";

    // The same key as a narrow terminal would have written it: broken after ten material chars, which
    // is under the redactor's 16-char floor, so the head alone is NOT a match and the tail alone
    // carries no prefix. Only a whole-text redaction catches this one.
    private const string WrappedHead = "sk-ant-api03-Fake";
    private const string WrappedTail = "0123456789Fake0123456789Fake0123456789FakeAA";

    private readonly SqliteTestContext _db = new();
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), "fravashi-bundle-tests", Guid.NewGuid().ToString("N"));

    public DiagnosticBundleWriterTests() => Directory.CreateDirectory(Path.Combine(_dataRoot, "logs"));

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DiagnosticBundleWriter Build(IProductEventStore? events = null) =>
        new(new DataDirectory(_dataRoot),
            new FixedProductInfo("9.9.9", "corp-vpn"),
            new FixedPosture(),
            _db.Factory,
            events ?? new SqliteProductEventStore(_db.Factory),
            () => new DateTimeOffset(2026, 9, 13, 7, 5, 9, TimeSpan.Zero));

    private void PlantLog(string app, string content) =>
        File.WriteAllText(Path.Combine(_dataRoot, "logs", $"{app}-2026-09-13.log"), content);

    private static Dictionary<string, string> ReadEntries(string zipPath)
    {
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            contents[entry.FullName] = reader.ReadToEnd();
        }

        return contents;
    }

    // ---- contents --------------------------------------------------------------------------------

    [Fact]
    public void Writing_a_bundle_keeps_only_the_newest_ten_on_disk()
    {
        // Backlog 75: bundles are regenerable snapshots carrying log tails, so the folder is capped. Twelve
        // older bundles are planted; after one more write the newest ten remain and the one just written is
        // among them.
        var folder = Path.Combine(_dataRoot, DiagnosticBundleWriter.FolderName);
        Directory.CreateDirectory(folder);
        for (var day = 1; day <= 12; day++)
        {
            File.WriteAllText(
                Path.Combine(folder, $"fravashi-diagnostics-202609{day:D2}-000000.zip"), "old");
        }

        PlantLog("studio", "nothing interesting");
        var bundle = Build().Write();

        var kept = Directory.EnumerateFiles(folder, "*.zip").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        kept.Should().HaveCount(DiagnosticBundleWriter.MaxKeptBundles);
        kept.Should().Contain(Path.GetFileName(bundle.Path));
        kept.Should().NotContain("fravashi-diagnostics-20260903-000000.zip");
        kept.First().Should().Be("fravashi-diagnostics-20260904-000000.zip");
    }

    [Fact]
    public void The_bundle_lands_under_the_data_directory_with_a_stamped_name()
    {
        PlantLog("studio", "nothing interesting");

        var bundle = Build().Write();

        bundle.Path.Should().Be(Path.Combine(
            _dataRoot, DiagnosticBundleWriter.FolderName, "fravashi-diagnostics-20260913-070509.zip"));
        File.Exists(bundle.Path).Should().BeTrue();
        bundle.SizeBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void The_bundle_carries_the_version_variant_posture_schema_row_counts_events_and_logs()
    {
        var events = new SqliteProductEventStore(_db.Factory);
        events.Append(ProductEvent.ApprovalResolved(
            new DateTimeOffset(2026, 9, 13, 6, 0, 0, TimeSpan.Zero),
            "ws-1",
            ProductEventOutcome.DeniedByUser,
            TimeSpan.FromSeconds(42)));

        PlantLog("studio", "studio line one");
        PlantLog("launcher", "launcher line one");
        PlantLog("window", "window line one");

        var bundle = Build(events).Write();
        var entries = ReadEntries(bundle.Path);

        // Version + variant, from the single source of truth for "what am I running".
        entries["summary.txt"].Should().Contain("product_version: 9.9.9");
        entries["summary.txt"].Should().Contain("product_variant: corp-vpn");

        // The runtime posture the LAUNCHER reported, including a control it could not apply.
        entries["posture.txt"].Should().Contain("runtime: podman");
        entries["posture.txt"].Should().Contain("no-new-privileges: Active");
        entries["posture.txt"].Should().Contain("read-only-root: Unavailable");

        // Schema version and per-table row counts, never table content.
        entries["database.txt"].Should().Contain(
            "schema_version: " + SqliteSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture));
        entries["database.txt"].Should().Contain("product_events: 1");
        entries["database.txt"].Should().Contain("agent_sessions: 0");

        // The recorded events, as their schema and nothing else.
        entries["events.csv"].Should().StartWith("kind,occurred_utc,outcome,workspace_hash,duration_ms,count");
        entries["events.csv"].Should().Contain("ApprovalResolved,");
        entries["events.csv"].Should().Contain("DeniedByUser,");
        entries["events.csv"].Should().Contain(WorkspaceHash.Of("ws-1").Value);
        entries["events.csv"].Should().Contain("42000");

        // One log from each app that writes into <data dir>/logs.
        entries["logs/studio-2026-09-13.log"].Should().Contain("studio line one");
        entries["logs/launcher-2026-09-13.log"].Should().Contain("launcher line one");
        entries["logs/window-2026-09-13.log"].Should().Contain("window line one");

        // And the bundle can say what is in it, so the user can see what they are about to send.
        bundle.Entries.Should().Contain(["summary.txt", "posture.txt", "database.txt", "events.csv"]);
    }

    [Fact]
    public void A_missing_log_is_reported_rather_than_silently_absent()
    {
        PlantLog("studio", "only the studio ran");

        var entries = ReadEntries(Build().Write().Path);

        entries.Should().ContainKey("logs/launcher.txt");
        entries["logs/launcher.txt"].Should().Contain("No launcher log file was present.");
    }

    [Fact]
    public void Only_the_most_recent_log_of_each_app_rides_along()
    {
        var older = Path.Combine(_dataRoot, "logs", "studio-2026-09-01.log");
        File.WriteAllText(older, "an older day");
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        PlantLog("studio", "today");
        File.SetLastWriteTimeUtc(
            Path.Combine(_dataRoot, "logs", "studio-2026-09-13.log"),
            new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc));

        var entries = ReadEntries(Build().Write().Path);

        entries.Should().ContainKey("logs/studio-2026-09-13.log");
        entries.Should().NotContainKey("logs/studio-2026-09-01.log");
    }

    [Fact]
    public void A_log_the_studio_is_still_writing_can_still_be_read()
    {
        // The studio writes its own log while this runs, so the bundle must not need an exclusive handle.
        var path = Path.Combine(_dataRoot, "logs", "studio-2026-09-13.log");
        using var held = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(held) { AutoFlush = true };
        writer.WriteLine("a line written while the file is open");

        var entries = ReadEntries(Build().Write().Path);

        entries["logs/studio-2026-09-13.log"].Should().Contain("a line written while the file is open");
    }

    // ---- redaction -------------------------------------------------------------------------------

    [Fact]
    public void A_credential_planted_in_a_log_is_redacted_before_it_reaches_the_zip()
    {
        PlantLog("studio", string.Join(Environment.NewLine,
        [
            "2026-09-13 07:00:00.000 +02:00 [inf] Session: the agent ran a command",
            "2026-09-13 07:00:01.000 +02:00 [inf] Session: ANTHROPIC_API_KEY=" + PlantedKey,
            "2026-09-13 07:00:02.000 +02:00 [inf] Session: and the run continued",
        ]));

        var entries = ReadEntries(Build().Write().Path);

        // Read back out of the written zip, not out of the writer: what matters is what the file the
        // user attaches to a message actually holds.
        foreach (var (name, content) in entries)
        {
            content.Should().NotContain(PlantedKey, "{0} must not carry a live key", name);
        }

        var log = entries["logs/studio-2026-09-13.log"];
        log.Should().Contain("[redacted:", "the redaction must be visible, not a silent deletion");
        // The surrounding lines are untouched, so the log is still a log.
        log.Should().Contain("the agent ran a command");
        log.Should().Contain("and the run continued");
    }

    [Fact]
    public void A_credential_wrapped_across_a_line_break_is_redacted_whole()
    {
        // The regression this pins: the bundle used to hand the redactor ONE LINE AT A TIME, which
        // silently disabled the joined-runs matching the redactor grew (its own rounds three and
        // final) because a wrapped key leaked its tail. Line by line, this key is not redacted at all:
        // the head run falls under the 16-char material floor and is returned unchanged, and the
        // continuation line has no provider prefix for anything to match. So the bundle whose entire
        // purpose is being safe to hand to someone else was strictly weaker than the transcript path
        // it claims to match.
        PlantLog("studio", string.Join(Environment.NewLine,
        [
            "2026-09-13 07:00:00.000 +02:00 [inf] Session: ANTHROPIC_API_KEY=" + WrappedHead,
            WrappedTail + " (a narrow terminal wrapped it here)",
        ]));

        var log = ReadEntries(Build().Write().Path)["logs/studio-2026-09-13.log"];

        log.Should().NotContain(WrappedHead, "the head of a wrapped key is still key material");
        log.Should().NotContain(WrappedTail, "the tail is the part that leaked in the original incident");
        log.Should().Contain("[redacted:sk-ant-api-key]");
    }

    [Fact]
    public void An_assignment_whose_line_ends_in_an_exotic_separator_is_still_redacted()
    {
        // Second adversarial review: the entry text used to be redacted and THEN have its line endings
        // normalized, and the two orders are not equivalent. The env-assignment rule is
        // RegexOptions.Multiline, so its anchors are \n anchors; U+2028 is a line separator that
        // StreamReader.ReadLine does NOT split on, so it arrives sitting INSIDE what the redactor sees
        // as one line, the end anchor never matches, and a real assignment rides into the zip whole.
        // Normalizing first turns it into a break the anchor can see. No provider prefix appears here
        // on purpose: the env rule is the only thing that can catch this value, so the assertion is
        // about the anchor and nothing else.
        const string plantedValue = "Fake0123456789Abcdef";
        PlantLog("studio", string.Join(Environment.NewLine,
        [
            "2026-09-13 07:00:00.000 +02:00 [inf] Session: the agent printed an environment dump",
            "MY_DEPLOY_TOKEN=" + plantedValue + "\u2028(the pane broke this line with a separator)",
            "2026-09-13 07:00:02.000 +02:00 [inf] Session: and the run continued",
        ]));

        var entries = ReadEntries(Build().Write().Path);

        foreach (var (name, content) in entries)
        {
            content.Should().NotContain(plantedValue, "{0} must not carry the assigned value", name);
        }

        var log = entries["logs/studio-2026-09-13.log"];
        log.Should().Contain("MY_DEPLOY_TOKEN=[redacted:MY_DEPLOY_TOKEN]",
            "the name is kept so the log still says what was printed, only the material goes");
        log.Should().Contain("the run continued");
    }

    [Fact]
    public void A_json_env_value_broken_by_an_exotic_separator_is_still_redacted()
    {
        // Third adversarial review, measured through this writer: normalizing FIRST and redacting once
        // was NOT strictly better than the old order, it traded one leak for another. The JSON env rule
        // (a docker inspect Env array or a serialized command in a log, which is exactly what this
        // writer ships) accepts a value containing anything but a quote, a backslash, \r or \n, so a
        // U+2028 sitting inside the value is ordinary value material and the rule redacts it. Normalize
        // first and that separator BECOMES a \n, which ends the value before its closing quote, so the
        // JSON rule no longer matches; the line-anchored plain rule cannot pick it up either, because
        // the line starts with JSON punctuation rather than with the assignment. The value then rode
        // into the zip WHOLE. Redacting on both sides of the normalization is what makes the sentence
        // in the writer's doc true: this case is caught before the normalization, the anchor case above
        // is caught after it.
        const string plantedValue = "0123456789abcdef\u2028ghijklmnop";
        PlantLog("studio", string.Join(Environment.NewLine,
        [
            "2026-09-13 07:00:00.000 +02:00 [inf] Session: inspect env "
                + "{\"Env\":[\"MY_DEPLOY_TOKEN=" + plantedValue + "\"]}",
            "2026-09-13 07:00:02.000 +02:00 [inf] Session: and the run continued",
        ]));

        var entries = ReadEntries(Build().Write().Path);

        foreach (var (name, content) in entries)
        {
            content.Should().NotContain("0123456789abcdef", "{0} must not carry the assigned value", name);
            content.Should().NotContain("ghijklmnop", "{0} must not carry the tail either", name);
        }

        var log = entries["logs/studio-2026-09-13.log"];
        log.Should().Contain("MY_DEPLOY_TOKEN=[redacted:MY_DEPLOY_TOKEN]");
        log.Should().Contain("the run continued");
    }

    [Theory]
    [InlineData('\f')]
    [InlineData('\u0085')]
    [InlineData('\u2029')]
    public void A_key_a_pane_wrapped_on_another_line_break_after_a_short_head_is_redacted_whole(char wrap)
    {
        // A short head ("api03-Fake" is 10 material chars, under the 16-char floor). Before backlog 119
        // only the normalized pass caught this, because U+000C, U+0085 and U+2029 were no joiners; they
        // are joiners now, so the first pass already redacts it whole and the second is a fixed point.
        PlantLog("studio", "2026-09-13 07:00:00.000 +02:00 [inf] Session: key="
            + WrappedHead + wrap + WrappedTail + " (the pane wrapped it here)");

        var log = ReadEntries(Build().Write().Path)["logs/studio-2026-09-13.log"];

        log.Should().NotContain(WrappedTail, "the tail is the part a wrap used to leave behind");
        log.Should().Contain("key=[redacted:sk-ant-api-key] (the pane wrapped it here)");
    }

    [Theory]
    [InlineData('\f')]
    [InlineData('\u0085')]
    [InlineData('\u2029')]
    public void A_key_wrapped_on_another_line_break_after_a_long_head_is_redacted_whole(char wrap)
    {
        // Lifted from the KNOWN GAP this test pinned while closing item 74 (backlog 119). This head
        // carries 20 material chars, over the floor on its own, so the first pass used to replace the
        // head and leave a bare charset tail after the wrap that neither pass could match. The wrap is
        // a joiner now, so the whole key becomes one marker and nothing of it reaches the zip.
        const string longHead = "sk-" + "ant-api03-Fake0123456789";
        const string tail = "Fake0123456789Fake0123456789FakeAA";
        PlantLog("studio", "2026-09-13 07:00:00.000 +02:00 [inf] Session: key="
            + longHead + wrap + tail + " (the pane wrapped it here)");

        var log = ReadEntries(Build().Write().Path)["logs/studio-2026-09-13.log"];

        log.Should().NotContain(longHead);
        log.Should().NotContain(tail);
        log.Should().NotContain("0123456789");
        log.Should().Contain("key=[redacted:sk-ant-api-key] (the pane wrapped it here)");
    }

    [Fact]
    public void A_key_whose_continuation_lines_are_indented_is_redacted_whole()
    {
        // Backlog 119's second shape, through the writer: a logger that indents continuation lines.
        // One log line per wrap, so ReadTail rejoins them with the platform newline first.
        PlantLog("studio", string.Join('\n',
        [
            "2026-09-13 07:00:00.000 +02:00 [inf] Session: key=sk-" + "ant-api03-Fake0123456789",
            "        Fake0123456789Fake0123456789",
            "\tFakeAA (logged by the tool)",
            "2026-09-13 07:00:01.000 +02:00 [inf] Session: next entry",
        ]));

        var log = ReadEntries(Build().Write().Path)["logs/studio-2026-09-13.log"];

        log.Should().NotContain("0123456789");
        log.Should().NotContain("FakeAA");
        log.Should().Contain("key=[redacted:sk-ant-api-key] (logged by the tool)\n2026-09-13 07:00:01.000");
    }

    [Fact]
    public void A_key_wrapped_past_the_old_join_cap_is_redacted_whole()
    {
        // Backlog 119's third shape, through the writer: a 106-char key at five columns needs twenty
        // joins, far past the old cap of six.
        var material = "api03-" + string.Concat(Enumerable.Repeat("Fake567890", 9)) + "ABC";
        var lines = Enumerable.Range(0, (material.Length + 4) / 5)
            .Select(i => material.Substring(i * 5, Math.Min(5, material.Length - (i * 5))));
        PlantLog("studio", "2026-09-13 07:00:00.000 +02:00 [inf] Session: key=sk-ant-\n"
            + string.Join('\n', lines) + "\n# the pane ends here");

        var log = ReadEntries(Build().Write().Path)["logs/studio-2026-09-13.log"];

        log.Should().NotContain("Fake5");
        log.Should().NotContain("ABC");
        log.Should().Contain("key=[redacted:sk-ant-api-key]\n# the pane ends here");
    }

    [Fact]
    public void A_second_redaction_pass_leaves_an_existing_marker_exactly_as_it_found_it()
    {
        // The writer redacts on both sides of the line-ending normalization, so every marker the first
        // pass writes is fed straight back through the redactor. That is only safe if a marker is a
        // fixed point: it must not nest ([redacted:[redacted:...]]) and it must not multiply. Checked
        // for both rules that can produce one, the provider rule (a bare marker in prose) and the env
        // rule (a marker as an assignment's value, which the env rule DOES match again).
        PlantLog("studio", string.Join(Environment.NewLine,
        [
            "2026-09-13 07:00:00.000 +02:00 [inf] Session: request failed with [redacted:github-token]",
            "SOME_TOKEN=[redacted:SOME_TOKEN]",
        ]));

        var log = ReadEntries(Build().Write().Path)["logs/studio-2026-09-13.log"];

        log.Should().Contain("with [redacted:github-token]");
        log.Should().Contain("SOME_TOKEN=[redacted:SOME_TOKEN]");
        log.Should().NotContain("[redacted:[redacted:");
        (log.Split("[redacted:").Length - 1).Should().Be(2, "a pass must not add markers of its own");
    }

    [Fact]
    public void A_bare_provider_token_anywhere_in_a_line_is_redacted_too()
    {
        // Not only assignments: the redactor's provider-prefix rules match anywhere, and the bundle
        // inherits that because it runs the same redactor the transcript write path runs.
        PlantLog("window", "console.error: request failed with token gh" + "p_Fake0123456789Fake0123456789ABCD");

        var entries = ReadEntries(Build().Write().Path);

        entries["logs/window-2026-09-13.log"].Should().NotContain("gh" + "p_Fake0123456789Fake0123456789ABCD");
        entries["logs/window-2026-09-13.log"].Should().Contain("[redacted:github-token]");
    }

    [Fact]
    public void Every_text_entry_goes_through_the_redactor_not_only_the_logs()
    {
        // The guarantee is a property of the writer: there is one way to add an entry and it redacts.
        // Proved by planting a key where the SUMMARY reads from, which is the product version.
        var writer = new DiagnosticBundleWriter(
            new DataDirectory(_dataRoot),
            new FixedProductInfo(PlantedKey, "full"),
            new FixedPosture(),
            _db.Factory,
            new SqliteProductEventStore(_db.Factory),
            () => new DateTimeOffset(2026, 9, 13, 7, 5, 9, TimeSpan.Zero));

        var entries = ReadEntries(writer.Write().Path);

        entries["summary.txt"].Should().NotContain(PlantedKey);
        entries["summary.txt"].Should().Contain("[redacted:sk-ant-api-key]");
    }

    private sealed class FixedProductInfo(string version, string variant) : IProductInfo
    {
        public string Version { get; } = version;

        public string Variant { get; } = variant;
    }

    private sealed class FixedPosture : ISandboxPosture
    {
        public SandboxPosture Current { get; } = new(
            "podman",
            [
                new SandboxControl("no-new-privileges", "No new privileges",
                    "Stops a child gaining rights", SandboxProtectionStatus.Active, "--security-opt"),
                new SandboxControl("read-only-root", "Read-only root",
                    "Keeps the image immutable", SandboxProtectionStatus.Unavailable,
                    "this runtime refuses the flag"),
            ],
            StrongerRuntimeRecommended: false,
            Recommendation: null,
            CheckedUtc: new DateTimeOffset(2026, 9, 13, 6, 0, 0, TimeSpan.Zero));
    }
}
