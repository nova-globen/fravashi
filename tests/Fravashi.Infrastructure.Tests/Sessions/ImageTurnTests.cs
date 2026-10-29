using System.Text.Json.Nodes;
using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 26: a pasted image reaches the agent in the shape the pinned CLI was measured to accept, and it is
/// kept with the transcript so the conversation still shows it after a reload.
/// </summary>
public class ImageTurnTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Sessions", "ImageTurnFixtures", name);

    private static AgentImageAttachment Probe() =>
        AgentImageAttachment.TryCreate(File.ReadAllBytes(FixturePath("probe.png")), out _)!;

    [Fact]
    public void The_user_turn_with_an_image_is_the_line_the_pinned_cli_answered()
    {
        // The recorded line is what was written to 2.1.222's stdin on 2026-09-15; the recorded stdout below is
        // its answer. Compared as JSON, not as text: key order and escaping are not part of the protocol.
        var recorded = JsonNode.Parse(File.ReadAllText(FixturePath("claude-image-turn.stdin.jsonl")));
        var built = JsonNode.Parse(ClaudeSessionRunner.UserTurn(
            "What word and number are written in this image? Reply with only them.", [Probe()]));

        JsonNode.DeepEquals(built, recorded).Should().BeTrue(
            "the runner should write exactly the shape the pinned CLI was driven with");
    }

    [Fact]
    public void The_pinned_cli_read_the_image_and_its_answer_parses_as_an_ordinary_turn()
    {
        var parser = new ClaudeStreamJsonParser(() => DateTimeOffset.UnixEpoch);
        List<AgentEvent> events =
            [.. File.ReadAllLines(FixturePath("claude-image-turn.stdout.jsonl")).SelectMany(parser.Parse)];
        events.AddRange(parser.Drain());

        events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant).Select(m => m.Text)
            .Should().ContainSingle().Which.Should().Be("ZEBRA 731", "that is the text rendered into probe.png");
        events.OfType<TurnCompleted>().Should().ContainSingle().Which.IsError.Should().BeFalse();
    }

    [Fact]
    public void A_turn_of_images_alone_carries_no_empty_text_block()
    {
        var content = JsonNode.Parse(ClaudeSessionRunner.UserTurn("  ", [Probe()]))!["message"]!["content"]!.AsArray();

        content.Should().ContainSingle();
        content[0]!["type"]!.GetValue<string>().Should().Be("image");
    }

    [Fact]
    public void A_turn_without_images_is_the_text_turn_it_always_was()
    {
        ClaudeSessionRunner.UserTurn("hello").Should().Be(
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"hello"}]}}""");
    }

    [Fact]
    public void The_images_row_round_trips_and_its_bytes_are_never_redacted()
    {
        // Base64 is the provider-key charset. A payload that happens to spell a key prefix must come back
        // byte for byte, because the redactor exists for text an agent printed and would only break an image.
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x3F }.CopyTo(bytes, 0);
        Convert.FromBase64String("AK" + "IAIOSFODNN7EXAMPLE").CopyTo(bytes, 12);

        // 0x3F and 0xFC encode as "/" either side of the key-shaped run, which is the boundary the pattern needs.
        bytes[27] = 0xFC;
        var image = AgentImageAttachment.TryCreate(bytes, out _)!;
        image.Base64.Should().Contain("AK" + "IAIOSFODNN7EXAMPLE", "the test only proves something if the shape is there");
        TranscriptRedactor.Redact(image.Base64).Should().NotBe(image.Base64, "the redactor would have eaten it");

        var e = new UserImagesAttached(DateTimeOffset.UnixEpoch, [image, Probe()]);
        var back = AgentEventJson.Deserialize(AgentEventJson.KindOf(e)!, AgentEventJson.Serialize(e));

        back.Should().BeOfType<UserImagesAttached>().Which.Images.Should().Equal(image, Probe());
    }

    [Fact]
    public void A_stored_image_that_is_not_what_it_claims_is_dropped_on_read()
    {
        const string json =
            """{"timestamp":"2026-09-15T00:00:00.0000000+00:00","images":[{"mediaType":"image/png","data":"PHN2Zy8+"}]}""";

        AgentEventJson.Deserialize("user_images", json).Should().BeNull();
    }

    [Fact]
    public void The_transcript_keeps_the_images_across_a_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-imageturn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new SqliteConnectionFactory(
                new SqliteStoreOptions(Path.Combine(directory, "fravashi.db"), Pooling: false));
            SqliteSchema.EnsureCreated(factory);
            var id = AgentSessionId.New();
            new SqliteSessionStore(factory).Append(id,
            [
                new UserImagesAttached(DateTimeOffset.UnixEpoch, [Probe()]),
                new MessageEmitted(DateTimeOffset.UnixEpoch, TranscriptRole.User, "what does it say?"),
            ]);

            // A second store over the same file is what a studio restart is.
            var transcript = new SqliteSessionStore(factory).Transcript(id);

            transcript.Should().HaveCount(2);
            transcript[0].Should().BeOfType<UserImagesAttached>().Which.Images.Single().Should().Be(Probe());
            transcript[1].Should().BeOfType<MessageEmitted>().Which.Text.Should().Be("what does it say?");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
