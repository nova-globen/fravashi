using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Mockups;

/// <summary>What became of a save.</summary>
public enum DesignSaveOutcome
{
    /// <summary>Nothing was written: the tree is the one that was opened (or last saved).</summary>
    Unchanged,

    /// <summary>A new version of the design was written.</summary>
    Saved,

    /// <summary>Somebody else saved the design since it was opened; nothing was written.</summary>
    Stale,

    /// <summary>The wiki refused the write for another reason; nothing was written.</summary>
    Refused,
}

/// <summary>A save's outcome, the design as it now stands when it was written, and the wiki's refusal otherwise.</summary>
public sealed record DesignSaveResult(DesignSaveOutcome Outcome, WikiItem? Item, IReadOnlyList<WikiError> Errors);

/// <summary>
/// Saving a design from the mockup editor (#196, #51's round-trip rule made concrete).
/// <para>
/// <b>A save with no edits writes nothing</b>, not even a new version, however the stored tree was formatted: what
/// is compared is the edit session's canonical tree against the one it opened. An agent's tree therefore survives
/// being opened and saved byte for byte, and only a real edit writes. When one does, the WHOLE block becomes
/// <see cref="MockupWriter"/>'s canonical form, so the first edit of an agent-written tree also reformats it once.
/// </para>
/// <para>
/// <b>Only the block's content is replaced.</b> Every character before its first line of JSON and after its last
/// (the fences included) is copied, so the design's prose is byte for byte what it was.
/// </para>
/// <para>
/// <b>The version opened is the version written against</b>, so a design somebody else saved in between is refused
/// as <see cref="DesignSaveOutcome.Stale"/> and never silently overwritten (the editor write path's posture, ADR 0016).
/// </para>
/// </summary>
public static class DesignMockupSave
{
    /// <summary>
    /// The body with <paramref name="block"/>'s content replaced by <paramref name="json"/> and nothing else
    /// changed. A block that was never closed is closed with a fence like the one that opens it.
    /// </summary>
    public static string WithTree(string body, MockupBlock block, string json)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(json);

        return WithContent(body, block.Start, block.Json.Length, block.Closed, json);
    }

    /// <summary>
    /// The body with the content of the fenced block opening at <paramref name="start"/> (whose current content is
    /// <paramref name="contentLength"/> characters) replaced by <paramref name="json"/>, and nothing else changed. Shared
    /// with the prototype's save (backlog 214), whose block is found the same CommonMark way.
    /// </summary>
    internal static string WithContent(string body, int start, int contentLength, bool closed, string json)
    {
        var newline = body.IndexOf('\n', start);
        if (newline < 0)
        {
            // The opening fence is the body's last line, with no line end: the content goes on a line of its own.
            return body + "\n" + Terminated(json) + (closed ? string.Empty : Fence(body, start) + "\n");
        }

        var contentStart = newline + 1;
        var contentEnd = contentStart + contentLength;
        var tail = closed ? body[contentEnd..] : Fence(body, start) + "\n";
        return body[..contentStart] + Terminated(json) + tail;
    }

    /// <summary>
    /// Saves the session's tree into the design it was opened from, against the version that was opened. Writes
    /// nothing when there is nothing to write. On <see cref="DesignSaveOutcome.Saved"/> the session's clean point
    /// moves to what was written.
    /// </summary>
    public static async Task<DesignSaveResult> SaveAsync(
        IWikiDocuments wiki, WikiItem opened, MockupEditSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wiki);
        ArgumentNullException.ThrowIfNull(opened);
        ArgumentNullException.ThrowIfNull(session);

        if (!session.IsDirty)
        {
            return new DesignSaveResult(DesignSaveOutcome.Unchanged, opened, []);
        }

        if (DesignMockup.Read(opened.Body).Block is not { } block)
        {
            return new DesignSaveResult(DesignSaveOutcome.Refused, null, [new WikiError(
                "no_mockup_block", "body", "This design no longer holds exactly one mockup block, so there is no place to save the tree.")]);
        }

        var body = WithTree(opened.Body, block, session.Json);
        if (string.Equals(body, opened.Body, StringComparison.Ordinal))
        {
            session.MarkSaved();
            return new DesignSaveResult(DesignSaveOutcome.Unchanged, opened, []);
        }

        var updated = await wiki.UpdateAsync(opened.Id, new WikiItemEdit { Body = body }, opened.Version, ct).ConfigureAwait(false);
        if (updated.IsValid)
        {
            session.MarkSaved();
            return new DesignSaveResult(DesignSaveOutcome.Saved, updated.Value, []);
        }

        var outcome = updated.Errors.Any(e => e.Code == WikiCodes.Conflict) ? DesignSaveOutcome.Stale : DesignSaveOutcome.Refused;
        return new DesignSaveResult(outcome, null, updated.Errors);
    }

    private static string Terminated(string json) => json.EndsWith('\n') ? json : json + "\n";

    /// <summary>The opening fence's run of backticks or tildes, which a closing fence repeats.</summary>
    private static string Fence(string body, int start)
    {
        var at = start;
        while (at < body.Length && body[at] == ' ')
        {
            at++;
        }

        var mark = body[at];
        var end = at;
        while (end < body.Length && body[end] == mark)
        {
            end++;
        }

        return body[at..end];
    }
}
