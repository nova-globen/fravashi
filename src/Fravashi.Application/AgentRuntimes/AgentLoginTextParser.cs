using System.Text.RegularExpressions;

namespace Fravashi.Application.AgentRuntimes;

/// <summary>
/// Turns a raw line of a CLI's interactive-login output into an <see cref="AgentLoginChunk"/>, extracting the
/// verification URL and device/user code the UI highlights. Pure and deterministic, so the highlight logic is
/// unit-tested directly. Terminal control sequences (colour/cursor codes a PTY emits) are stripped first.
/// </summary>
public static partial class AgentLoginTextParser
{
    public static AgentLoginChunk ToChunk(DateTimeOffset at, string rawLine, bool isError)
    {
        var clean = StripControl(rawLine ?? string.Empty);
        var url = ExtractUrl(clean);
        var code = ExtractDeviceCode(clean);
        var kind = isError ? AgentLoginChunkKind.Error : AgentLoginChunkKind.Output;
        return new AgentLoginChunk(at, clean, kind, url, code);
    }

    /// <summary>The first http(s) URL in the line, with trailing punctuation trimmed; null if none.</summary>
    public static string? ExtractUrl(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var match = UrlRegex().Match(line);
        if (!match.Success)
        {
            return null;
        }

        return match.Value.TrimEnd('.', ',', ')', ']', '}', '>', '"', '\'', ';', ':');
    }

    /// <summary>
    /// A device/user code in the line (e.g. GitHub's <c>WDJB-MJHT</c>), null if none. Matches an
    /// upper-alphanumeric group, optionally hyphenated, that is not part of a URL.
    /// </summary>
    public static string? ExtractDeviceCode(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        // Do not mistake a fragment of the verification URL for a code.
        var withoutUrl = UrlRegex().Replace(line, " ");
        var match = DeviceCodeRegex().Match(withoutUrl);
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// What the panel shows from the first sign of a token on a line (backlog 183, 268). Whether the token was kept is said
    /// once, in the notice that ends the sign-in, because a hidden value is not always a kept one.
    /// </summary>
    public const string RedactedToken = "[a token was printed here; it is never shown]";

    /// <summary>Removes ANSI/VT control sequences and stray carriage returns a PTY emits.</summary>
    public static string StripControl(string text) => VisibleText(StripControlMarkingMoves(text));

    /// <summary>
    /// Like <see cref="StripControl"/>, but every cursor move is left in the text as a marker rather than turned into what
    /// it looks like, and nothing is trimmed: a forward move by n columns becomes n <see cref="ForwardMove"/>, any other
    /// move one <see cref="OtherMove"/>. A move between two characters of a token means the terminal did not print them
    /// in a row, so characters may be missing from the stream (backlog 268); <see cref="SubscriptionTokenScanner"/> needs
    /// to see that, where the panel only needs the spaces.
    /// </summary>
    public static string StripControlMarkingMoves(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // The markers are private-use characters; one that arrived in the output itself is dropped so it cannot pose as one.
        var own = text.Replace(ForwardMove.ToString(), string.Empty, StringComparison.Ordinal)
            .Replace(OtherMove.ToString(), string.Empty, StringComparison.Ordinal);

        // OSC first: its payload is arbitrary text, so it must go before anything reads the line as content.
        var withoutOsc = OscRegex().Replace(own, string.Empty);

        // A TUI draws the gap between words by moving the cursor forward, so that is a space on the panel; without this
        // the panel read "WelcometoClaudeCode" (seen 2026-10-02).
        var forward = CursorForwardRegex().Replace(withoutOsc, m =>
            new string(ForwardMove, int.TryParse(m.Groups[1].Value, out var n) && n > 0 ? Math.Min(n, 200) : 1));
        var moved = OtherMoveRegex().Replace(forward, OtherMove.ToString());
        var stripped = AnsiRegex().Replace(EscapeRegex().Replace(moved, string.Empty), string.Empty).Replace("\r", string.Empty);

        // A control character left over (a tab, a backspace, DEL, an ESC nothing above matched) moves the cursor or is
        // not printed, so it is a move too: a tab forward, which the panel shows as a space, anything else the other kind.
        // Left in place it ended a token's run at a cut head that still looked whole (backlog 268 review).
        return string.Create(stripped.Length, stripped, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c == '\t' ? ForwardMove : char.IsControl(c) ? OtherMove : c;
            }
        });
    }

    /// <summary>The text of <see cref="StripControlMarkingMoves"/> as the panel shows it: a forward move is a space, any other nothing.</summary>
    public static string VisibleText(string marked) =>
        marked.Replace(ForwardMove, ' ').Replace(OtherMove.ToString(), string.Empty, StringComparison.Ordinal).TrimEnd();

    /// <summary>One column of a cursor-forward, in <see cref="StripControlMarkingMoves"/>.</summary>
    public const char ForwardMove = (char)0xE000;

    /// <summary>Any other cursor move, in <see cref="StripControlMarkingMoves"/>.</summary>
    public const char OtherMove = (char)0xE001;

    [GeneratedRegex(@"https?://[^\s'""]+")]
    private static partial Regex UrlRegex();

    // Two 3-8 char upper-alphanumeric groups joined by a hyphen (device codes), or a single 6-10 char group.
    [GeneratedRegex(@"\b[A-Z0-9]{3,8}-[A-Z0-9]{3,8}\b")]
    private static partial Regex DeviceCodeRegex();

    /// <summary>
    /// True for a line that is only a progress spinner's frame: a TUI redraws one glyph many times while it waits, which
    /// says nothing a person needs and pushed the sign-in's link out of the panel (seen 2026-10-02).
    /// </summary>
    public static bool IsSpinnerFrame(string cleanLine)
    {
        var text = (cleanLine ?? string.Empty).Trim();
        return text.Length is > 0 and <= 3 && text.All(c => SpinnerGlyphs.Contains(c, StringComparison.Ordinal) || char.IsWhiteSpace(c));
    }

    // The frames of the Claude CLI's spinner: middle dot, four-teardrop asterisk, eight-spoked asterisk, six-pointed
    // black star, teardrop-spoked asterisk, heavy teardrop-spoked asterisk, and the plain asterisk and plus.
    private static readonly string SpinnerGlyphs = new([(char)0x00B7, (char)0x2722, (char)0x2733, (char)0x2736, (char)0x273B, (char)0x273D, '*', '+']);

    // CSI with every parameter byte ECMA-48 allows (0x30 to 0x3F): private-mode ones (ESC [ > 0 q showed in the panel as
    // "[>0q"), and the colon in a colour like ESC [ 38:5:208 m, which this used to leave in the line, so a token coloured
    // that way stopped at the escape and its rest was shown (backlog 268, a candidate cause, not measured).
    [GeneratedRegex(@"\x1B\[[\x30-\x3F]*[ -/]*[@-~]")]
    private static partial Regex AnsiRegex();

    // Cursor forward by n columns (ESC [ n C).
    [GeneratedRegex(@"\x1B\[(\d*)C")]
    private static partial Regex CursorForwardRegex();

    // Every other cursor move ECMA-48 and the xterm set define, by CSI final byte: up, down, back, next and previous line,
    // to a column (G and the backtick), to a row (d), to a position (H, f), relative right, down, left and up (a, e, j,
    // k), forward and back tab (I, Z) and restore (u); and the escapes that move it (ESC 8 restore, ESC D index, ESC E next
    // line, ESC M reverse index). Anything else is stripped as not moving the cursor.
    [GeneratedRegex(@"\x1B\[[0-9;]*[ABDEFGHIZ`adefjku]|\x1B[8DEM]")]
    private static partial Regex OtherMoveRegex();

    // Two-character escapes: save and restore the cursor (ESC 7, ESC 8), keypad modes (ESC =, ESC >) and a
    // character-set choice (ESC ( B and the like), which showed in the panel as a stray "7", "8" and "(B".
    [GeneratedRegex(@"\x1B(?:[()*+][0-9A-Za-z]|[78=>DEHMNOZc])")]
    private static partial Regex EscapeRegex();

    // An operating system command: ESC ] payload, ended by BEL or by ESC \. Stripping these is not theoretical:
    // a vendor CLI's device-code sign-in was measured sending OSC 52 (put the code on the clipboard, in base64),
    // and without this the first line the user saw was that escape rendered as garbage.
    [GeneratedRegex(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)")]
    private static partial Regex OscRegex();
}
