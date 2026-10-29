using System.Globalization;
using System.Text;
using System.Text.Json;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>What one conversation file yields: a title, when it started, and the text the two sides said.</summary>
public sealed record ParsedCliConversation(string Title, DateTimeOffset StartedUtc, IReadOnlyList<MessageEmitted> Messages);

/// <summary>
/// Rebuilds a DISPLAY transcript from a Claude CLI conversation file (backlog 43). One JSON object per
/// line; only the <c>user</c> and <c>assistant</c> lines carry what was said, and of those only their text
/// is kept. Thinking, tool calls, tool results, token usage and cost are deliberately not reconstructed:
/// the studio's own sessions record those as they happen, and pretending to recover them from here would
/// make an import look complete when it is not.
/// <para>
/// Lenient by design, because the file is the CLI's and its line types move between versions: a line
/// that is not JSON (the tail of a file still being written) is skipped, an unknown type is skipped, and a
/// file lacking any particular line type is still a conversation. The archived scanner discarded such a
/// file; this one keeps it, even with no messages at all.
/// </para>
/// Sidechain lines (a subagent's own conversation) and <c>isMeta</c> lines (harness-injected context the
/// user never typed) are not part of what the user and the agent said to each other, so they are skipped.
/// The title is the user's own rename when there is one, then the CLI's generated title, then the first
/// thing the user said.
/// </summary>
public static class CliConversationTranscriptParser
{
    public const string UntitledTitle = "Untitled conversation";

    private const int TitleLength = 80;

    public static ParsedCliConversation Parse(byte[] content, DateTimeOffset fallbackTimestamp)
    {
        ArgumentNullException.ThrowIfNull(content);

        var messages = new List<MessageEmitted>();
        string? customTitle = null;
        string? aiTitle = null;
        string? lastAssistantId = null;
        DateTimeOffset? started = null;
        var clock = fallbackTimestamp;

        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (Timestamp(root) is { } stamp)
                {
                    clock = stamp;
                    started ??= stamp;
                }

                switch (String(root, "type"))
                {
                    case "custom-title":
                        customTitle = String(root, "customTitle") is { Length: > 0 } custom ? custom : customTitle;
                        break;

                    case "ai-title":
                        aiTitle = String(root, "aiTitle") is { Length: > 0 } generated ? generated : aiTitle;
                        break;

                    case "user" when IsConversational(root):
                        if (Text(root) is { Length: > 0 } userText)
                        {
                            messages.Add(new MessageEmitted(clock, TranscriptRole.User, userText));
                            lastAssistantId = null;
                        }

                        break;

                    case "assistant" when IsConversational(root):
                        if (Text(root) is { Length: > 0 } assistantText)
                        {
                            var id = root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                                ? String(message, "id")
                                : null;

                            // The CLI writes each content block of one reply as its own line under the same
                            // message id; they read as one reply, so they are shown as one.
                            if (id is not null && id == lastAssistantId && messages.Count > 0
                                && messages[^1].Role == TranscriptRole.Assistant)
                            {
                                messages[^1] = messages[^1] with { Text = messages[^1].Text + "\n\n" + assistantText };
                            }
                            else
                            {
                                messages.Add(new MessageEmitted(clock, TranscriptRole.Assistant, assistantText));
                            }

                            lastAssistantId = id;
                        }

                        break;
                }
            }
        }

        var title = customTitle ?? aiTitle ?? FirstUserLine(messages) ?? UntitledTitle;
        return new ParsedCliConversation(title, started ?? fallbackTimestamp, messages);
    }

    private static bool IsConversational(JsonElement root) =>
        !Flag(root, "isSidechain") && !Flag(root, "isMeta");

    private static string? Text(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()?.Trim();
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object && String(block, "type") == "text"
                && String(block, "text") is { } text && !string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text.Trim());
            }
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static string? FirstUserLine(IReadOnlyList<MessageEmitted> messages)
    {
        var first = messages.FirstOrDefault(m => m.Role == TranscriptRole.User)?.Text;
        if (first is null)
        {
            return null;
        }

        var line = first.Split('\n', 2)[0].Trim();
        return line.Length <= TitleLength ? line : line[..(TitleLength - 3)].TrimEnd() + "...";
    }

    private static DateTimeOffset? Timestamp(JsonElement root) =>
        String(root, "timestamp") is { } raw
        && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
