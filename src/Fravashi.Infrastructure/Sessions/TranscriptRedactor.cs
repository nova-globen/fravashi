using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// Removes high-confidence secret shapes from transcript text (backlog 102): the studio must never
/// keep or show a credential an agent happened to print, and the incident that proved it was a real
/// ANTHROPIC_API_KEY persisted verbatim from an env dump whose own sed redaction had failed.
/// <para>
/// ONE redactor serves every path on purpose: the persistence write
/// (<see cref="AgentEventJson.Serialize"/> and <see cref="AgentToolSubject.ForStorage"/>, which
/// redacts BEFORE its cap so a secret straddling the cut cannot be split around the storage marker),
/// the one-time startup scrub over rows written before the rule existed
/// (<see cref="Persistence.SqliteSchema"/>, schema v7), and the leaf display components
/// (ToolCallCard, ChatBubble), so a raw row the scrub has never seen is still never rendered.
/// </para>
/// <para>
/// The policy is two-tier, rebalanced after an adversarial review broke the first version (item 102
/// round two). PROVIDER-PREFIX patterns are primary: a published prefix (sk-ant-, the ghp_ family,
/// Slack's xox*/xapp-, glpat-, AKIA) followed by its known charset within sane length bounds is high
/// confidence, so it matches ANYWHERE in text and is never guarded. The ENV-NAME rule is secondary:
/// an assignment whose UPPERCASE name says KEY, TOKEN, SECRET or PASSWORD, decorated the ways real
/// transcripts decorate it (leading whitespace, export, declare -x, a diff's +/-, quotes, a trailing
/// comment, a JSON string) but GUARDED against values that are provably not secrets (passthroughs
/// starting with $, placeholder words, path values with /, the name itself, anything under 8 chars),
/// because a name is only a hint. The value is replaced, the name is kept, so the transcript still
/// says what was printed, just not the material.
/// </para>
/// <para>
/// Round three reshaped the provider patterns' wrap tolerance into JOINED RUNS: a match is one
/// charset run optionally joined to further runs by a single line-break joiner each, greedy, never
/// starting or ending on a joiner. Round two tolerated exactly one \r?\n confined to the first 15
/// chars, so an 80-column wrap (break after 16+ chars), a double wrap, a lone-\r capture and a
/// U+2028 break all leaked the tail of a real key. The join brings a DOCUMENTED over-redaction
/// with it: a key ending exactly at a line end joins into the next line's first word (see the
/// acceptance test); the damage is bounded by the join cap and the per-run length cap, and the
/// swallow stops at the first non-charset character.
/// </para>
/// <para>
/// The FINAL round widened the joiner set and the cap. The joiners are now the raw line breaks
/// (\r\n, \n, a lone \r, U+2028) AND their ESCAPED two-character text forms (a literal backslash
/// then n, backslash then r, and the four-character backslash-r-backslash-n, each consumed as ONE
/// join): a key wrapped inside double-encoded JSON carries its break as text, not as a control
/// character, and its tail leaked. No provider charset contains a backslash, so an escaped joiner
/// can never eat material. The join cap rose from three to six, sized for realistic narrow panes
/// (round three's K family leaked contiguous material past three joins); a 10-column wrap of a
/// 106-char key needs ten joins and its tail past the sixth is the documented accepted bound,
/// pinned by a test. Material counting still excludes joiner characters, escaped forms included.
/// </para>
/// <para>
/// Backlog 119 lifted the three shapes the final round still missed, each measured through the
/// diagnostic bundle writer. (1) The raw joiner set grew to every line break a pane or a log can carry:
/// U+000B, U+000C, U+0085 and U+2029 join like \n and U+2028 do, so a wrap on one of them no longer
/// orphans the tail after a head that cleared the floor. (2) A joiner may be FOLLOWED by indentation
/// (spaces, tabs, or the escaped two-character \t text form), because a logger or a pane that indents
/// its continuation lines left the whole key in the clear when the head was short. Indentation alone,
/// with no break in front of it, still ends a match, so a key followed by a space and a word never
/// swallows that word. (3) The fixed six-join cap is gone: the join count is now bounded by the
/// pattern's own per-run length cap (256 for sk-ant-, 16 for AKIA), which is the longest material
/// any key of that shape can have, so a key wrapped at any width down to one column redacts whole. What
/// bounds the swallow instead is a MATERIAL CEILING in the replacer: once a match's material reaches
/// that same cap, the marker ends at the end of the run that reached it and everything after is
/// returned to the text untouched (see <see cref="ReplaceToken"/>). That keeps the old
/// never-decline property (nothing a real key could be is returned), while a run of charset-only
/// lines after a key is swallowed only up to the ceiling instead of without limit.
/// </para>
/// <para>
/// The widening's measured cost (the corpus tests pin each one): a key ending at a line end now also
/// swallows the first word of an INDENTED next line when that word is charset (so
/// <c>key: sk-ant-...</c> then <c>  model: x</c> loses <c>  model</c> and keeps <c>: x</c>); lines
/// separated by U+000B, U+000C, U+0085 or U+2029 join exactly like lines separated by \n already did;
/// and a key or a literal prefix in prose followed by charset-only lines swallows up to the material
/// ceiling (about 256 chars of such lines) where it used to stop after six. The one thing redacted LESS
/// than before is charset-only continuation text past the ceiling (a six-join match could reach about
/// 1800 chars), which no key of that shape is long enough to reach. Measured over this repository's
/// sources and docs (about 6.4 million chars) and 594 million chars of real agent CLI session files,
/// the old and new redactor produced byte-identical output: the costs above are real but did not occur
/// in that corpus.
/// </para>
/// <para>
/// Still NOT joined, deliberately, and pinned by a test: a blank line between two runs (two breaks in
/// a row), trailing whitespace BEFORE a break, and a provider prefix that is itself split by the wrap.
/// Each would widen the swallow into ordinary paragraphs and prose. A generic env value wrapped onto a
/// second line keeps its tail (the env rule is single-line, see its own acceptance test), and a secret
/// with no provider prefix and no telling env name is invisible to every rule here.
/// </para>
/// </summary>
public static partial class TranscriptRedactor
{
    /// <summary>
    /// Which revision of the patterns this is. Bump it whenever a pattern widens or narrows: stores that
    /// remember what they already scrubbed (the agent CLI transcript scrub, backlog 116) scrub everything
    /// again once under the new revision. 2 is backlog 119's widening.
    /// </summary>
    public const int PolicyRevision = 2;

    /// <summary>
    /// An Anthropic API key: the <c>sk-ant-</c> prefix then 16 to 256 material chars of its charset,
    /// not embedded in a longer identifier (the lookbehind). The 16 floor (enforced by the replacer)
    /// keeps the bare prefix in prose out; real keys are far longer. The body is joined runs (round
    /// three, widened in the final round): a first run of up to 256 chars, then up to six joins of
    /// up to 256 chars each, greedy, where a joiner is a raw line break OR its escaped two-character
    /// text form (see the class doc), so a key wrapped by an 80-column terminal, wrapped repeatedly
    /// by a narrow pane, or wrapped inside double-encoded JSON redacts WHOLE instead of leaking its
    /// tail. The first run may be empty (a break directly after the prefix); a continuation run may
    /// not, so the match never ends on a joiner.
    /// </summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_-])sk-ant-(?<body>[A-Za-z0-9_-]{0,256}(?:" + Joiner + @"[A-Za-z0-9_-]{1,256}){0,256})")]
    private static partial Regex AnthropicKey();

    /// <summary>
    /// One join between two material runs (backlog 119): a raw line break of any kind (\r\n, \n, a
    /// lone \r, U+000B, U+000C, U+0085, U+2028, U+2029) or the escaped two-character text form of
    /// \r\n, \n or \r, then any indentation (spaces, tabs, or the escaped \t text form). No provider
    /// charset contains a break, a space, a tab or a backslash, so a joiner can never eat material and
    /// the alternatives never overlap with a run, which is also what keeps the nested quantifiers
    /// linear: at every position exactly one of "continue the run" or "take a joiner" can apply.
    /// </summary>
    private const string Joiner =
        @"(?:(?:\r\n|\\r\\n|\\n|\\r|[\n\r\v\f\u0085\u2028\u2029])(?:[ \t]|\\t)*)";

    /// <summary>
    /// Whether a body character is joiner material rather than key material. The backslash of an
    /// escaped joiner is handled by the caller, which skips it and the letter after it.
    /// </summary>
    private static bool IsJoinerChar(char c) =>
        c is '\r' or '\n' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029' or ' ' or '\t';

    /// <summary>
    /// A GitHub token: classic (<c>ghp_</c>/<c>gho_</c>/<c>ghu_</c>/<c>ghs_</c>) or fine-grained
    /// (<c>github_pat_</c>), then 16 to 255 material chars of the token charset, joined-runs
    /// wrap-tolerant like every provider pattern.
    /// </summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])(?:ghp_|gho_|ghu_|ghs_|github_pat_)(?<body>[A-Za-z0-9_]{0,255}(?:" + Joiner + @"[A-Za-z0-9_]{1,255}){0,255})")]
    private static partial Regex GitHubToken();

    /// <summary>
    /// A Slack token: <c>xoxb-</c>/<c>xoxp-</c>/<c>xoxa-</c> or an app token <c>xapp-</c>, then 10 to
    /// 250 material chars of its charset (digits, letters and hyphens). The 10 floor keeps a short id
    /// fragment like <c>xoxb-1234</c> in prose out.
    /// </summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9-])(?:xoxb-|xoxp-|xoxa-|xapp-)(?<body>[A-Za-z0-9-]{0,250}(?:" + Joiner + @"[A-Za-z0-9-]{1,250}){0,250})")]
    private static partial Regex SlackToken();

    /// <summary>A GitLab personal access token: <c>glpat-</c> then at least its published 20 chars.</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_-])glpat-(?<body>[A-Za-z0-9_-]{0,255}(?:" + Joiner + @"[A-Za-z0-9_-]{1,255}){0,255})")]
    private static partial Regex GitLabPat();

    /// <summary>
    /// An AWS access key id: <c>AKIA</c> then 16 of [A-Z0-9], bounded on both sides so a longer
    /// identifier never partially matches on a single line. Across joins the greedy runs can pick up
    /// a following uppercase word (the documented join acceptance), so the replacer floor is 16; the
    /// join count is capped at 16 (a one-column wrap) and the replacer's material ceiling of 16 hands
    /// back any following uppercase word the joins picked up past the key itself.
    /// </summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])AKIA(?<body>[A-Z0-9]{0,16}(?:" + Joiner + @"[A-Z0-9]{1,16}){0,16})(?![A-Za-z0-9])")]
    private static partial Regex AwsAccessKeyId();

    /// <summary>
    /// A decorated env assignment on its own line: optional leading +/- (diff lines), whitespace,
    /// <c>export</c> or <c>declare -x</c>, and a quote; then an UPPERCASE name containing KEY, TOKEN,
    /// SECRET or PASSWORD; <c>=</c> with no spaces; then a quoted value (redacted inside its quotes)
    /// or an unquoted run, allowed to be followed only by a closing quote, trailing whitespace or an
    /// inline <c>#</c> comment before end of line. Spaced assignments (<c>var TOKEN = ...</c>) and
    /// lowercase names never match; the guards in <see cref="LooksNonSecret"/> run before replacing.
    /// </summary>
    [GeneratedRegex(
        @"^(?<prefix>[+\-]?[ \t]*(?:(?:export|declare[ \t]+-x)[ \t]+)?[""']?)(?<name>(?=[A-Z_])[A-Z0-9_]*(?:KEY|TOKEN|SECRET|PASSWORD)[A-Z0-9_]*)=(?:""(?<value>[^""\r\n]+)""|'(?<value>[^'\r\n]+)'|(?<value>[^\s""']+))(?=[""']?[ \t]*(?:#[^\r\n]*)?\r?$)",
        RegexOptions.Multiline)]
    private static partial Regex EnvAssignment();

    /// <summary>
    /// The same env assignment inside a JSON string (a docker inspect Env array, a serialized
    /// command): the name is preceded by a quote and the value CONSUMES ESCAPES (round three): an
    /// escaped quote or backslash is part of the value, not its end, so the whole value redacts and
    /// runs to the real closing quote. Round two stopped the value at the first backslash, which
    /// redacted the head and leaked everything after an embedded escaped quote.
    /// </summary>
    [GeneratedRegex(
        @"(?<="")(?<name>(?=[A-Z_])[A-Z0-9_]*(?:KEY|TOKEN|SECRET|PASSWORD)[A-Z0-9_]*)=(?<value>(?:[^""\\\r\n]|\\.)+)(?="")")]
    private static partial Regex JsonEnvAssignment();

    /// <summary>
    /// Value shapes the env rule must not touch: placeholders and templates people write on purpose.
    /// Provider-prefix matches are NEVER guarded; by the time the env rule runs, any provider token
    /// in a value is already a marker, which is also what makes the "path value UNLESS a provider
    /// prefix matched" rule fall out of ordering rather than needing its own branch.
    /// </summary>
    private static readonly string[] PlaceholderFragments =
        ["your-", "changeme", "example", "placeholder", "dummy", "<"];

    /// <summary>Redacts the known secret shapes out of a piece of transcript text.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        // Provider prefixes first (primary, unguarded), then the guarded env rules over what remains.
        var redacted = ReplaceToken(AnthropicKey(), text, min: 16, max: 256, marker: "sk-ant-api-key");
        redacted = ReplaceToken(GitHubToken(), redacted, min: 16, max: 255, marker: "github-token");
        redacted = ReplaceToken(SlackToken(), redacted, min: 10, max: 250, marker: "slack-token");
        redacted = ReplaceToken(GitLabPat(), redacted, min: 20, max: 255, marker: "gitlab-pat");
        redacted = ReplaceToken(AwsAccessKeyId(), redacted, min: 16, max: 16, marker: "aws-access-key-id");
        redacted = EnvAssignment().Replace(redacted, RedactEnvMatch);
        return JsonEnvAssignment().Replace(redacted, RedactEnvMatch);
    }

    /// <summary>
    /// Applies one provider pattern. The joined-runs regex cannot enforce a TOTAL floor across the
    /// joins, so the material's char count (joiner chars excluded) is re-checked here and a match
    /// under the floor is returned unchanged: that is what keeps prose glued across a line break out.
    /// A backslash in the body can only be part of an ESCAPED joiner (no provider charset contains
    /// one), so it and the letter after it are joiner, not material. There is deliberately NO total
    /// ceiling here: the ceiling is structural (each run is capped in the regex, at most seven
    /// runs), because DECLINING an over-long greedy match would return the real key inside it to
    /// the transcript, which is exactly the padding attack the round-two harness ran against the
    /// storage cap.
    /// <para>
    /// What bounds the swallow since backlog 119 raised the join cap is the MATERIAL CEILING
    /// <paramref name="max"/>, the pattern's per-run cap and so the longest material a key of that
    /// shape can have. It never declines: once the material count reaches the ceiling the marker ends
    /// at the end of the run that reached it, and only what follows that run (which starts with a
    /// joiner, and which no key of this shape is long enough to own) goes back into the text. The
    /// remainder carries no prefix and sits after the marker's closing bracket, so a second pass cannot
    /// pick it up either.
    /// </para>
    /// </summary>
    private static string ReplaceToken(Regex pattern, string text, int min, int max, string marker)
    {
        return pattern.Replace(text, m =>
        {
            var body = m.Groups["body"].ValueSpan;
            var material = 0;
            var cut = body.Length;
            for (var i = 0; i < body.Length; i++)
            {
                var c = body[i];
                if (c == '\\')
                {
                    i++;
                }
                else if (!IsJoinerChar(c))
                {
                    material++;
                    if (material >= max)
                    {
                        // The ceiling is reached inside this run: the run ends at the next joiner.
                        var end = i + 1;
                        while (end < body.Length && body[end] != '\\' && !IsJoinerChar(body[end]))
                        {
                            end++;
                        }

                        cut = end;
                        break;
                    }
                }
            }

            if (material < min)
            {
                return m.Value;
            }

            var replacement = "[redacted:" + marker + "]";
            return cut >= body.Length
                ? replacement
                : string.Concat(replacement, body[cut..]);
        });
    }

    /// <summary>
    /// Replaces only the value span of an env-rule match, keeping the decoration (prefix, quotes,
    /// trailing comment) exactly as written, unless the guards say the value is not a secret.
    /// </summary>
    private static string RedactEnvMatch(Match m)
    {
        var name = m.Groups["name"].Value;
        var value = m.Groups["value"];
        if (LooksNonSecret(name, value.Value))
        {
            return m.Value;
        }

        var start = value.Index - m.Index;
        return string.Concat(
            m.Value.AsSpan(0, start),
            "[redacted:" + name + "]",
            m.Value.AsSpan(start + value.Length));
    }

    /// <summary>
    /// The non-secret value guards (env rule only; item 102 round two, decided): passthroughs
    /// (<c>$NAME</c>, <c>${...}</c>, <c>$(...)</c>), placeholder fragments, path values containing
    /// <c>/</c>, the name assigned to itself, and anything under 8 chars all round trip untouched.
    /// </summary>
    private static bool LooksNonSecret(string name, string value)
    {
        if (value.Length < 8 || value[0] == '$')
        {
            return true;
        }

        if (value.Contains('/') || string.Equals(value, name, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var fragment in PlaceholderFragments)
        {
            if (value.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Redacts every string value in a JSON payload, recursively, in place. Recursion is what covers a
    /// turn document's message and question texts as well as the flat detail/text fields; non-string
    /// values (numbers, booleans, ids that are not strings) are untouched by construction, and string
    /// values that carry neither shape come back byte-identical. Returns whether anything changed, so
    /// the startup scrub only rewrites rows it actually altered.
    /// </summary>
    public static bool RedactInPlace(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var changed = false;
                foreach (var name in obj.Select(static p => p.Key).ToList())
                {
                    changed |= RedactChild(obj[name], replace: value => obj[name] = value);
                }

                return changed;
            }

            case JsonArray array:
            {
                var changed = false;
                for (var i = 0; i < array.Count; i++)
                {
                    var index = i;
                    changed |= RedactChild(array[index], replace: value => array[index] = value);
                }

                return changed;
            }

            default:
                return false;
        }
    }

    private static bool RedactChild(JsonNode? child, Action<JsonNode?> replace)
    {
        if (child is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var redacted = Redact(text);
            if (!string.Equals(redacted, text, StringComparison.Ordinal))
            {
                replace(JsonValue.Create(redacted));
                return true;
            }

            return false;
        }

        return RedactInPlace(child);
    }
}
