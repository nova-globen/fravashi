using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>What one scrub pass did, in counts only (never a path's content).</summary>
/// <param name="FilesExamined">Conversation files read.</param>
/// <param name="FilesRewritten">Files that held something to redact and were atomically replaced.</param>
/// <param name="LinesRedacted">Lines whose content changed.</param>
/// <param name="FilesSkippedBusy">Files that changed on disk while being scrubbed and were left for a later pass.</param>
/// <param name="FilesFailed">Files that could not be read or replaced.</param>
public sealed record CliTranscriptScrubResult(
    int FilesExamined,
    int FilesRewritten,
    int LinesRedacted,
    int FilesSkippedBusy,
    int FilesFailed)
{
    /// <summary>Nothing examined, nothing done.</summary>
    public static CliTranscriptScrubResult None { get; } = new(0, 0, 0, 0, 0);

    internal CliTranscriptScrubResult Add(CliTranscriptScrubResult other) => new(
        FilesExamined + other.FilesExamined,
        FilesRewritten + other.FilesRewritten,
        LinesRedacted + other.LinesRedacted,
        FilesSkippedBusy + other.FilesSkippedBusy,
        FilesFailed + other.FilesFailed);
}

/// <summary>
/// Backlog 116: the Claude CLI keeps its OWN transcript of every session, every tool result verbatim, as
/// JSONL under its config home (<c>projects/&lt;cwd-hash&gt;/&lt;session-id&gt;.jsonl</c>, plus subagent
/// sidechains under <c>projects/&lt;cwd-hash&gt;/&lt;session-id&gt;/</c>). In the container that home is
/// <c>$HOME/.claude</c> = <c>/data/home/.claude</c> on the PERSISTENT volume, or a session's projected
/// <c>&lt;data&gt;/agent-sessions/{id}/claude</c>. Everything <see cref="TranscriptRedactor"/> keeps out of
/// the studio's own store was sitting one directory over, unredacted and durable.
/// <para>
/// <b>The choice is to SCRUB, not to delete or relocate.</b> Continuing a conversation runs the CLI with
/// <c>--resume</c>, which reads exactly this file back; a resumed session reuses the session's config home
/// precisely so the file is found. Deleting it or moving it somewhere ephemeral would break Continue. So
/// the same redactor the studio's transcript store uses is run over the file, in place, at two moments:
/// when a session ENDS (only that session's files, by the CLI conversation ids it announced, because other
/// sessions may be appending to their own files in the same home), and when the studio STARTS (every
/// conversation file under every config home, before any session can be running).
/// </para>
/// <para>
/// <b>The file must still resume afterwards</b>, so the rewrite is structural, never textual: each line is
/// read as a JSON token stream, every string VALUE is redacted through the same per-string
/// <see cref="TranscriptRedactor.Redact"/> call <see cref="TranscriptRedactor.RedactInPlace"/> makes for the
/// studio's store (property names, numbers, ids and the line order are untouched), and only a line that actually changed
/// is re-serialized. Structural for two reasons: the redactor then sees the DECODED text the model saw (a
/// line break or an escaped quote the CLI stored as a JSON escape is the real character there, exactly as
/// the studio's own store redacts it through the same call), and the output is valid JSON by construction
/// rather than by an argument about where a match can start and end. A line that is not JSON at all (a
/// torn final line from a killed process) is already unreadable to the CLI, so it gets the plain text
/// redaction. The replacement is atomic: a temporary file in the same
/// folder, flushed, given the original's permissions, then renamed over the original, and abandoned if the
/// original changed size or timestamp while it was being read.
/// </para>
/// <para>
/// <b>Idempotent by construction</b>: a redacted line redacts to itself, so a second pass rewrites nothing.
/// The startup pass keeps a small manifest (<see cref="ManifestFileName"/> in the data directory) of each
/// file's size and timestamp after it was last scrubbed under the current
/// <see cref="TranscriptRedactor.PolicyRevision"/>, so an unchanged file is not re-read on every start and a
/// widened redactor re-scrubs everything once.
/// </para>
/// <para>
/// What this does NOT cover (written into the agent threat model): the file is unredacted between the
/// moment the CLI writes a line and the session's end (a live session's home is plaintext while it runs,
/// and a studio crash defers the scrub to the next start); the redactor's own misses (a credential shape it
/// has no pattern for is left as written); and the CLI's other files in the same home (file-history
/// checkpoints, shell snapshots, todos, debug logs), which are not conversation transcripts and are not
/// rewritten.
/// </para>
/// </summary>
public sealed class CliTranscriptScrubber
{
    /// <summary>The startup manifest's file name, under the data directory.</summary>
    public const string ManifestFileName = "cli-transcript-scrub.json";

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        // The CLI reads these lines with JSON.parse; relaxed escaping keeps non-ASCII text as written
        // instead of turning every accented character into a \u escape.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _dataDirectory;
    private readonly string? _defaultConfigHome;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    /// <param name="dataDirectory">The studio's data directory (holds <c>agent-sessions/</c> and the manifest).</param>
    /// <param name="defaultConfigHome">
    /// The config home a session with no projected <c>CLAUDE_CONFIG_DIR</c> writes into; null means
    /// <see cref="OwnedDefaultConfigHome"/> of this process (the default home, only when it is on the data volume).
    /// </param>
    /// <param name="logger">Counts and failures only; never a line's content.</param>
    public CliTranscriptScrubber(string dataDirectory, string? defaultConfigHome = null, ILogger<CliTranscriptScrubber>? logger = null)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _defaultConfigHome = defaultConfigHome ?? OwnedDefaultConfigHome(dataDirectory);
        _logger = logger ?? NullLogger<CliTranscriptScrubber>.Instance;
    }

    /// <summary>
    /// The default config home, but ONLY when it lives inside the studio's data directory. In the container
    /// that is always true (<c>HOME=/data/home</c>), so the home every session without a projection writes
    /// into is scrubbed. On a host-side development run the default home is the developer's OWN
    /// <c>~/.claude</c>, which holds their own CLI's conversations rather than evidence the studio made, and
    /// rewriting it would reach far past what the studio owns, so it is left alone (null).
    /// </summary>
    public static string? OwnedDefaultConfigHome(string dataDirectory) =>
        OwnedConfigHome(dataDirectory, DefaultConfigHome());

    /// <summary>The ownership rule of <see cref="OwnedDefaultConfigHome"/>, for a given home.</summary>
    public static string? OwnedConfigHome(string dataDirectory, string? home)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);

        if (home is null)
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(home);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.StartsWith(root, comparison) ? home : null;
    }

    /// <summary>
    /// The config home a child with no <c>CLAUDE_CONFIG_DIR</c> of its own uses: an explicit
    /// <c>CLAUDE_CONFIG_DIR</c> on this process (children inherit it through the allowlist), otherwise
    /// <c>$HOME/.claude</c>.
    /// </summary>
    public static string? DefaultConfigHome()
    {
        var explicitHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(explicitHome))
        {
            return explicitHome;
        }

        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE");
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".claude");
    }

    /// <summary>
    /// Scrubs one ended session's conversation files: every <c>&lt;id&gt;.jsonl</c> and every JSONL under
    /// <c>&lt;id&gt;/</c> in the projects folder of <paramref name="configDirectory"/> (or the default home),
    /// for each CLI conversation id the session announced. Never throws.
    /// </summary>
    public CliTranscriptScrubResult ScrubSession(string? configDirectory, IEnumerable<string> conversationIds)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);

        var home = string.IsNullOrWhiteSpace(configDirectory) ? _defaultConfigHome : configDirectory;
        var projects = home is null ? null : Path.Combine(home, "projects");
        if (projects is null || !Directory.Exists(projects))
        {
            return CliTranscriptScrubResult.None;
        }

        var files = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var id in conversationIds.Where(IsSafeConversationId).Distinct(StringComparer.Ordinal))
            {
                foreach (var project in Directory.EnumerateDirectories(projects))
                {
                    var main = Path.Combine(project, id + ".jsonl");
                    if (File.Exists(main))
                    {
                        files.Add(main);
                    }

                    var sidechains = Path.Combine(project, id);
                    if (Directory.Exists(sidechains))
                    {
                        foreach (var file in Directory.EnumerateFiles(sidechains, "*.jsonl", SearchOption.AllDirectories))
                        {
                            files.Add(file);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The agent CLI transcript scrub could not list a session's files: {Error}", ex.GetType().Name);
            return new CliTranscriptScrubResult(0, 0, 0, 0, 1);
        }

        lock (_gate)
        {
            var result = CliTranscriptScrubResult.None;
            foreach (var file in files)
            {
                result = result.Add(ScrubFile(file).Result);
            }

            Log("session end", result);
            return result;
        }
    }

    /// <summary>
    /// Scrubs every conversation file under every config home the studio's sessions use: the default home and
    /// each <c>&lt;data&gt;/agent-sessions/*/claude</c>. Meant for studio start, before any session can run.
    /// Files unchanged since the manifest recorded them under the current policy are skipped. Never throws.
    /// </summary>
    public CliTranscriptScrubResult ScrubAll()
    {
        lock (_gate)
        {
            var manifest = ReadManifest();
            var next = new Dictionary<string, FileStamp>(StringComparer.Ordinal);
            var result = CliTranscriptScrubResult.None;

            foreach (var file in AllConversationFiles())
            {
                var before = Stamp(file);
                if (before is null)
                {
                    continue;
                }

                if (manifest.TryGetValue(file, out var known) && known == before)
                {
                    next[file] = before;
                    continue;
                }

                var (fileResult, after) = ScrubFile(file);
                result = result.Add(fileResult);
                if (after is not null && fileResult.FilesFailed == 0 && fileResult.FilesSkippedBusy == 0)
                {
                    next[file] = after;
                }
            }

            WriteManifest(next);
            Log("studio start", result);
            return result;
        }
    }

    /// <summary>
    /// The default config home this scrubber treats as the studio's (null on a host-side run whose home is
    /// the developer's own). Evidence listing and deletion use the same answer, so the page never offers to
    /// delete a folder the scrub would not touch.
    /// </summary>
    public string? DefaultHome => _defaultConfigHome;

    /// <summary>The config homes a scrub-all visits, in a stable order.</summary>
    public IReadOnlyList<string> ConfigHomes()
    {
        var homes = new List<string>();
        if (_defaultConfigHome is not null)
        {
            homes.Add(_defaultConfigHome);
        }

        var sessions = Path.Combine(_dataDirectory, "agent-sessions");
        try
        {
            if (Directory.Exists(sessions))
            {
                homes.AddRange(Directory.EnumerateDirectories(sessions)
                    .Select(d => Path.Combine(d, "claude"))
                    .Where(Directory.Exists)
                    .Order(StringComparer.Ordinal));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The agent CLI transcript scrub could not list session config homes: {Error}", ex.GetType().Name);
        }

        return homes;
    }

    private IEnumerable<string> AllConversationFiles()
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var home in ConfigHomes())
        {
            try
            {
                var projects = Path.Combine(home, "projects");
                if (Directory.Exists(projects))
                {
                    foreach (var file in Directory.EnumerateFiles(projects, "*.jsonl", SearchOption.AllDirectories))
                    {
                        files.Add(file);
                    }
                }

                // The CLI's prompt history: what was typed, per project, which is where a pasted key lands.
                var history = Path.Combine(home, "history.jsonl");
                if (File.Exists(history))
                {
                    files.Add(history);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("The agent CLI transcript scrub could not list a config home: {Error}", ex.GetType().Name);
            }
        }

        return files;
    }

    /// <summary>
    /// Redacts one JSONL file in place, atomically. Returns the counts and the file's stamp afterwards (null
    /// when the file could not be read).
    /// </summary>
    internal (CliTranscriptScrubResult Result, FileStamp? After) ScrubFile(string path)
    {
        try
        {
            var before = Stamp(path);
            if (before is null)
            {
                return (CliTranscriptScrubResult.None, null);
            }

            string original;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true))
            {
                original = reader.ReadToEnd();
            }

            var (rewritten, changedLines) = RedactJsonLines(original);
            if (changedLines == 0)
            {
                return (new CliTranscriptScrubResult(1, 0, 0, 0, 0), before);
            }

            var directory = Path.GetDirectoryName(path)!;
            var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + ".scrub-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = Utf8NoBom.GetBytes(rewritten);
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush(flushToDisk: true);
                }

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
                }

                // The CLI appends to this file by path. A change between the read and the rename would be lost
                // by the rename, so a file that moved underneath is left for the next pass instead.
                if (Stamp(path) != before)
                {
                    File.Delete(temporary);
                    return (new CliTranscriptScrubResult(1, 0, 0, 1, 0), null);
                }

                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            return (new CliTranscriptScrubResult(1, 1, changedLines, 0, 0), Stamp(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The agent CLI transcript scrub could not rewrite a file: {Error}", ex.GetType().Name);
            return (new CliTranscriptScrubResult(1, 0, 0, 0, 1), null);
        }
    }

    /// <summary>
    /// Redacts a JSONL document line by line, preserving every line ending and the trailing newline exactly.
    /// A JSON line is redacted structurally (string values only) and re-serialized only when it changed; a
    /// line that is not JSON gets the plain text redaction. Returns the text and how many lines changed.
    /// </summary>
    public static (string Text, int ChangedLines) RedactJsonLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var output = new StringBuilder(text.Length);
        var changed = 0;
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline;
            var contentEnd = end > start && text[end - 1] == '\r' ? end - 1 : end;
            var line = text[start..contentEnd];

            var redacted = RedactLine(line);
            if (!string.Equals(redacted, line, StringComparison.Ordinal))
            {
                changed++;
            }

            output.Append(redacted);
            output.Append(text, contentEnd, (newline < 0 ? text.Length : newline + 1) - contentEnd);
            start = newline < 0 ? text.Length : newline + 1;
        }

        return (output.ToString(), changed);
    }

    private static string RedactLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return line;
        }

        try
        {
            return RedactJsonValue(line) ?? line;
        }
        catch (JsonException)
        {
            // Not JSON: a torn last line the CLI could not read back anyway. The secret still matters.
            return TranscriptRedactor.Redact(line);
        }
    }

    /// <summary>
    /// Rewrites one JSON document token by token, redacting every string VALUE through
    /// <see cref="TranscriptRedactor.Redact"/> (the same per-string call <see cref="TranscriptRedactor.RedactInPlace"/>
    /// makes for the studio's store) and copying everything else as it came: numbers as their raw text,
    /// property names, nesting and order. A token stream rather than a node tree because a tree refuses a
    /// duplicate property name, and a line it refused would have kept its secret. Returns null when no string
    /// changed, so an untouched line is never re-serialized. Throws <see cref="JsonException"/> for text that is
    /// not exactly one JSON document.
    /// </summary>
    private static string? RedactJsonValue(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        using var buffer = new MemoryStream(bytes.Length + 64);
        var changed = false;
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = LineOptions.Encoder }))
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        writer.WriteStartObject();
                        break;
                    case JsonTokenType.EndObject:
                        writer.WriteEndObject();
                        break;
                    case JsonTokenType.StartArray:
                        writer.WriteStartArray();
                        break;
                    case JsonTokenType.EndArray:
                        writer.WriteEndArray();
                        break;
                    case JsonTokenType.PropertyName:
                        writer.WritePropertyName(reader.GetString()!);
                        break;
                    case JsonTokenType.String:
                        var value = reader.GetString()!;
                        var redacted = IsImageData(value) ? value : TranscriptRedactor.Redact(value);
                        changed |= !string.Equals(redacted, value, StringComparison.Ordinal);
                        writer.WriteStringValue(redacted);
                        break;
                    case JsonTokenType.Number:
                        writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                        break;
                    case JsonTokenType.True:
                        writer.WriteBooleanValue(true);
                        break;
                    case JsonTokenType.False:
                        writer.WriteBooleanValue(false);
                        break;
                    case JsonTokenType.Null:
                        writer.WriteNullValue();
                        break;
                    default:
                        throw new JsonException("Unexpected JSON token.");
                }
            }
        }

        return changed ? Encoding.UTF8.GetString(buffer.ToArray()) : null;
    }

    /// <summary>
    /// A conversation id is used as a file name, so only the shape the CLI uses (a UUID-like run of letters,
    /// digits and hyphens) is accepted; anything else is ignored rather than turned into a path.
    /// </summary>
    private static bool IsSafeConversationId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= 128
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    internal sealed record FileStamp(long Length, long LastWriteTicks);

    private static FileStamp? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string ManifestPath => Path.Combine(_dataDirectory, ManifestFileName);

    private Dictionary<string, FileStamp> ReadManifest()
    {
        var entries = new Dictionary<string, FileStamp>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(ManifestPath))
            {
                return entries;
            }

            var root = JsonNode.Parse(File.ReadAllText(ManifestPath)) as JsonObject;
            if (root?["policy"]?.GetValue<int>() != TranscriptRedactor.PolicyRevision
                || root["files"] is not JsonObject files)
            {
                // Written under an older redactor: every file is scrubbed again under the current one.
                return entries;
            }

            foreach (var (path, value) in files)
            {
                if (value is JsonArray { Count: 2 } pair)
                {
                    entries[path] = new FileStamp(pair[0]!.GetValue<long>(), pair[1]!.GetValue<long>());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidOperationException or FormatException)
        {
            // An unreadable manifest only costs a full pass.
            entries.Clear();
        }

        return entries;
    }

    private void WriteManifest(Dictionary<string, FileStamp> entries)
    {
        try
        {
            var files = new JsonObject();
            foreach (var (path, stamp) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                files[path] = new JsonArray(stamp.Length, stamp.LastWriteTicks);
            }

            var root = new JsonObject
            {
                ["policy"] = TranscriptRedactor.PolicyRevision,
                ["files"] = files,
            };

            Directory.CreateDirectory(_dataDirectory);
            var temporary = ManifestPath + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(), Utf8NoBom);
            File.Move(temporary, ManifestPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The agent CLI transcript scrub manifest could not be written: {Error}", ex.GetType().Name);
        }
    }

    private void Log(string moment, CliTranscriptScrubResult result)
    {
        if (result.FilesExamined == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Agent CLI transcript scrub ({Moment}): {Examined} file(s) examined, {Rewritten} rewritten, {Lines} line(s) redacted, {Busy} busy, {Failed} failed.",
            moment,
            result.FilesExamined.ToString(CultureInfo.InvariantCulture),
            result.FilesRewritten.ToString(CultureInfo.InvariantCulture),
            result.LinesRedacted.ToString(CultureInfo.InvariantCulture),
            result.FilesSkippedBusy.ToString(CultureInfo.InvariantCulture),
            result.FilesFailed.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Whether a string is an image's base64 payload (backlog 159): long, and nothing but the base64 alphabet. A
    /// screenshot the agent took is stored in the conversation file this way, and the redactor's AKIA pattern can match
    /// inside one by chance, which would corrupt the image and make a resume of the conversation fail at the provider.
    /// Nothing is lost by skipping it: every pattern the redactor knows needs characters base64 does not have, apart
    /// from an AWS key id, and one of those inside a blob that long is already encoded past what the redactor sees.
    /// </summary>
    public static bool IsImageData(string value)
    {
        if (value.Length < 1024)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
            {
                return false;
            }
        }

        return true;
    }
}
