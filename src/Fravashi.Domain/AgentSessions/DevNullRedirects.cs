using System.Text.RegularExpressions;

namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// ONE rule for what counts as "a redirection to /dev/null", read by two callers that must never disagree about
/// it: <see cref="AgentReadRedirect"/>, which does not let a redirection of this exact shape disqualify a Bash
/// command from being recognised as a pure read (backlog 337), and the autopilot judge's own command rewrite in
/// <c>AgentApprovalBroker</c>, which strips this same shape from the command text before judgement so a read
/// that merely discards its output is not flagged as a write outside the copy (backlog 326, moved here
/// unchanged on backlog 337 so the two could share it). Before this type existed the two disagreed: the judge's
/// rewrite already forgave a trailing <c>2&gt;/dev/null</c>, while <c>AgentReadRedirect</c> disqualified any
/// command containing <c>&gt;</c> at all, which is exactly the gap #337 found (a read ending in
/// <c>2&gt;/dev/null</c> reached a person instead of Read, Grep or Glob).
/// <para>
/// Backlog 326's original reasoning for the shape: the judge cleared <c>mkdir -p ./scripts ./docs</c> but
/// refused the otherwise identical read <c>cat ./README.md 2&gt;/dev/null</c> as <c>writes_outside_copy</c>: the
/// redirection names a path outside the copy in the letter and discards output in fact, and agents append
/// <c>2&gt;/dev/null</c> to reads constantly, so it is removed from what either caller reads as the command
/// rather than left to read as a write.
/// </para>
/// <para>
/// Matches a plain <c>&gt;</c>/<c>&gt;&gt;</c>, a leading file-descriptor digit run
/// (<c>1&gt;</c>, <c>2&gt;</c>, <c>10&gt;</c>, <c>2&gt;&gt;</c>, ...), or
/// <c>&amp;&gt;</c>/<c>&amp;&gt;&gt;</c>, with optional spaces or tabs (never a newline, so a
/// redirection starting its own line never fuses onto the previous command) before the path, which
/// may be bare or wrapped in one matching quote character (a quoted target does the same thing a
/// bare one does, so it counts the same way; see plan @144). The
/// leading digit run counts only when it starts its own shell word, so <c>foo2&gt;/dev/null</c>
/// strips only the plain <c>&gt;</c> and leaves <c>foo2</c> as the word it is, which is what the
/// shell itself would do with it; a REAL multi-digit descriptor glued to nothing else, such as
/// <c>10&gt;/dev/null</c>, is removed whole, digits included.
/// </para>
/// <para>
/// This boundary set is DELIBERATELY NARROWER than the autopilot judge's own root-to-dot rewrite's boundary set
/// (<c>AgentApprovalBroker.RewriteCopyRootToDot</c>'s <c>IsBoundaryBefore</c>): a quote or <c>=</c> ends a shell
/// word when a PATH stands alone there, but not when a digit does, because a digit can be the tail of an
/// assignment's value (<c>N=2&gt;/dev/null</c> is the word <c>N=2</c>, not an assignment followed by descriptor
/// <c>2</c>) or of a word a quoted expansion merely starts (<c>"$x"2&gt;/dev/null</c> is the one
/// word <c>"$x"2</c>). A literal <c>&amp;</c> counts only when it is not itself the second character
/// of <c>&gt;&amp;</c> or <c>&lt;&amp;</c> (a duplication operator), so the <c>1</c> in
/// <c>2&gt;&amp;1&gt;/dev/null</c> is read as what it is, the duplication target of
/// <c>2&gt;&amp;1</c>, never as a descriptor starting a second redirection; the match that removes
/// <c>&gt;/dev/null</c> there starts at that final <c>&gt;</c>, not at the <c>1</c>, so
/// <c>2&gt;&amp;1</c> itself is left exactly as written, same as everywhere else it appears.
/// </para>
/// <para>
/// The target must end exactly at <c>/dev/null</c> (or its closing quote): followed by whitespace, a
/// shell metacharacter, or the end, but never a quote or a backtick, because either of those
/// CONTINUES the same shell word rather than ending it (<c>&gt;/dev/null'x'</c> writes to a file
/// literally named <c>/dev/nullx</c>, the bare and quoted parts glued into one word by the shell)
/// rather than naming <c>/dev/null</c> alone. This is also what leaves <c>/dev/nullx</c> and
/// <c>/dev/null/x</c> untouched. Because the literal text <c>/dev/null</c> must follow for any of
/// this to match, every other redirection, <c>2&gt;&amp;1</c> included, is left exactly as written;
/// the noclobber-override <c>&gt;|</c> is one of those, because its <c>|</c> stops the mandatory
/// <c>/dev/null</c> literal from ever following. The read-write <c>&lt;&gt;</c> and a brace-expanded
/// or shell-variable file descriptor that itself ends in a bare <c>&gt;</c> right before
/// <c>/dev/null</c> (<c>cmd &lt;&gt;/dev/null</c>, <c>cmd {fd}&gt;/dev/null</c>) are NOT left as
/// written: the plain <c>&gt;</c> alternative above has no lookbehind, so they lose their
/// <c>&gt;/dev/null</c> suffix the same as any other plain <c>&gt;</c> redirect would, leaving a
/// dangling <c>&lt;</c> or <c>{fd}</c> in what either caller is shown. Accepted rather than
/// special-cased: neither the item nor the evidence ADR 0031 decision 8's second addendum answers to
/// names either shape.
/// </para>
/// </summary>
public static class DevNullRedirects
{
    private static readonly Regex Pattern = new(
        @"[ \t]*(?:(?<=^|[\s;|()`]|(?:^|[^<>])&)[0-9]+>>?|>>?|&>>?)[ \t]*(['""]?)/dev/null\1(?=$|[\s;&|()<>])",
        RegexOptions.Compiled);

    /// <summary>
    /// <paramref name="command"/> with every redirection to exactly /dev/null removed, and everything else about
    /// the command left exactly as written, with the two exceptions the class doc above names: the read-write
    /// <c>&lt;&gt;</c> and a brace-expanded or shell-variable file descriptor ending in a bare <c>&gt;</c> right
    /// before <c>/dev/null</c> lose their <c>&gt;/dev/null</c> suffix too, leaving a dangling prefix.
    /// </summary>
    public static string Strip(string command) =>
        string.IsNullOrEmpty(command) ? command : Pattern.Replace(command, string.Empty);
}
