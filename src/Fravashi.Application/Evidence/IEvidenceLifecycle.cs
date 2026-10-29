namespace Fravashi.Application.Evidence;

/// <summary>
/// The kinds of evidence the studio keeps on disk today (backlog 75). Each has a stated retention, a stated
/// redaction and a delete that removes it from disk; <c>docs/evidence-lifecycle.md</c> is the written policy
/// and Settings &gt; Data &gt; Evidence retention is where a user sees and deletes each class.
/// </summary>
public enum EvidenceClass
{
    /// <summary>Diagnostic bundles written from Settings &gt; Advanced &gt; Diagnostics (backlog 74).</summary>
    DiagnosticBundles,

    /// <summary>Backup archives and the pre-restore folders <c>fravashi restore</c> leaves behind (backlog 79).</summary>
    Backups,

    /// <summary>Agent conversation transcripts in the studio's own database.</summary>
    AgentTranscripts,

    /// <summary>The agent CLI's own conversation files and prompt history under its config home (backlog 116).</summary>
    AgentCliConversations,

    /// <summary>Per-session artifacts under <c>&lt;data&gt;/agent-sessions</c>: hook files and projected config homes.</summary>
    SessionArtifacts,

    /// <summary>Archives of files deleted from the Code page explorer (<c>&lt;data&gt;/trash</c>).</summary>
    TrashArchives,

    /// <summary>The studio, launcher and window log files under <c>&lt;data&gt;/logs</c>.</summary>
    Logs,

    /// <summary>
    /// What a verification run kept under <c>&lt;data&gt;/verification</c> (backlog 224): the design check's report, the
    /// consoles, the application's logs and the paired screenshots.
    /// </summary>
    VerificationArtifacts,
}

/// <summary>What one evidence class currently holds, and the policy that governs it.</summary>
/// <param name="Class">Which class.</param>
/// <param name="Name">The user-facing name.</param>
/// <param name="Location">Where it lives, as a path or a database table description.</param>
/// <param name="ItemCount">How many items (files, archives, conversations) it holds now.</param>
/// <param name="SizeBytes">How much disk it takes now (for the database class, the stored payload size).</param>
/// <param name="Retention">The stated retention, in one sentence.</param>
/// <param name="Redaction">What redaction runs before it is stored, in one sentence.</param>
/// <param name="DeleteEffect">What deleting it removes and what stops working, in one sentence.</param>
/// <param name="DeleteBlockedReason">Why it cannot be deleted right now (a session is running), or null.</param>
public sealed record EvidenceClassReport(
    EvidenceClass Class,
    string Name,
    string Location,
    int ItemCount,
    long SizeBytes,
    string Retention,
    string Redaction,
    string DeleteEffect,
    string? DeleteBlockedReason);

/// <summary>What a delete did.</summary>
/// <param name="Class">Which class was deleted.</param>
/// <param name="Completed">True when everything the class held is gone.</param>
/// <param name="ItemsRemoved">How many items were removed.</param>
/// <param name="BytesRemoved">How many bytes were removed.</param>
/// <param name="Message">One sentence for the page: what happened, and what was left and why.</param>
public sealed record EvidenceDeletionResult(
    EvidenceClass Class,
    bool Completed,
    int ItemsRemoved,
    long BytesRemoved,
    string Message);

/// <summary>Lists each evidence class with its size and policy, and deletes a class from disk.</summary>
public interface IEvidenceLifecycle
{
    /// <summary>Every class, in a stable order, with what it holds now. Never throws.</summary>
    IReadOnlyList<EvidenceClassReport> Inventory();

    /// <summary>
    /// One class with what it holds now (work item #356): what the Evidence page measures class by class, off its first
    /// render, so one slow folder no longer holds the whole page. Never throws. The default reads the inventory.
    /// </summary>
    EvidenceClassReport ReportFor(EvidenceClass evidenceClass) =>
        Inventory().First(report => report.Class == evidenceClass);

    /// <summary>
    /// Deletes everything the class holds from disk. Refused (Completed false, nothing removed) while a
    /// running agent session could still be writing to it. Never throws.
    /// </summary>
    EvidenceDeletionResult Delete(EvidenceClass evidenceClass);
}
