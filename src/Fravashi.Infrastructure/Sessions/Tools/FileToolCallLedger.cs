using System.Globalization;
using System.Text;
using System.Text.Json;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// The tool server's idempotency ledger, as one append-only JSON-lines file per session under
/// <c>&lt;data dir&gt;/agent-tool-ledger/</c> (backlog 84).
/// <para>
/// A file rather than a SQLite table on purpose: the ledger has exactly one reader and one writer (this class),
/// is never queried across sessions, and an append plus an fsync is the whole durability story, so a schema
/// migration would buy nothing but a merge conflict. It lives OUTSIDE the session's hook folder
/// (<c>agent-sessions/{id}</c>), because that folder is deleted when a session ends, and a call cut off by a
/// studio restart has to stay resolvable after it. A Continue starts a NEW studio session id and so a new ledger
/// scope, which loses nothing: the CLI's tool-use ids are unique per call and a resumed CLI does not re-send old ones.
/// </para>
/// <para>
/// The protocol is claim, then run, then complete. A <c>started</c> line is flushed to disk BEFORE the tool
/// runs. Every line carries the id of the ledger instance (one per studio process) that wrote it, so a
/// <c>started</c> with no outcome is told apart honestly: written by THIS process means the call is still
/// running (refuse a duplicate), written by an earlier process means the studio went away mid-call and nobody
/// knows whether it took effect, which is recorded as <c>interrupted</c> and answered as a failure forever
/// after. A torn last line (a crash inside a write) is skipped: a torn <c>started</c> was never durable, so the
/// tool never ran; a torn outcome leaves its <c>started</c> standing, which reads as interrupted.
/// </para>
/// </summary>
public sealed class FileToolCallLedger : IToolCallLedger
{
    private const string Started = "started";
    private const string Completed = "completed";
    private const string InterruptedState = "interrupted";

    private readonly string _root;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private readonly Lock _gate = new();
    private readonly Dictionary<AgentSessionId, Dictionary<string, Entry>> _sessions = [];

    public FileToolCallLedger(string dataDirectory, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _root = Path.Combine(dataDirectory, "agent-tool-ledger");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public ToolCallClaim Claim(AgentSessionId sessionId, string operationKey, string toolName, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(fingerprint);

        lock (_gate)
        {
            var entries = Load(sessionId);
            if (entries.TryGetValue(operationKey, out var existing))
            {
                if (!string.Equals(existing.Tool, toolName, StringComparison.Ordinal)
                    || !string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return new ToolCallClaim(ToolCallClaimOutcome.Conflict);
                }

                if (existing.State == Completed)
                {
                    return new ToolCallClaim(ToolCallClaimOutcome.Replayed, existing.Result);
                }

                if (existing.State == InterruptedState)
                {
                    return new ToolCallClaim(ToolCallClaimOutcome.Interrupted);
                }

                if (string.Equals(existing.Instance, _instance, StringComparison.Ordinal))
                {
                    return new ToolCallClaim(ToolCallClaimOutcome.InFlight);
                }

                // Claimed by an earlier studio process that never recorded an outcome. Recorded as interrupted
                // so the answer stays the same on every later replay, in this process and the next.
                var interrupted = existing with { State = InterruptedState, Instance = _instance };
                Append(sessionId, interrupted);
                entries[operationKey] = interrupted;
                return new ToolCallClaim(ToolCallClaimOutcome.Interrupted);
            }

            var claim = new Entry(operationKey, toolName, fingerprint, Started, _instance, null);

            // Durable first, visible second: if this throws, nothing was claimed and the call does not run.
            Append(sessionId, claim);
            entries[operationKey] = claim;
            return new ToolCallClaim(ToolCallClaimOutcome.Fresh);
        }
    }

    public void Complete(AgentSessionId sessionId, string operationKey, StudioToolResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            var entries = Load(sessionId);
            if (!entries.TryGetValue(operationKey, out var existing)
                || existing.State != Started
                || !string.Equals(existing.Instance, _instance, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Only a call this process claimed and has not completed can be completed.");
            }

            var completed = existing with { State = Completed, Result = result };
            Append(sessionId, completed);
            entries[operationKey] = completed;
        }
    }

    public void Forget(AgentSessionId sessionId)
    {
        lock (_gate)
        {
            _sessions.Remove(sessionId);
            try
            {
                File.Delete(PathFor(sessionId));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A lingering ledger file is not worth failing a delete over; it holds no secret.
            }
        }
    }

    public IReadOnlyList<InterruptedToolCall> RecoverInterrupted()
    {
        var found = new List<InterruptedToolCall>();
        if (!Directory.Exists(_root))
        {
            return found;
        }

        lock (_gate)
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*.jsonl"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var guid))
                {
                    continue;
                }

                var sessionId = new AgentSessionId(guid);
                var entries = Load(sessionId);
                foreach (var entry in entries.Values.ToList())
                {
                    if (entry.State != Started || string.Equals(entry.Instance, _instance, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var interrupted = entry with { State = InterruptedState, Instance = _instance };
                    try
                    {
                        Append(sessionId, interrupted);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Left as started: the next claim of that key still reads it as interrupted.
                        continue;
                    }

                    entries[entry.Key] = interrupted;
                    found.Add(new InterruptedToolCall(sessionId, entry.Key, entry.Tool));
                }
            }
        }

        return found;
    }

    /// <summary>Where a session's ledger lives. Exposed for tests and diagnosis.</summary>
    public string PathFor(AgentSessionId sessionId) => Path.Combine(_root, sessionId.ToString() + ".jsonl");

    private Dictionary<string, Entry> Load(AgentSessionId sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var loaded))
        {
            return loaded;
        }

        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var path = PathFor(sessionId);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (Parse(line) is { } entry)
                {
                    // Later lines win: the file is the history of each key's states, oldest first.
                    entries[entry.Key] = entry;
                }
            }
        }

        _sessions[sessionId] = entries;
        return entries;
    }

    private void Append(AgentSessionId sessionId, Entry entry)
    {
        Directory.CreateDirectory(_root);
        var bytes = Encoding.UTF8.GetBytes(Render(entry, _clock()) + "\n");
        using var stream = new FileStream(PathFor(sessionId), FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);

        // The whole point of the ledger: the claim must be on disk before the tool runs.
        stream.Flush(flushToDisk: true);
    }

    private static string Render(Entry entry, DateTimeOffset at)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("key", entry.Key);
            writer.WriteString("tool", entry.Tool);
            writer.WriteString("fingerprint", entry.Fingerprint);
            writer.WriteString("state", entry.State);
            writer.WriteString("instance", entry.Instance);
            writer.WriteString("at", at.ToString("O", CultureInfo.InvariantCulture));
            if (entry.Result is { } result)
            {
                writer.WriteString("text", result.Text);
                writer.WriteBoolean("isError", result.IsError);

                // Backlog 159: an image is a picture of a moment, never replayed, and base64 would swell this file;
                // how many there were is kept so a replay can say what it is not returning.
                if (result.Images is { Count: > 0 } images)
                {
                    writer.WriteNumber("images", images.Count);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static Entry? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var key = Text(root, "key");
            var tool = Text(root, "tool");
            var fingerprint = Text(root, "fingerprint");
            var state = Text(root, "state");
            var instance = Text(root, "instance");
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(tool) || fingerprint is null || instance is null
                || state is not (Started or Completed or InterruptedState))
            {
                return null;
            }

            StudioToolResult? result = null;
            if (state == Completed)
            {
                if (Text(root, "text") is not { } text
                    || !root.TryGetProperty("isError", out var isError)
                    || isError.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    // A completion without its result cannot be replayed honestly; the earlier started line
                    // stands and the call reads as interrupted.
                    return null;
                }

                if (root.TryGetProperty("images", out var images) && images.TryGetInt32(out var count) && count > 0)
                {
                    text += $"\n\n(This call returned {count} image(s) when it ran; images are not kept for a repeat of the same call. Take a new screenshot to see the page now.)";
                }

                result = new StudioToolResult(text, isError.GetBoolean());
            }

            return new Entry(key, tool, fingerprint, state, instance, result);
        }
        catch (JsonException)
        {
            // A torn line from a crash mid-write.
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record Entry(
        string Key,
        string Tool,
        string Fingerprint,
        string State,
        string Instance,
        StudioToolResult? Result);
}
