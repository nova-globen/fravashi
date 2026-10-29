using Fravashi.Application.Sessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// The one-line human subject of a tool call: what it is about to act on. Shared by the stream-json
/// parser (which reads it from the CLI's JSON) and the approval endpoint (which reads it from the
/// hook's payload), so a tool call reads the same in the transcript and in the approval prompt.
/// </summary>
public static class AgentToolSubject
{
    /// <summary>A subject longer than this is shortened: one call cannot flood the transcript.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// The honest, large bound on what a stored subject may carry (backlog 103). The store holds the
    /// RAW value and display shortening derives from it on read, so this cap exists only to keep one
    /// pathological call from writing megabytes into a transcript row.
    /// </summary>
    public const int MaxStoredLength = 65_536;

    /// <summary>
    /// The raw subject as the store carries it: verbatim, newlines and all, up to
    /// <see cref="MaxStoredLength"/>. A longer value keeps its head AND tail around a marker that names
    /// the storage cap, spelled differently from the display elision on purpose: a reader of the row
    /// must be able to tell "the store cut this" from "the renderer shortened this".
    /// </summary>
    public static string ForStorage(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Item 102 round two: redact BEFORE the cap. The cap cuts the middle out of an over-long
        // value, and a secret straddling the head cut used to be split around the storage marker so
        // its head survived the later redaction pass. Redacting the raw value first sees the whole
        // secret; the cut then runs over text that is already safe. Serialize redacts again on the
        // way to the row, harmlessly: the redactor is idempotent.
        value = TranscriptRedactor.Redact(value);

        if (value.Length <= MaxStoredLength)
        {
            return value;
        }

        // Room for the marker itself, so the stored row still fits the cap whatever the count says.
        var keep = MaxStoredLength - 64;
        var head = keep / 2;
        var tail = keep - head;
        return value[..head] + $" ...[storage cap: {value.Length - keep} chars omitted]... " + value[^tail..];
    }

    /// <summary>A single argument longer than this is capped before the UI renders it.</summary>
    public const int MaxInputValueLength = 10_000;

    /// <summary>How much of a resolved path the approval prompt shows before shortening it.</summary>
    public const int MaxTargetLength = 512;

    /// <summary>
    /// Input fields that name what a tool acts on, best first. "pattern" outranks "path" because a
    /// search's subject is what it looks for; the path is only where it looks. "message" is last: it is what
    /// a SendMessage call says to another agent (backlog 192), and a subject chosen by input order instead
    /// was whichever argument the CLI happened to send first.
    /// </summary>
    public static readonly string[] Fields =
        ["file_path", "notebook_path", "command", "pattern", "url", "path", "description", "prompt", "query", "message"];

    /// <summary>
    /// The best subject in a flattened tool input, or empty when nothing reads as one. RAW, not
    /// shortened (backlog 103): this value is what the transcript persists, so it has to be the
    /// command the CLI was really handed, bounded only by <see cref="ForStorage"/>'s honest cap.
    /// Display copies derive from it at render time via <see cref="Shorten"/>, never the reverse.
    /// </summary>
    public static string From(IReadOnlyDictionary<string, string> input)
    {
        ArgumentNullException.ThrowIfNull(input);

        foreach (var field in Fields)
        {
            if (input.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return ForStorage(value);
            }
        }

        foreach (var (_, value) in input)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return ForStorage(value);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The longest argument the approval card carries in full (backlog 240, plan @53): about 5,000 lines, far
    /// past what a person reads on a card, and four times the transcript's own storage cap. A longer one is
    /// shown by its head and tail with a loud warning, never claimed as shown.
    /// </summary>
    public const int MaxFullValueLength = 262_144;

    /// <summary>
    /// A copy of a tool input safe to hand to the UI: every value over <see cref="MaxInputValueLength"/>
    /// keeps its first and last halves of that budget around a line naming how many characters are not
    /// shown, so one call cannot flood the approval prompt and the cut cannot hide where the text ends
    /// (backlog 240: a bare ellipsis at 10,000 hid everything after it). The whole value reaches the card
    /// separately, from the raw input (<see cref="CutArguments"/>).
    /// <para>
    /// This is the LAST thing done to a tool input, never the first. Capping produces a value that is
    /// not the one the CLI will run, and a shortened path normalises differently from the real one:
    /// "/workspaces/w1/" followed by thousands of "./" and then an escape resolves to the key ring,
    /// while the same value cut at the cap resolves to the copy itself. So containment
    /// (<see cref="AgentReadScope"/>) is always decided on the RAW input, and the result of this method
    /// must never reach a security decision.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> ForDisplay(IReadOnlyDictionary<string, string> input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var capped = new Dictionary<string, string>(input.Count, StringComparer.Ordinal);
        foreach (var (key, value) in input)
        {
            capped[key] = value is { Length: > MaxInputValueLength } ? HeadAndTail(value) : value;
        }

        return capped;
    }

    /// <summary>
    /// The arguments <see cref="ForDisplay"/> cuts, each with its RAW value (backlog 240), so the card can show
    /// the whole of what is approved. Built from the same raw input every decision was made on, and read by
    /// nothing but the UI: it is not a second copy anything checks.
    /// </summary>
    public static IReadOnlyList<CutToolArgument> CutArguments(IReadOnlyDictionary<string, string> input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var cut = new List<CutToolArgument>();
        foreach (var (key, value) in input)
        {
            if (value is { Length: > MaxInputValueLength })
            {
                var (head, tail) = Kept(value);
                cut.Add(new CutToolArgument(
                    key, value.Length, value.Length - head - tail, value.Length <= MaxFullValueLength ? value : null));
            }
        }

        return cut;
    }

    /// <summary>
    /// The first and last halves of the display budget, joined by a line of its own naming what is missing.
    /// The marker is on its own line so it cannot read as part of the agent's text.
    /// </summary>
    private static string HeadAndTail(string value)
    {
        var (head, tail) = Kept(value);
        return value[..head]
            + "\n\n[... " + NotShown(value.Length - head - tail) + " here; open \"Show the full value\" below ...]\n\n"
            + value[^tail..];
    }

    /// <summary>How many characters of an over-long value the display copy keeps at each end.</summary>
    private static (int Head, int Tail) Kept(string value)
    {
        var head = MaxInputValueLength / 2;
        var tail = MaxInputValueLength - head;

        // Never cut between the two halves of a surrogate pair, which would show a broken character.
        if (char.IsHighSurrogate(value[head - 1]))
        {
            head--;
        }

        if (char.IsLowSurrogate(value[^tail]))
        {
            tail--;
        }

        return (head, tail);
    }

    /// <summary>"5,000 characters not shown", the words the card uses wherever a cut is named.</summary>
    public static string NotShown(int count) =>
        count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
        + (count == 1 ? " character not shown" : " characters not shown");

    /// <summary>
    /// Collapses a value to one line and shortens it WITHOUT changing what it says it points at: the
    /// head and the TAIL both survive, and the cut says how much went missing.
    /// <para>
    /// Cutting the end off a path produces a different path that reads as an ordinary one. A
    /// <c>file_path</c> of 500 characters ("&lt;copy&gt;/src" then padding then a "../" chain then
    /// "/data/keys") lands on the key ring, while the same value cut at 200 reads as a deep folder
    /// safely inside the copy. That shortened form was what the approval prompt showed, so a user
    /// approving what they read approved something else entirely. Keeping the tail is what makes the
    /// shortened form unable to disguise the destination; it is still never the ONLY thing shown, the
    /// resolved target is shown beside it (<see cref="Domain.AgentSessions.AgentToolTarget"/>).
    /// </para>
    /// </summary>
    public static string Shorten(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var single = value.ReplaceLineEndings(" ").Trim();
        if (single.Length <= max)
        {
            return single;
        }

        // Room for the marker itself, so the result still fits the caller's budget.
        var keep = max - 24;
        if (keep < 16)
        {
            return Trim(single, max);
        }

        var head = keep / 2;
        var tail = keep - head;
        return single[..head] + $" ...[{single.Length - keep} more]... " + single[^tail..];
    }

    /// <summary>
    /// Caps a value's length from the END, appending an ellipsis when cut, WITHOUT collapsing its
    /// newlines. For tool OUTPUT, whose line structure is the content (a diff, a stack trace, a
    /// multi-line file listing) and which is rendered in a preformatted block, not on one line
    /// (backlog 104 D3). Unlike <see cref="Trim"/>, which flattens every line ending to a space for a
    /// one-line subject, this keeps the shape the tool produced; the only thing it removes is length.
    /// </summary>
    public static string Cap(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..(max - 3)] + "...";
    }

    /// <summary>
    /// Collapses a value to one line and caps its length from the END, appending an ellipsis when cut.
    /// For text whose tail carries nothing (a one-line log line, a subject). Never for a path: use
    /// <see cref="Shorten"/>, which cannot turn one path into a different, plausible one; and never for
    /// tool OUTPUT, whose newlines are meaningful: use <see cref="Cap"/>.
    /// </summary>
    public static string Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var single = value.ReplaceLineEndings(" ").Trim();
        return single.Length <= max ? single : single[..(max - 3)] + "...";
    }
}
