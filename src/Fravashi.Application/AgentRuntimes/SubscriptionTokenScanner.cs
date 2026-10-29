using System.Text.RegularExpressions;

namespace Fravashi.Application.AgentRuntimes;

/// <summary>
/// Reads one sign-in's output line by line, keeps the long-lived subscription token it prints (backlog 183), and hides
/// every sign of a token from what the panel shows whether or not it could be read (backlog 268).
/// <para>
/// On 2026-10-08 the pinned CLI's token reached the panel shown and nothing was kept. Why is not measured: the real output
/// holds a real token. So this holds to two rules that do not depend on knowing. Hiding is decided by SIGNS of a token
/// (<c>sk-ant</c>, <c>ant-oat</c>, <c>oat</c> two digits and a hyphen, a line ending in the start of the prefix, or a word
/// anywhere in the line of at least <see cref="BodyFloor"/> token characters with a letter or digit, bounded by whitespace,
/// a cursor move, a box border or the line's edge, which is what a head lost past <c>oat</c> leaves), never by a whole
/// match, and from the first sign the rest of the line is hidden. A following line made only of token
/// characters is hidden too: while the token is not yet whole it is joined to it (a wrap inside its prefix); after a whole
/// token it is hidden only when it carries a letter or digit, and then the token is ambiguous (the line could be its rest
/// or a word like "Done"); a line without one, like a row of hyphens, ends the token and is shown. Keeping is decided by a
/// WHOLE match, and only when the terminal printed it in a row: a cursor move between two of its characters means some may
/// be missing from the stream, so that token is hidden and not kept, and neither is an ambiguous one.
/// </para>
/// </summary>
public sealed partial class SubscriptionTokenScanner
{
    private readonly HashSet<string> _whole = new(StringComparer.Ordinal);
    private string? _run;
    private bool _moved;

    /// <summary>
    /// A token-shaped value appeared that could not be read whole: a cursor move inside it, no head, or a line of token
    /// characters after it that could be its rest.
    /// </summary>
    public bool SawUnreadable { get; private set; }

    /// <summary>
    /// The fewest token characters a word (bounded by whitespace, a cursor move, a box border or the line's edge, anywhere
    /// in the line, with a letter or digit) must hold to be a sign of a token's body when no other sign is there. The real
    /// body's length is not measured; the test's fake has 48, and a device code like <c>WDJB-MJHT</c> has 9.
    /// </summary>
    public const int BodyFloor = 24;

    /// <summary>More than one different whole token appeared, so there is no telling which one is the sign-in's.</summary>
    public bool SawSeveral => _whole.Count > 1;

    /// <summary>The one whole token the sign-in printed, or null; settled by <see cref="Finish"/>.</summary>
    public string? Token => _whole.Count == 1 ? _whole.First() : null;

    /// <summary>What the panel may show of this raw line: control sequences stripped, and from any sign of a token on, hidden.</summary>
    public string Show(string rawLine)
    {
        var marked = AgentLoginTextParser.StripControlMarkingMoves(rawLine ?? string.Empty);

        if (_run is not null)
        {
            // The last line ended in token characters, and this one is nothing but token characters (between spaces or a
            // box's borders).
            var content = Content(marked);
            if (content.Length > 0 && content.All(c => IsTokenCharacter(c) || IsMove(c)) && !content.Contains("sk-ant", StringComparison.Ordinal))
            {
                if (!WholeTokenRegex().IsMatch(_run))
                {
                    // Not a token yet (the wrap fell inside the prefix): this line is the rest of it.
                    _moved |= content.Any(IsMove);
                    _run += new string(content.Where(IsTokenCharacter).ToArray());
                    return AgentLoginTextParser.RedactedToken;
                }

                if (content.Any(char.IsAsciiLetterOrDigit))
                {
                    // Already a whole token: this line is either the rest of it, wrapped, or a word like "Done". Joining
                    // would store a wrong token in the second case and not joining a cut one in the first, and with no
                    // measured length there is no telling, so it is hidden and nothing is kept.
                    _moved = true;
                    return AgentLoginTextParser.RedactedToken;
                }
            }

            Settle();
        }

        var sign = SignRegex().Match(marked);
        if (!sign.Success)
        {
            return AgentLoginTextParser.VisibleText(marked);
        }

        var at = sign.Index;
        var run = new System.Text.StringBuilder();
        var moved = false;
        while (at < marked.Length)
        {
            if (IsTokenCharacter(marked[at]))
            {
                run.Append(marked[at++]);
                continue;
            }

            var after = at;
            while (after < marked.Length && IsMove(marked[after]))
            {
                after++;
            }

            if (after > at && after < marked.Length && IsTokenCharacter(marked[after]))
            {
                moved = true;
                at = after;
                continue;
            }

            break;
        }

        _run = run.ToString();
        _moved = moved;
        if (Content(marked[at..]).Length > 0)
        {
            Settle();
        }

        var before = marked[..sign.Index].Replace(AgentLoginTextParser.ForwardMove, ' ')
            .Replace(AgentLoginTextParser.OtherMove.ToString(), string.Empty, StringComparison.Ordinal);
        return before + AgentLoginTextParser.RedactedToken;
    }

    /// <summary>Settles a token still running at the end of the output.</summary>
    public void Finish() => Settle();

    private void Settle()
    {
        if (_run is null)
        {
            return;
        }

        if (!_moved && WholeTokenRegex().IsMatch(_run))
        {
            _whole.Add(_run);
        }
        else
        {
            SawUnreadable = true;
        }

        _run = null;
        _moved = false;
    }

    /// <summary>The line without spaces, cursor moves or a box's borders at either end.</summary>
    private static string Content(string marked)
    {
        var start = 0;
        var end = marked.Length;
        while (start < end && IsEdge(marked[start]))
        {
            start++;
        }

        while (end > start && IsEdge(marked[end - 1]))
        {
            end--;
        }

        return marked[start..end];
    }

    // Box drawing is U+2500 to U+257F.
    private static bool IsEdge(char c) => char.IsWhiteSpace(c) || IsMove(c) || c is >= (char)0x2500 and <= (char)0x257F;

    private static bool IsMove(char c) => c is AgentLoginTextParser.ForwardMove or AgentLoginTextParser.OtherMove;

    private static bool IsTokenCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';

    // Claude Code's long-lived OAuth token, as the vendor documents it: sk-ant-oat01-... .
    [GeneratedRegex(@"^sk-ant-oat\d{2}-[A-Za-z0-9_\-]+$")]
    private static partial Regex WholeTokenRegex();

    // A sign of a token: its prefix; a piece of the prefix a lost head left behind; a line that ENDS in the start of the
    // prefix (from "sk" on, as a word of its own), which a wrap inside the prefix leaves; or a WORD of at least BodyFloor
    // token characters with a letter or digit, which a head lost past "oat" leaves. A word is bounded by whitespace, a
    // cursor move, a box border or the line's edge, so a long piece of the sign-in link (inside a word that holds ":" and
    // "/") is never one, and the link stays clickable.
    [GeneratedRegex(@"sk-ant|ant-oat|oat\d{2}-|(?<![A-Za-z0-9_\-])sk(?:-(?:an?)?)?(?=[\s\p{Co}\p{IsBoxDrawing}]*$)"
        + @"|(?<![^\s\p{Co}\p{IsBoxDrawing}])(?=[_\-]*[A-Za-z0-9])[A-Za-z0-9_\-]{24,}(?![^\s\p{Co}\p{IsBoxDrawing}])")]
    private static partial Regex SignRegex();
}
