using System.Text.RegularExpressions;

namespace Fravashi.Domain.AgentSessions;

/// <summary>A credential the studio itself placed in an agent child's environment.</summary>
/// <param name="Name">The variable's name, which is safe to show.</param>
/// <param name="Value">Its value, which is never shown, logged or put in a sentence.</param>
public sealed record AgentHeldCredential(string Name, string Value);

/// <summary>
/// What a tool call has to do with the credentials the agent is holding (backlog 108).
/// <para>
/// The agent CLI needs its provider credential to run, so it is in the child's environment by design and
/// every tool the child starts can read it. Until the broker tier removes the key from the container
/// altogether, which is what closes that item, two narrower things can be true and are held here.
/// </para>
/// <para>
/// A call whose arguments CARRY a credential's value is refused. There is no legitimate reason for the
/// value to be in an argument: whatever needs it already has it from the environment.
/// </para>
/// <para>
/// A call whose arguments NAME a held credential, or read the whole environment, is not refused, because
/// the studio cannot know what a shell will do with a name and refusing would break honest commands. It
/// is made impossible to approve without seeing: the prompt carries an alarm naming the variable.
/// </para>
/// <para>
/// What this does NOT cover, stated rather than implied: a script file that reads the environment when it
/// is later run, a name assembled at run time, an encoded or split value, and anything a process does
/// after it was approved. It is a statement about the arguments of one call and nothing more. The sync
/// engine's own check on what reaches the HOST is the other half and does not depend on any of this.
/// </para>
/// </summary>
public static partial class AgentCredentialExposure
{
    /// <summary>A value shorter than this is not treated as a credential: it would match ordinary text.</summary>
    public const int MinimumValueLength = 12;

    /// <summary>
    /// The NAME of a held credential whose value appears in any argument, or null. The value itself never
    /// leaves this method.
    /// </summary>
    public static string? CarriedBy(IReadOnlyDictionary<string, string> input, IEnumerable<AgentHeldCredential> held)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(held);

        foreach (var credential in held)
        {
            if (credential.Value is not { Length: >= MinimumValueLength } value)
            {
                continue;
            }

            if (input.Values.Any(argument => argument is not null && argument.Contains(value, StringComparison.Ordinal)))
            {
                return credential.Name;
            }
        }

        return null;
    }

    /// <summary>True when any of this text carries a held credential's value. For file content, not arguments.</summary>
    public static string? CarriedBy(string? text, IEnumerable<AgentHeldCredential> held)
    {
        ArgumentNullException.ThrowIfNull(held);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return held
            .Where(c => c.Value is { Length: >= MinimumValueLength } && text.Contains(c.Value, StringComparison.Ordinal))
            .Select(c => c.Name)
            .FirstOrDefault();
    }

    /// <summary>The alarms a prompt owes the user about this call and the credentials the agent holds.</summary>
    public static IReadOnlyList<AgentToolEffect> Naming(
        IReadOnlyDictionary<string, string> input, IEnumerable<AgentHeldCredential> held) =>
        Alarms(null, input, held, wholeEnvironment: true);

    /// <summary>
    /// The fields the two alarms do NOT read, by tool (backlog 314), and the only place that says so. Each is a label
    /// the CLI shows a person and never executes, read from the pinned CLI's own tool schemas, so an alarm on it fires
    /// on prose ("... without live browser env" was a whole-environment read). An exclusion list on purpose: an unknown
    /// tool, an unknown field and every field not named here keep being read, because over-asking costs a glance and
    /// under-asking costs the credential.
    /// <list type="bullet">
    /// <item><c>Bash.description</c>: a plain-words account of the command for the user; <c>command</c> is what runs.</item>
    /// <item><c>Monitor.description</c>: shown in notifications; <c>command</c> is what runs.</item>
    /// </list>
    /// Deliberately NOT here: <c>Agent.description</c> (whether that label reaches the subagent was not established),
    /// and any body of Write, Edit or NotebookEdit (a file written now may run later). Tool names match ordinally and
    /// exactly, so an <c>mcp__*__Bash</c> keeps every field. <see cref="CarriedBy(IReadOnlyDictionary{string, string}, IEnumerable{AgentHeldCredential})"/>
    /// never consults this: a credential's VALUE in a description is still refused.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> DisplayOnlyFields =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Bash"] = new HashSet<string>(StringComparer.Ordinal) { "description" },
            ["Monitor"] = new HashSet<string>(StringComparer.Ordinal) { "description" },
        };

    /// <summary>True when the alarms leave this field of this tool unread (<see cref="DisplayOnlyFields"/>).</summary>
    public static bool IsDisplayOnly(string? toolName, string field) =>
        toolName is not null && DisplayOnlyFields.TryGetValue(toolName, out var fields) && fields.Contains(field);

    private static IReadOnlyList<AgentToolEffect> Alarms(
        string? toolName, IReadOnlyDictionary<string, string> input, IEnumerable<AgentHeldCredential> held,
        bool wholeEnvironment)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(held);

        var names = held.Select(c => c.Name).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            return [];
        }

        var effects = new List<AgentToolEffect>();
        foreach (var (field, argument) in input)
        {
            if (string.IsNullOrEmpty(argument) || IsDisplayOnly(toolName, field))
            {
                continue;
            }

            foreach (var name in names.Where(name => NamesVariable(argument, name)))
            {
                effects.Add(new AgentToolEffect(
                    field,
                    name,
                    $"This names {name}, which holds a credential the agent is running with. Approving lets this "
                    + "call read it and put it anywhere it can write, and the studio cannot see what it does with it.",
                    AgentEffectWeight.Alarm));
            }

            if (wholeEnvironment && WholeEnvironment().IsMatch(argument))
            {
                effects.Add(new AgentToolEffect(
                    field,
                    string.Join(", ", names),
                    "This reads the whole environment, which includes a credential the agent is running with "
                    + $"({string.Join(", ", names)}). Approving lets this call put it anywhere it can write.",
                    AgentEffectWeight.Alarm));
            }
        }

        return effects;
    }

    // A variable name is a name only as a whole word: OPENAI_API_KEY_BACKUP does not name OPENAI_API_KEY. Case
    // is ignored on purpose, because over-asking costs a glance and under-asking costs the credential.
    private static bool NamesVariable(string argument, string name)
    {
        var index = 0;
        while ((index = argument.IndexOf(name, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = index == 0 ? ' ' : argument[index - 1];
            var end = index + name.Length;
            var after = end >= argument.Length ? ' ' : argument[end];
            if (!IsNameCharacter(before) && !IsNameCharacter(after))
            {
                return true;
            }

            index = end;
        }

        return false;
    }

    private static bool IsNameCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// The same alarms, scoped by the tool they are for (backlog 241). The whole-environment line is left off a
    /// studio tool that only STORES text (<see cref="AgentHostAlias.ExemptTools"/>, the one list backlog 191
    /// keeps of them): a work item or wiki text mentioning printenv cannot read an environment, so the line is
    /// false there. The naming alarm is unchanged for every tool, and so is the refusal of a call carrying a
    /// value, which is <see cref="CarriedBy(IReadOnlyDictionary{string, string}, IEnumerable{AgentHeldCredential})"/>
    /// and never consults the tool. Both alarms skip the tool's <see cref="DisplayOnlyFields"/> (backlog 314).
    /// </summary>
    public static IReadOnlyList<AgentToolEffect> Naming(
        string? toolName, IReadOnlyDictionary<string, string> input, IEnumerable<AgentHeldCredential> held) =>
        Alarms(toolName, input, held, wholeEnvironment: !AgentHostAlias.IsExempt(toolName));

    // Ways to read every variable at once, and nothing else (backlog 241: the alarm says "the whole environment",
    // so a form that reads ONE variable by name is not on it; a held credential's name is caught by the naming
    // rule above, which runs on every argument whatever this pattern says). Kept narrow on purpose, because an
    // alarm that fires on the safe form teaches the person to read past it:
    // - printenv only with no variable name after it: bare, or options only, then a pipe, a redirect, a command
    //   separator, a closing quote or bracket, or the end. printenv NAME prints that one variable.
    // - env the same way: "env" as a word also appears in "#!/usr/bin/env python", ".env", "venv" and
    //   "env FOO=1 cmd", so it is matched only as a command with nothing after it but a pipe, a redirect or the end.
    // - process.env and os.environ used WHOLE. A member or index with a LITERAL name (process.env.NAME,
    //   process.env["NAME"], os.environ["NAME"], os.environ.get("NAME"), a quote escaped for the shell included)
    //   reads one variable and is not matched,
    //   for the same reason as printenv NAME. A computed key (process.env[k], os.environ.get(k)) can be any
    //   variable and is matched, and so is a method call on process.env (valueOf() hands back the whole object).
    // - /proc/*/environ, export -p, declare -x and declare -p print everything whatever follows.
    [GeneratedRegex(
        @"\bprintenv(?:[ \t]+-[^\s""'`]*)*(?:[""'`)\]]|[ \t]*(?:\r?$|\d*[<>]|[|;&)]))"
        + @"|\bprocess\.env\b(?!\s*(?:\.\s*(?>[A-Za-z_$][\w$]*)(?!\s*\()|\[\s*\\?(?<pq>[""'`])[A-Za-z_]\w*\\?\k<pq>\s*\]))"
        + @"|\bos\.environ\b(?!\s*(?:\[\s*\\?(?<iq>[""'])[A-Za-z_]\w*\\?\k<iq>\s*\]"
        + @"|\.\s*get\s*\(\s*\\?(?<gq>[""'])[A-Za-z_]\w*\\?\k<gq>\s*[,)]))"
        + @"|/proc/[^\s/]+/environ|\bexport\s+-p\b|\bdeclare\s+-[xp]\b"
        + @"|(^|[;&|(\s])env\s*($|[|>;&)])",
        RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex WholeEnvironment();
}
