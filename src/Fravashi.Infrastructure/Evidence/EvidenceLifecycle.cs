using Fravashi.Application.Evidence;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Logging;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Telemetry;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Evidence;

/// <summary>
/// Backlog 75: the evidence the studio keeps TODAY, listed with its size and its policy, and deletable a class
/// at a time. The written policy is <c>docs/evidence-lifecycle.md</c>; the strings here are the same policy in
/// the words the Settings page shows, and the tests hold the two to what the code does.
/// <para>
/// "Deleted" means removed from disk by this process: files and folders are deleted, and database rows are
/// deleted and then the database is checkpointed and VACUUMed so the rows are not left behind in free pages or
/// the write-ahead log. It is not a secure wipe (a filesystem or an SSD can keep old blocks), and a copy that
/// went into a BACKUP stays there until the Backups class is deleted too. Nothing here is synced to a provider
/// yet; "gone from the provider" becomes part of a delete when item 83 adds a push path.
/// </para>
/// <para>
/// The three session-shaped classes (transcripts, CLI conversations, session artifacts) refuse to delete while
/// ANY agent session is running, because a running session is writing to all three.
/// </para>
/// </summary>
public sealed class EvidenceLifecycle : IEvidenceLifecycle
{
    /// <summary>The message a session-shaped class gives while a session runs.</summary>
    public const string SessionRunningReason =
        "An agent session is running and still writing here. Stop it first, then delete.";

    private readonly DataDirectory _dataDirectory;
    private readonly ISqliteConnectionFactory _connections;
    private readonly ISessionStore _sessions;
    private readonly CliTranscriptScrubber _cliTranscripts;
    private readonly Func<bool> _anySessionRunning;
    private readonly Func<AgentSessionId, bool> _forgetSession;
    private readonly ILogger _logger;

    /// <param name="dataDirectory">The studio's data directory.</param>
    /// <param name="connections">The studio database, for transcript sizes and the post-delete VACUUM.</param>
    /// <param name="sessions">The session store, to list every stored conversation.</param>
    /// <param name="cliTranscripts">The CLI transcript scrubber, whose owned default home is the one listed here.</param>
    /// <param name="anySessionRunning">True while any agent session is running in this process.</param>
    /// <param name="forgetSession">
    /// Forgets one conversation and its transcript (the live registry's delete, so an open list drops it too);
    /// defaults to the store's delete.
    /// </param>
    /// <param name="logger">Counts and failure kinds only.</param>
    public EvidenceLifecycle(
        DataDirectory dataDirectory,
        ISqliteConnectionFactory connections,
        ISessionStore sessions,
        CliTranscriptScrubber cliTranscripts,
        Func<bool> anySessionRunning,
        Func<AgentSessionId, bool>? forgetSession = null,
        ILogger<EvidenceLifecycle>? logger = null)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _cliTranscripts = cliTranscripts ?? throw new ArgumentNullException(nameof(cliTranscripts));
        _anySessionRunning = anySessionRunning ?? throw new ArgumentNullException(nameof(anySessionRunning));
        _forgetSession = forgetSession ?? (id =>
        {
            _sessions.Delete(id);
            return true;
        });
        _logger = logger ?? NullLogger<EvidenceLifecycle>.Instance;
    }

    private string Data => _dataDirectory.Path;

    private string DiagnosticsFolder => Path.Combine(Data, DiagnosticBundleWriter.FolderName);

    private string BackupsFolder => Path.Combine(Data, Backup.StudioBackupWriter.FolderName);

    private string SessionsFolder => Path.Combine(Data, "agent-sessions");

    private string TrashFolder => Path.Combine(Data, "trash");

    private string LogsFolder => Path.Combine(Data, "logs");

    private string VerificationFolder => Path.Combine(Data, Verification.VerificationArtifactStore.FolderName);

    /// <inheritdoc />
    public IReadOnlyList<EvidenceClassReport> Inventory() =>
        [.. Enum.GetValues<EvidenceClass>().Select(Report)];

    /// <inheritdoc />
    public EvidenceClassReport ReportFor(EvidenceClass evidenceClass) => Report(evidenceClass);

    private EvidenceClassReport Report(EvidenceClass evidenceClass)
    {
        try
        {
            return evidenceClass switch
            {
                EvidenceClass.DiagnosticBundles => FilesReport(
                    evidenceClass,
                    "Diagnostic bundles",
                    DiagnosticsFolder,
                    Files(DiagnosticsFolder, "*.zip", recursive: false),
                    Invariant($"The newest {DiagnosticBundleWriter.MaxKeptBundles} are kept; writing another deletes the oldest beyond that."),
                    "Every entry is passed through the transcript redactor whole, before and after line-ending normalisation, as it is written.",
                    "Removes every bundle zip. Nothing else depends on them.",
                    blocked: null),
                EvidenceClass.Backups => FilesReport(
                    evidenceClass,
                    "Backups",
                    BackupsFolder,
                    Files(BackupsFolder, "*", recursive: true),
                    "Kept until you delete them. Nothing is removed automatically, because a backup is often the only copy.",
                    "Not redacted: a backup is a restore image. Transcripts inside it were redacted when they were stored, but the archive carries the key ring and the database whole.",
                    $"Removes every backup zip and every pre-restore folder {ProductName.Command} restore left behind. After this there is nothing to restore from.",
                    blocked: null),
                EvidenceClass.AgentTranscripts => TranscriptsReport(),
                EvidenceClass.AgentCliConversations => FilesReport(
                    evidenceClass,
                    "Agent CLI conversation files",
                    _cliTranscripts.DefaultHome is { } home ? Path.Combine(home, "projects") : "(no studio-owned CLI home on this machine)",
                    CliConversationFiles(),
                    "The CLI's own cleanup applies (its documented default is 30 days since last activity); the studio does not shorten it.",
                    "Scrubbed in place with the transcript redactor when each session ends and on every studio start (backlog 116); plaintext while a session is running.",
                    "Removes the CLI's conversation files and prompt history, never its sign-in. Continue can no longer resume those conversations; their transcripts in the studio stay.",
                    blocked: RunningBlock()),
                EvidenceClass.SessionArtifacts => FilesReport(
                    evidenceClass,
                    "Session artifacts",
                    SessionsFolder,
                    Files(SessionsFolder, "*", recursive: true),
                    "Each session's folder is removed when the session ends, unless the studio stopped before it could.",
                    "Hook files carry no conversation. A projected CLI config home's conversation files are scrubbed like the CLI files above.",
                    "Removes every session folder, including projected CLI config homes. Continue can no longer resume a conversation that ran with a Settings > Framework projection.",
                    blocked: RunningBlock()),
                EvidenceClass.TrashArchives => FilesReport(
                    evidenceClass,
                    "Deleted-file archives",
                    TrashFolder,
                    Files(TrashFolder, "*.zip", recursive: false),
                    "Kept until you delete them. Nothing is removed automatically, because each archive is the only copy of the files you deleted.",
                    "Not redacted: an archive holds your workspace files exactly as they were, so it can restore them.",
                    "Removes every archive. The explorer's undo can no longer restore a deletion.",
                    blocked: null),
                EvidenceClass.Logs => FilesReport(
                    evidenceClass,
                    "Logs",
                    LogsFolder,
                    Files(LogsFolder, "*.log", recursive: false),
                    Invariant($"Studio log files older than {FileLoggerProvider.RetentionDays} days are deleted as the studio writes; launcher and window logs are written by {ProductName.Executable} and kept until you delete them."),
                    "Every studio log line passes through the transcript redactor before it is written; launcher and window logs are redacted only when they go into a diagnostic bundle.",
                    "Removes every log file that is not open for writing. The studio starts a new file with its next line.",
                    blocked: null),
                EvidenceClass.VerificationArtifacts => FilesReport(
                    evidenceClass,
                    "Verification artifacts",
                    VerificationFolder,
                    Files(VerificationFolder, "*", recursive: true),
                    "Kept until you delete them. Each run's folder is the evidence a work item's verification names.",
                    "The report, the consoles and the application's logs pass through the transcript redactor whole before they are written; screenshots are pictures and are kept as taken.",
                    "Removes every run's folder. The work items' evidence records stay, and their pages say the files are gone.",
                    blocked: null),
                _ => throw new ArgumentOutOfRangeException(nameof(evidenceClass)),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            _logger.LogWarning("An evidence class could not be measured: {Class} {Error}", evidenceClass, ex.GetType().Name);
            return new EvidenceClassReport(evidenceClass, evidenceClass.ToString(), "(unreadable)", 0, 0, string.Empty,
                string.Empty, string.Empty, "It could not be read: " + ex.Message);
        }
    }

    private string? RunningBlock() => _anySessionRunning() ? SessionRunningReason : null;

    private static EvidenceClassReport FilesReport(
        EvidenceClass evidenceClass,
        string name,
        string location,
        IReadOnlyList<FileInfo> files,
        string retention,
        string redaction,
        string effect,
        string? blocked) =>
        new(evidenceClass, name, location, files.Count, files.Sum(f => f.Length), retention, redaction, effect, blocked);

    private EvidenceClassReport TranscriptsReport()
    {
        using var connection = _connections.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT COUNT(*) FROM agent_sessions), " +
            "(SELECT COALESCE(SUM(LENGTH(payload)), 0) FROM agent_session_events);";
        using var reader = command.ExecuteReader();
        reader.Read();
        return new EvidenceClassReport(
            EvidenceClass.AgentTranscripts,
            "Agent transcripts",
            Path.Combine(Data, DataDirectory.DatabaseFileName) + " (agent_sessions, agent_session_events)",
            (int)reader.GetInt64(0),
            reader.GetInt64(1),
            "Kept until you delete a conversation or this class.",
            "Every event is redacted before it is stored and again before it is shown; one stored value is capped in size.",
            "Removes every conversation and its transcript from the database, then compacts the database so the rows are not left in free space.",
            RunningBlock());
    }

    /// <inheritdoc />
    public EvidenceDeletionResult Delete(EvidenceClass evidenceClass)
    {
        try
        {
            return evidenceClass switch
            {
                EvidenceClass.DiagnosticBundles => DeleteFiles(evidenceClass, Files(DiagnosticsFolder, "*.zip", recursive: false)),
                EvidenceClass.Backups => DeleteChildren(evidenceClass, BackupsFolder),
                EvidenceClass.AgentTranscripts => _anySessionRunning() ? Refused(evidenceClass) : DeleteTranscripts(),
                EvidenceClass.AgentCliConversations => _anySessionRunning()
                    ? Refused(evidenceClass)
                    : DeleteCliConversations(),
                EvidenceClass.SessionArtifacts => _anySessionRunning()
                    ? Refused(evidenceClass)
                    : DeleteChildren(evidenceClass, SessionsFolder),
                EvidenceClass.TrashArchives => DeleteFiles(evidenceClass, Files(TrashFolder, "*.zip", recursive: false)),
                EvidenceClass.Logs => DeleteFiles(evidenceClass, Files(LogsFolder, "*.log", recursive: false)),
                EvidenceClass.VerificationArtifacts => DeleteChildren(evidenceClass, VerificationFolder),
                _ => throw new ArgumentOutOfRangeException(nameof(evidenceClass)),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            _logger.LogWarning("An evidence class could not be deleted: {Class} {Error}", evidenceClass, ex.GetType().Name);
            return new EvidenceDeletionResult(evidenceClass, false, 0, 0, "The delete failed: " + ex.GetType().Name + ".");
        }
    }

    private static EvidenceDeletionResult Refused(EvidenceClass evidenceClass) =>
        new(evidenceClass, false, 0, 0, SessionRunningReason);

    private EvidenceDeletionResult DeleteFiles(EvidenceClass evidenceClass, IReadOnlyList<FileInfo> files)
    {
        var removed = 0;
        long bytes = 0;
        var left = 0;
        foreach (var file in files)
        {
            if (TryDelete(file, ref bytes))
            {
                removed++;
            }
            else
            {
                left++;
            }
        }

        return Outcome(evidenceClass, removed, bytes, left);
    }

    /// <summary>Deletes every file and folder directly inside <paramref name="folder"/>, keeping the folder.</summary>
    private EvidenceDeletionResult DeleteChildren(EvidenceClass evidenceClass, string folder)
    {
        if (!Directory.Exists(folder))
        {
            return Outcome(evidenceClass, 0, 0, 0);
        }

        var removed = 0;
        long bytes = 0;
        var left = 0;
        foreach (var file in Directory.EnumerateFiles(folder).Select(f => new FileInfo(f)).ToList())
        {
            if (TryDelete(file, ref bytes))
            {
                removed++;
            }
            else
            {
                left++;
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(folder).ToList())
        {
            var contents = Files(directory, "*", recursive: true);
            var size = contents.Sum(f => f.Length);
            try
            {
                Directory.Delete(directory, recursive: true);
                removed += contents.Count;
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Part of the folder may be gone; what is still there is counted as left.
                var remaining = Files(directory, "*", recursive: true);
                left += Math.Max(1, remaining.Count);
                removed += Math.Max(0, contents.Count - remaining.Count);
                bytes += Math.Max(0, size - remaining.Sum(f => f.Length));
            }
        }

        return Outcome(evidenceClass, removed, bytes, left);
    }

    private EvidenceDeletionResult DeleteTranscripts()
    {
        var forgotten = 0;
        foreach (var record in _sessions.Recent(int.MaxValue))
        {
            if (_forgetSession(record.Id))
            {
                forgotten++;
            }
        }

        long bytes;
        using (var connection = _connections.Open())
        {
            using (var size = connection.CreateCommand())
            {
                size.CommandText = "SELECT COALESCE(SUM(LENGTH(payload)), 0) FROM agent_session_events;";
                bytes = (long)size.ExecuteScalar()!;
            }

            // Events whose session row is already gone (an older delete, a crash between the two statements)
            // are evidence too.
            using var orphans = connection.CreateCommand();
            orphans.CommandText = "DELETE FROM agent_session_events;";
            orphans.ExecuteNonQuery();
        }

        var compacted = Compact();
        return new EvidenceDeletionResult(
            EvidenceClass.AgentTranscripts,
            compacted,
            forgotten,
            bytes,
            compacted
                ? Invariant($"Deleted {forgotten} conversation(s) and compacted the database.")
                : Invariant($"Deleted {forgotten} conversation(s), but the database could not be compacted yet, so the text may remain in its free space until the next delete."));
    }

    /// <summary>
    /// Checkpoints the write-ahead log into the database, VACUUMs it (which rewrites the file without the free
    /// pages the deleted rows were in), and truncates the log, so a deleted row is not recoverable from either
    /// file by reading it.
    /// </summary>
    private bool Compact()
    {
        try
        {
            using var connection = _connections.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning("The database could not be compacted after deleting transcripts: {Code}", ex.SqliteErrorCode);
            return false;
        }
    }

    private IReadOnlyList<FileInfo> CliConversationFiles()
    {
        if (_cliTranscripts.DefaultHome is not { } home)
        {
            return [];
        }

        var files = new List<FileInfo>(Files(Path.Combine(home, "projects"), "*", recursive: true));
        var history = new FileInfo(Path.Combine(home, "history.jsonl"));
        if (history.Exists)
        {
            files.Add(history);
        }

        return files;
    }

    private EvidenceDeletionResult DeleteCliConversations()
    {
        if (_cliTranscripts.DefaultHome is not { } home)
        {
            return Outcome(EvidenceClass.AgentCliConversations, 0, 0, 0);
        }

        var projects = DeleteChildren(EvidenceClass.AgentCliConversations, Path.Combine(home, "projects"));
        var removed = projects.ItemsRemoved;
        var bytes = projects.BytesRemoved;
        var left = projects.Completed ? 0 : 1;

        var history = new FileInfo(Path.Combine(home, "history.jsonl"));
        if (history.Exists)
        {
            if (TryDelete(history, ref bytes))
            {
                removed++;
            }
            else
            {
                left++;
            }
        }

        return Outcome(EvidenceClass.AgentCliConversations, removed, bytes, left);
    }

    private bool TryDelete(FileInfo file, ref long bytes)
    {
        try
        {
            var length = file.Exists ? file.Length : 0;
            file.Delete();
            bytes += length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("An evidence file could not be deleted: {Error}", ex.GetType().Name);
            return false;
        }
    }

    private static EvidenceDeletionResult Outcome(EvidenceClass evidenceClass, int removed, long bytes, int left) =>
        new(
            evidenceClass,
            left == 0,
            removed,
            bytes,
            left == 0
                ? Invariant($"Deleted {removed} item(s), {bytes} bytes.")
                : Invariant($"Deleted {removed} item(s), {bytes} bytes; {left} could not be deleted because they are in use. Try again later."));

    private static IReadOnlyList<FileInfo> Files(string folder, string pattern, bool recursive)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(folder, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Select(f => new FileInfo(f))];
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
