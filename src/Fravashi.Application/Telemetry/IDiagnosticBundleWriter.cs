namespace Fravashi.Application.Telemetry;

/// <summary>What a written diagnostic bundle is, so the page can say where it went.</summary>
/// <param name="Path">The absolute path of the written zip.</param>
/// <param name="SizeBytes">Its size on disk.</param>
/// <param name="Entries">The entry names inside it, so the user can see what they are about to send.</param>
public sealed record DiagnosticBundle(string Path, long SizeBytes, IReadOnlyList<string> Entries);

/// <summary>
/// Writes the diagnosis a user can hand us (backlog 74): one zip under the data directory holding the
/// product version and variant, the runtime posture the launcher reported, the database's schema
/// version and per-table row counts, the recorded product events, and the most recent studio, launcher
/// and window logs.
/// <para>
/// The WHOLE text of every entry goes through the studio's existing transcript redaction first, in one
/// call rather than a line at a time, so a credential an agent or a tool happened to print into a log
/// does not ride along to whoever the user sends the file to. Redaction is pattern-based and so
/// best-effort, not a guarantee. Since backlog 119 a known provider key a pane wrapped is redacted whole
/// at any wrap width, on any line break (including U+000B, U+000C, U+0085, U+2028 and U+2029) and with
/// indented continuation lines. What it still cannot see is its own and documented on the redactor: a
/// credential shape it has no pattern for, a provider prefix that is itself split by the wrap, a wrap
/// with a blank line or trailing whitespace before the break, and a generic (non-provider) env value
/// wrapped onto a second line; this writer never widens or narrows those. The bundle is written and left
/// on disk, nothing uploads it, and the user decides whether to send it.
/// </para>
/// </summary>
public interface IDiagnosticBundleWriter
{
    /// <summary>Writes a bundle and returns where it landed.</summary>
    DiagnosticBundle Write();
}
