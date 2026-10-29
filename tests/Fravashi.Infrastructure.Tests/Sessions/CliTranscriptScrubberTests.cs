using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 116: the agent CLI's own conversation files are redacted IN PLACE, never deleted, because
/// Continue resumes from them. Held here: the rewrite keeps every line valid JSON and every untouched line
/// byte-identical, a session-end scrub touches only that session's files, the startup scrub reaches every
/// config home the studio's sessions use, and both are idempotent.
/// </summary>
public class CliTranscriptScrubberTests : IDisposable
{
    // Not a real key: the shape is real, the material is the word "Fake".
    private const string FakeKey = "sk-" + "ant-api03-Fake0123456789Fake0123456789Fake0123456789FakeAA";

    private readonly string _data =
        Path.Combine(Path.GetTempPath(), "fravashi-cli-scrub-" + Guid.NewGuid().ToString("N"));

    private readonly string _home;

    public CliTranscriptScrubberTests()
    {
        _home = Path.Combine(_data, "home", ".claude");
        Directory.CreateDirectory(_home);
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

    private CliTranscriptScrubber Scrubber() => new(_data, _home);

    private static string Line(object value) => JsonSerializer.Serialize(value);

    private static string ToolResultLine(string sessionId, string output) => Line(new
    {
        type = "user",
        sessionId,
        uuid = Guid.NewGuid().ToString(),
        message = new
        {
            role = "user",
            content = new object[] { new { type = "tool_result", tool_use_id = "toolu_01", content = output } },
        },
        toolUseResult = new { stdout = output, stderr = string.Empty, interrupted = false },
    });

    private string Plant(string home, string project, string name, params string[] lines)
    {
        var path = Path.Combine(home, "projects", project, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        return path;
    }

    // ---- the line rewrite -----------------------------------------------------------------------

    [Fact]
    public void A_secret_in_a_tool_result_is_redacted_and_every_line_stays_valid_json()
    {
        var text = string.Join('\n',
            Line(new { type = "summary", summary = "ordinary" }),
            ToolResultLine("s1", "ANTHROPIC_API_KEY=" + FakeKey + "\nHOME=/root"),
            Line(new { type = "assistant", message = new { content = "the key " + FakeKey + " is set" } })) + "\n";

        var (redacted, changed) = CliTranscriptScrubber.RedactJsonLines(text);

        changed.Should().Be(2);
        redacted.Should().NotContain("Fake0123456789");
        redacted.Should().Contain("[redacted:sk-ant-api-key]");
        foreach (var line in redacted.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            FluentJson(line);
        }
    }

    [Fact]
    public void A_line_with_nothing_to_redact_is_byte_identical_and_line_endings_are_kept()
    {
        // Spacing, key order and escapes that System.Text.Json would write differently must survive on a
        // line the redactor does not change, and CRLF and a missing final newline are preserved.
        var untouched = "{\"type\": \"user\",  \"text\":\"caf\\u00e9 \\/ ok\", \"n\": 1.50}";
        var secret = Line(new { text = FakeKey });
        var text = untouched + "\r\n" + secret + "\r\n" + untouched;

        var (redacted, changed) = CliTranscriptScrubber.RedactJsonLines(text);

        changed.Should().Be(1);
        var lines = redacted.Split("\r\n");
        lines.Should().HaveCount(3);
        lines[0].Should().Be(untouched);
        lines[2].Should().Be(untouched);
        lines[1].Should().Contain("[redacted:sk-ant-api-key]");
    }

    [Fact]
    public void Numbers_ids_and_property_names_survive_a_rewritten_line()
    {
        var original = "{\"uuid\":\"ea12436c-b6eb-455e-b670-687beea9c6d2\",\"parentUuid\":null,\"costUSD\":0.000123456789,"
            + "\"tokens\":12345678901234,\"GITHUB_TOKEN\":\"ghp_SecretMaterial12345\",\"nested\":{\"a\":[1,\"" + FakeKey + "\"]}}";

        var (redacted, _) = CliTranscriptScrubber.RedactJsonLines(original);

        var node = JsonNode.Parse(redacted)!.AsObject();
        node["uuid"]!.GetValue<string>().Should().Be("ea12436c-b6eb-455e-b670-687beea9c6d2");
        node["parentUuid"].Should().BeNull();
        redacted.Should().Contain("\"costUSD\":0.000123456789");
        redacted.Should().Contain("\"tokens\":12345678901234");
        node["GITHUB_TOKEN"]!.GetValue<string>().Should().Be("[redacted:github-token]");
        node["nested"]!["a"]![1]!.GetValue<string>().Should().Be("[redacted:sk-ant-api-key]");
    }

    [Fact]
    public void A_break_stored_as_a_json_escape_is_seen_as_the_real_break()
    {
        // The model saw a real U+2029 between head and tail; the CLI wrote it as an escape. Structural
        // redaction decodes it, so the whole key goes (backlog 119's joiner set applies to what was said).
        var original = "{\"out\":\"key sk-" + "ant-api03-Fake0123456789\\u2029Fake0123456789Fake0123456789FakeAA done\"}";

        var (redacted, changed) = CliTranscriptScrubber.RedactJsonLines(original);

        changed.Should().Be(1);
        redacted.Should().NotContain("Fake0123456789");
        JsonNode.Parse(redacted)!["out"]!.GetValue<string>().Should().Be("key [redacted:sk-ant-api-key] done");
    }

    [Fact]
    public void A_torn_final_line_that_is_not_json_still_loses_its_secret()
    {
        var text = Line(new { ok = true }) + "\n{\"type\":\"user\",\"text\":\"" + FakeKey;

        var (redacted, changed) = CliTranscriptScrubber.RedactJsonLines(text);

        changed.Should().Be(1);
        redacted.Should().NotContain("Fake0123456789");
        redacted.Should().StartWith(Line(new { ok = true }) + "\n");
    }

    [Fact]
    public void A_line_with_a_duplicate_property_name_is_handled_without_breaking_the_json()
    {
        // The CLI never writes one, but a hand-edited or foreign file could. Measured rather than assumed:
        // whatever the node model does with it, the line that comes out must still parse.
        var original = "{\"text\":\"first\",\"text\":\"" + FakeKey + "\"}";

        var (redacted, _) = CliTranscriptScrubber.RedactJsonLines(original);

        FluentJson(redacted);
        redacted.Should().NotContain("Fake0123456789");
    }

    [Fact]
    public void Rewriting_is_idempotent()
    {
        var text = ToolResultLine("s1", "export GITHUB_TOKEN=ghp_SecretMaterial12345\n" + FakeKey) + "\n";

        var (once, _) = CliTranscriptScrubber.RedactJsonLines(text);
        var (twice, changed) = CliTranscriptScrubber.RedactJsonLines(once);

        changed.Should().Be(0);
        twice.Should().Be(once);
    }

    // ---- a session's end ------------------------------------------------------------------------

    [Fact]
    public void A_session_end_scrub_redacts_that_sessions_files_in_place_and_leaves_other_sessions_alone()
    {
        var mine = Plant(_home, "-workspaces-w1", "aaaa-1111.jsonl", ToolResultLine("aaaa-1111", "key " + FakeKey));
        var sidechain = Plant(_home, "-workspaces-w1", Path.Combine("aaaa-1111", "subagents", "agent-1.jsonl"),
            ToolResultLine("aaaa-1111", "sub " + FakeKey));
        var theirs = Plant(_home, "-workspaces-w2", "bbbb-2222.jsonl", ToolResultLine("bbbb-2222", "key " + FakeKey));
        var theirsBefore = File.ReadAllBytes(theirs);

        var result = Scrubber().ScrubSession(null, ["aaaa-1111"]);

        result.FilesRewritten.Should().Be(2);
        File.Exists(mine).Should().BeTrue("Continue resumes from this file, so it is never deleted");
        File.ReadAllText(mine).Should().NotContain("Fake0123456789").And.Contain("[redacted:sk-ant-api-key]");
        File.ReadAllText(sidechain).Should().NotContain("Fake0123456789");
        File.ReadAllBytes(theirs).Should().Equal(theirsBefore, "another session may still be appending to its own file");
        Directory.EnumerateFiles(Path.GetDirectoryName(mine)!, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void A_session_with_a_projected_config_home_is_scrubbed_there()
    {
        var projected = Path.Combine(_data, "agent-sessions", "sid", "claude");
        var file = Plant(projected, "-workspaces-w1", "cccc-3333.jsonl", ToolResultLine("cccc-3333", FakeKey));

        Scrubber().ScrubSession(projected, ["cccc-3333"]).FilesRewritten.Should().Be(1);

        File.ReadAllText(file).Should().NotContain("Fake0123456789");
    }

    [Fact]
    public void A_second_session_end_scrub_rewrites_nothing()
    {
        var file = Plant(_home, "-p", "dddd-4444.jsonl", ToolResultLine("dddd-4444", FakeKey));
        Scrubber().ScrubSession(null, ["dddd-4444"]);
        var stamp = File.GetLastWriteTimeUtc(file);

        var again = Scrubber().ScrubSession(null, ["dddd-4444"]);

        again.FilesExamined.Should().Be(1);
        again.FilesRewritten.Should().Be(0);
        File.GetLastWriteTimeUtc(file).Should().Be(stamp);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    public void A_conversation_id_that_is_not_an_id_is_never_turned_into_a_path(string id)
    {
        Plant(_home, "-p", "escape.jsonl", ToolResultLine("x", FakeKey));

        Scrubber().ScrubSession(null, [id]).FilesExamined.Should().Be(0);
    }

    [Fact]
    public void File_permissions_survive_the_atomic_replace()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var file = Plant(_home, "-p", "eeee-5555.jsonl", ToolResultLine("eeee-5555", FakeKey));
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Scrubber().ScrubSession(null, ["eeee-5555"]).FilesRewritten.Should().Be(1);

        File.GetUnixFileMode(file).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // ---- studio start ---------------------------------------------------------------------------

    [Fact]
    public void The_startup_scrub_reaches_the_default_home_every_projected_home_and_the_prompt_history()
    {
        var inDefault = Plant(_home, "-p", "ffff-6666.jsonl", ToolResultLine("ffff-6666", FakeKey));
        var projected = Path.Combine(_data, "agent-sessions", "s2", "claude");
        var inProjected = Plant(projected, "-p", "gggg-7777.jsonl", ToolResultLine("gggg-7777", FakeKey));
        var history = Path.Combine(_home, "history.jsonl");
        File.WriteAllText(history, Line(new { display = "use " + FakeKey, project = "/workspaces/w1" }) + "\n");

        var result = Scrubber().ScrubAll();

        result.FilesRewritten.Should().Be(3);
        File.ReadAllText(inDefault).Should().NotContain("Fake0123456789");
        File.ReadAllText(inProjected).Should().NotContain("Fake0123456789");
        File.ReadAllText(history).Should().NotContain("Fake0123456789");
    }

    [Fact]
    public void The_startup_scrub_skips_unchanged_files_and_rescrubs_a_file_that_grew()
    {
        var file = Plant(_home, "-p", "hhhh-8888.jsonl", ToolResultLine("hhhh-8888", FakeKey));
        Scrubber().ScrubAll().FilesRewritten.Should().Be(1);

        Scrubber().ScrubAll().FilesExamined.Should().Be(0, "the manifest says nothing changed");

        File.AppendAllText(file, ToolResultLine("hhhh-8888", "later " + FakeKey) + "\n");
        var third = Scrubber().ScrubAll();

        third.FilesRewritten.Should().Be(1);
        File.ReadAllText(file).Should().NotContain("Fake0123456789");
    }

    [Fact]
    public void A_manifest_written_under_an_older_redactor_policy_scrubs_everything_again()
    {
        Plant(_home, "-p", "iiii-9999.jsonl", Line(new { text = "nothing secret" }));
        Scrubber().ScrubAll();
        var manifestPath = Path.Combine(_data, CliTranscriptScrubber.ManifestFileName);
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["policy"] = TranscriptRedactor.PolicyRevision - 1;
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        Scrubber().ScrubAll().FilesExamined.Should().Be(1);
    }

    [Fact]
    public void The_default_home_is_only_owned_when_it_lives_on_the_data_volume()
    {
        var data = Path.Combine(Path.GetTempPath(), "dd-data");

        CliTranscriptScrubber.OwnedConfigHome(data, Path.Combine(data, "home", ".claude"))
            .Should().Be(Path.Combine(data, "home", ".claude"));
        CliTranscriptScrubber.OwnedConfigHome(data, Path.Combine(Path.GetTempPath(), "someone", ".claude"))
            .Should().BeNull("a developer's own CLI history on a host-side run is not the studio's to rewrite");
        CliTranscriptScrubber.OwnedConfigHome(data, data + "-other" + Path.DirectorySeparatorChar + ".claude")
            .Should().BeNull("a sibling folder that merely shares the prefix is not inside the data directory");
        CliTranscriptScrubber.OwnedConfigHome(data, null).Should().BeNull();
    }

    private static void FluentJson(string line)
    {
        var parse = () => JsonDocument.Parse(line).Dispose();
        parse.Should().NotThrow();
    }
}
