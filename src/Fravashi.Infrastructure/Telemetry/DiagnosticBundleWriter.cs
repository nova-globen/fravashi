using System.Globalization;
using System.IO.Compression;
using System.Text;
using Fravashi.Application.Sandbox;
using Fravashi.Application.Telemetry;
using Fravashi.Application.Updates;
using Fravashi.Domain.Telemetry;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;

namespace Fravashi.Infrastructure.Telemetry;

/// <summary>
/// Writes the diagnosis a user can hand us (backlog 74). One zip under
/// <c>&lt;data dir&gt;/diagnostics</c>, holding what someone reading a field report actually needs:
/// which build is running, what the launcher was able to harden, what shape the database is in, the
/// product events this install recorded, and the most recent log file from each of the three apps.
/// <para>
/// <b>Every text entry goes through <see cref="TranscriptRedactor"/> WHOLE on the way in.</b> The logs
/// are the risky part: a tool an agent ran can print an environment dump, and the whole reason this
/// bundle is safe to send is that the same redactor the transcript write path uses runs over the same
/// text here.
/// </para>
/// <para>
/// Whole, and never line by line, which is what an earlier version did: the redactor's provider
/// patterns match a key as JOINED RUNS across line breaks precisely because a wrapped key
/// leaked its tail (round three and the final round of its own hardening), so handing it one line at a
/// time silently disables that. A key split by a newline would then not be redacted AT ALL: the head
/// run falls under the replacer's 16-char material floor and comes back unchanged, and the
/// continuation line carries no provider prefix, so nothing matches it. The env rule loses nothing by
/// the whole-text call either, because of the NORMALIZED pass below: the rule is
/// <c>RegexOptions.Multiline</c>, so its anchors see exactly the lines a line-by-line call would have
/// handed it once every line break is one the anchors recognize. The memory argument was never real:
/// <see cref="ReadTail"/> already joins the whole 5000-line tail into one string before it gets here.
/// </para>
/// <para>
/// The whole text is redacted TWICE: once AS WRITTEN, then again after its line endings are
/// normalized to <c>\n</c>. Not because two passes are tidy, but because NEITHER order alone is safe,
/// and the claim that normalizing first was strictly better (the second adversarial review) was
/// false. Both halves were measured through this writer and both are pinned by tests:
/// </para>
/// <para>
/// Redacting only BEFORE the normalization loses the plain env case. The env rule's <c>^</c> and
/// <c>$</c> are <c>\n</c> anchors, while <see cref="ReadTail"/>'s <c>ReadLine</c> splits only on
/// <c>\r</c>, <c>\n</c> and <c>\r\n</c>: an exotic separator (U+2028, U+2029, U+0085, U+000C)
/// therefore arrives sitting INSIDE what the redactor sees as one line, the end anchor never matches,
/// and a real assignment rides into the zip unredacted. Normalizing turns it into a break the anchor
/// can see. For the PROVIDER wraps the normalized pass no longer has anything to add: until backlog 119
/// the redactor's raw joiners were only <c>\r\n</c>, <c>\n</c>, a lone <c>\r</c> and U+2028, so a key a
/// pane wrapped on U+000C, U+0085 or U+2029 after a head that cleared the 16-char floor lost its head to
/// the first pass and kept its TAIL in the clear through both (a test pinned that as a known gap).
/// Backlog 119 made every one of those breaks a joiner, let a joiner be followed by indentation, and
/// replaced the six-join cap with a material ceiling, so the first pass redacts each of those shapes
/// whole and the normalized pass meets a marker it leaves alone. The tests that pinned the gap now
/// assert the whole key, through this writer, for all three breaks, for indented continuation lines
/// and for a wrap far past the old cap. What the redactor still misses is documented on the redactor
/// (a prefix split by the wrap, a blank line or trailing whitespace before the break, shapes it has no
/// pattern for), and it is the redactor's to lift, never this writer's.
/// </para>
/// <para>
/// Redacting only AFTER it loses the JSON env case, which is the one this writer ships most of: a
/// docker inspect Env array or a serialized command printed into a log. That rule's value class
/// accepts every character except a quote, a backslash, <c>\r</c> and <c>\n</c>, so an exotic
/// separator inside such a value is ordinary value material and the rule redacts it. Normalize first
/// and the separator BECOMES a <c>\n</c>, which ends the value before its closing quote: the JSON
/// rule no longer matches, the line-anchored rule cannot pick it up either (the line starts with the
/// JSON punctuation, not with the assignment), and the value leaks WHOLE.
/// </para>
/// <para>
/// Redacting on both sides costs one more pass over a tail already in memory, and repeating it is
/// safe: a replacement only ever turns matched material into a <c>[redacted:...]</c> marker, so the
/// second pass can shorten but never restore, and a marker fed back through comes out as the same
/// marker rather than a nested one (pinned by a test). The ESCAPED two-character joiners are text and
/// not line endings, so normalizing never touches them.
/// </para>
/// <para>
/// Nothing uploads the file. It is written and left on disk, and the page reports the path, because
/// deciding to send a diagnosis is the user's decision and not the studio's.
/// </para>
/// </summary>
public sealed class DiagnosticBundleWriter : IDiagnosticBundleWriter
{
    /// <summary>The folder under the data directory bundles are written into.</summary>
    public const string FolderName = "diagnostics";

    /// <summary>
    /// How many trailing lines of each log file ride along. A log is append-only and days long; the
    /// tail is the part that describes the failure being reported, and the cap is what keeps a bundle
    /// small enough to attach to a message.
    /// </summary>
    public const int LogTailLines = 5000;

    /// <summary>
    /// How many bundles stay on disk (backlog 75). A bundle is a snapshot anyone can write again in a second,
    /// and every one carries log tails, so an unbounded folder of them is evidence kept for no one. Writing a
    /// new bundle deletes the oldest beyond this count.
    /// </summary>
    public const int MaxKeptBundles = 10;

    private readonly DataDirectory _dataDirectory;
    private readonly IProductInfo _product;
    private readonly ISandboxPosture _posture;
    private readonly ISqliteConnectionFactory _connections;
    private readonly IProductEventStore _events;
    private readonly Func<DateTimeOffset> _clock;

    public DiagnosticBundleWriter(
        DataDirectory dataDirectory,
        IProductInfo product,
        ISandboxPosture posture,
        ISqliteConnectionFactory connections,
        IProductEventStore events,
        Func<DateTimeOffset>? clock = null)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _product = product ?? throw new ArgumentNullException(nameof(product));
        _posture = posture ?? throw new ArgumentNullException(nameof(posture));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public DiagnosticBundle Write()
    {
        var stamp = _clock().ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var folder = Path.Combine(_dataDirectory.Path, FolderName);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"fravashi-diagnostics-{stamp}.zip");

        var entries = new List<string>();
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            AddText(archive, entries, "summary.txt", BuildSummary());
            AddText(archive, entries, "posture.txt", BuildPosture());
            AddText(archive, entries, "database.txt", BuildDatabaseInventory());
            AddText(archive, entries, "events.csv", BuildEvents());
            AddLogs(archive, entries);
        }

        var bundle = new DiagnosticBundle(path, new FileInfo(path).Length, entries);
        PruneOldBundles(folder, path);
        return bundle;
    }

    /// <summary>Deletes the oldest bundles beyond <see cref="MaxKeptBundles"/>, never the one just written.</summary>
    private static void PruneOldBundles(string folder, string justWritten)
    {
        try
        {
            var stale = Directory.EnumerateFiles(folder, "fravashi-diagnostics-*.zip")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(MaxKeptBundles)
                .Where(f => !string.Equals(f.FullName, Path.GetFullPath(justWritten), StringComparison.Ordinal))
                .ToList();
            foreach (var file in stale)
            {
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retention is best effort: a bundle that cannot be pruned now is pruned by the next write.
        }
    }

    // ---- sections -------------------------------------------------------------------------------

    private string BuildSummary()
    {
        var posture = _posture.Current;
        var text = new StringBuilder();
        text.AppendLine(Invariant($"generated_utc: {_clock().ToUniversalTime():O}"));
        text.AppendLine(Invariant($"product_version: {_product.Version}"));
        text.AppendLine(Invariant($"product_variant: {_product.Variant}"));
        text.AppendLine(Invariant($"schema_version: {SchemaVersion()}"));
        text.AppendLine(Invariant($"schema_version_expected: {SqliteSchema.CurrentVersion}"));
        text.AppendLine(Invariant($"runtime: {posture.Runtime}"));
        text.AppendLine(Invariant($"runtime_posture_known: {posture.Known}"));
        text.AppendLine(Invariant($"recorded_events: {SafeEventCount()}"));
        text.AppendLine();
        text.AppendLine("Every text entry in this bundle has been passed through the studio's transcript");
        text.AppendLine("redaction whole, which replaces known credential shapes with a [redacted:...]");
        text.AppendLine("marker, including a credential wrapped across line breaks.");
        return text.ToString();
    }

    private string BuildPosture()
    {
        var posture = _posture.Current;
        var text = new StringBuilder();
        text.AppendLine(Invariant($"runtime: {posture.Runtime}"));
        text.AppendLine(Invariant($"known: {posture.Known}"));
        text.AppendLine(Invariant($"checked_utc: {posture.CheckedUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "(never)"}"));
        text.AppendLine(Invariant($"active: {posture.ActiveCount}"));
        text.AppendLine(Invariant($"unavailable: {posture.UnavailableCount}"));
        text.AppendLine(Invariant($"stronger_runtime_recommended: {posture.StrongerRuntimeRecommended}"));
        if (posture.Recommendation is { Length: > 0 } recommendation)
        {
            text.AppendLine(Invariant($"recommendation: {recommendation}"));
        }

        text.AppendLine();
        foreach (var control in posture.Controls)
        {
            text.AppendLine(Invariant($"{control.Id}: {control.Status} - {control.Detail ?? control.Description}"));
        }

        return text.ToString();
    }

    /// <summary>
    /// The database's shape: its recorded schema version and how many rows each table holds. Counts
    /// only, never content: "the wiki store has 412 pages" is a diagnosis, and the pages are not.
    /// </summary>
    private string BuildDatabaseInventory()
    {
        var text = new StringBuilder();
        text.AppendLine(Invariant($"schema_version: {SchemaVersion()}"));
        text.AppendLine(Invariant($"schema_version_expected: {SqliteSchema.CurrentVersion}"));
        text.AppendLine();

        try
        {
            using var connection = _connections.Open();
            var tables = new List<string>();
            using (var list = connection.CreateCommand())
            {
                list.CommandText =
                    "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                using var reader = list.ExecuteReader();
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            foreach (var table in tables)
            {
                using var count = connection.CreateCommand();
                // The names come from sqlite_master, not from a caller, so they cannot be anything but
                // this database's own table names; SQLite has no parameter form for an identifier.
                count.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
                text.AppendLine(Invariant($"{table}: {count.ExecuteScalar()}"));
            }
        }
        catch (Exception ex)
        {
            text.AppendLine(Invariant($"(the database could not be inventoried: {ex.GetType().Name})"));
        }

        return text.ToString();
    }

    /// <summary>
    /// The recorded events as CSV. There is no redaction question here and there never can be: the
    /// columns are the payload schema, and it has no field text can travel in.
    /// </summary>
    private string BuildEvents()
    {
        var text = new StringBuilder();
        text.AppendLine("kind,occurred_utc,outcome,workspace_hash,duration_ms,count");

        IReadOnlyList<ProductEvent> events;
        try
        {
            events = _events.Recent();
        }
        catch (Exception ex)
        {
            text.AppendLine(Invariant($"(the recorded events could not be read: {ex.GetType().Name})"));
            return text.ToString();
        }

        foreach (var e in events)
        {
            text.AppendLine(string.Join(',',
                e.Kind.ToString(),
                e.OccurredUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                e.Outcome.ToString(),
                e.Workspace.Value,
                e.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                e.Count?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
        }

        return text.ToString();
    }

    /// <summary>
    /// The most recent log file from each app that writes into <c>&lt;data dir&gt;/logs</c>. Opened
    /// share-read-write because the studio is writing its own file as this runs.
    /// </summary>
    private void AddLogs(ZipArchive archive, List<string> entries)
    {
        var logs = Path.Combine(_dataDirectory.Path, "logs");
        if (!Directory.Exists(logs))
        {
            AddText(archive, entries, "logs/README.txt", "No log folder existed when this bundle was written.");
            return;
        }

        foreach (var app in new[] { "studio", "launcher", "window" })
        {
            var newest = Directory.EnumerateFiles(logs, app + "-*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (newest is null)
            {
                AddText(archive, entries, $"logs/{app}.txt", $"No {app} log file was present.");
                continue;
            }

            AddText(archive, entries, "logs/" + newest.Name, ReadTail(newest.FullName));
        }
    }

    private static string ReadTail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var tail = new Queue<string>(LogTailLines);
            while (reader.ReadLine() is { } line)
            {
                tail.Enqueue(line);
                if (tail.Count > LogTailLines)
                {
                    tail.Dequeue();
                }
            }

            return string.Join(Environment.NewLine, tail);
        }
        catch (IOException ex)
        {
            return $"(this log could not be read: {ex.GetType().Name})";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"(this log could not be read: {ex.GetType().Name})";
        }
    }

    // ---- plumbing -------------------------------------------------------------------------------

    /// <summary>
    /// Writes one entry, REDACTED. Every text entry goes through here and there is no other way to add
    /// one, which is what makes "everything in the bundle is redacted" a property of the writer rather
    /// than a habit each section has to keep.
    /// <para>
    /// The redactor is handed the WHOLE entry in one call, never a line at a time, and it is handed it
    /// TWICE: once as written and once after the line endings are normalized to <c>\n</c>. Each pass
    /// catches a class the other provably misses, both measured through this writer. See the class doc.
    /// </para>
    /// </summary>
    private static void AddText(ZipArchive archive, List<string> entries, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using (var stream = entry.Open())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            var redacted = TranscriptRedactor.Redact(content);
            writer.Write(TranscriptRedactor.Redact(redacted.ReplaceLineEndings("\n")));
        }

        entries.Add(name);
    }

    private string SchemaVersion()
    {
        try
        {
            using var connection = _connections.Open();
            return SchemaMigrator.StoredVersion(connection).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            return $"(unreadable: {ex.GetType().Name})";
        }
    }

    private string SafeEventCount()
    {
        try
        {
            return _events.Count().ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            return $"(unreadable: {ex.GetType().Name})";
        }
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
