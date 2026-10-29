namespace Fravashi.Domain.AgentSessions;

/// <summary>How loudly the approval prompt has to say a thing.</summary>
public enum AgentEffectWeight
{
    /// <summary>True and worth stating, but still the ordinary case.</summary>
    Note,

    /// <summary>
    /// Changes what saying yes MEANS. It must not render like the ordinary case, because an alarm that
    /// fires on everything says nothing about the call that matters.
    /// </summary>
    Alarm,
}

/// <summary>One sentence the prompt owes the user about an argument, and how loudly to say it.</summary>
/// <param name="Field">The input key this is about, named so the sentence can be checked against the call.</param>
/// <param name="Value">The value as the CLI sent it, shortened for display only.</param>
/// <param name="Notice">The sentence itself, written for someone deciding whether to press Approve.</param>
/// <param name="Weight">Whether this is the ordinary case being stated or the reason it is not.</param>
public sealed record AgentToolEffect(
    string Field,
    string Value,
    string Notice,
    AgentEffectWeight Weight);

/// <summary>
/// Arguments whose VALUE changes what a tool call means, for tools the studio can be asked to approve.
/// <para>
/// <see cref="AgentToolPaths"/> answers "where does this land"; this answers "and what does doing it
/// there actually do". They are the same defect one layer apart. The padded-path escape worked because
/// the prompt showed the reassuring half of a true sentence about a PATH; an Agent call has no path at
/// all, and its prompt showed a three-word description and a task prompt while <c>isolation: "remote"</c>
/// sent the workspace copy to a cloud environment that is not this machine. Both times what was rendered
/// was true and what was missing was the decision.
/// </para>
/// <para>
/// Every rule here is written the fail-closed way round: it declares the values it stays QUIET for, and
/// speaks for everything else, including values nobody anticipated. A field the studio does not
/// recognise the value of is a field it cannot promise anything about, so it says that rather than
/// nothing. Field names and defaults come from the CLI's own <c>sdk-tools.d.ts</c>, read in the
/// container, not from what a tool description implies: <c>run_in_background</c> defaults to TRUE on
/// Agent and to false on Bash, so one shared rule would have been wrong for one of them. What the CLI
/// then DOES with a value is read with the studio's own switches on (<see cref="AgentCliSwitches"/>),
/// because a default the studio has switched off is not what happens here (backlog 192).
/// </para>
/// <para>
/// Nothing here allows anything or decides containment. A change asks either way; this only refuses to
/// be quiet about the argument the answer turns on.
/// </para>
/// </summary>
public static class AgentToolEffects
{
    /// <summary>A displayed value is capped: one argument cannot flood the prompt with its own prose.</summary>
    private const int MaxValueLength = 80;

    /// <summary>Reads one field's value (null when the tool did not send it) into a sentence, or nothing.</summary>
    private delegate AgentToolEffect? Rule(string? value);

    /// <summary>
    /// The order effects are rendered in, loudest first, and the complete set of fields any rule speaks
    /// for. A field absent from here is never asked about, so adding a rule means adding it here too.
    /// </summary>
    private static readonly string[] Order =
    [
        "isolation",
        "dangerouslyDisableSandbox",
        "discard_changes",
        "action",
        "edit_mode",
        "replace_all",
        "run_in_background",
    ];

    /// <summary>
    /// Rules that hold for whatever tool sends the field, because the field means the same thing
    /// wherever it appears. A tool that needs a different answer overrides it in <see cref="ByTool"/>.
    /// </summary>
    private static readonly Dictionary<string, Rule> Anywhere = new(StringComparer.Ordinal)
    {
        ["isolation"] = value => value switch
        {
            null or "" => null,

            "remote" => new AgentToolEffect(
                "isolation",
                "remote",
                "isolation is \"remote\": this runs in a REMOTE CLOUD ENVIRONMENT, not on this machine, and "
                + "the workspace copy goes there with it. Approving this sends the code off this computer.",
                AgentEffectWeight.Alarm),

            "worktree" => new AgentToolEffect(
                "isolation",
                "worktree",
                "isolation is \"worktree\": this works in a separate temporary git worktree, not in the "
                + "contained copy you are looking at.",
                AgentEffectWeight.Note),

            _ => new AgentToolEffect(
                "isolation",
                Show(value),
                $"isolation is \"{Show(value)}\", which this studio does not recognise, so it cannot say "
                + "where this would run or whether it stays on this machine.",
                AgentEffectWeight.Alarm),
        },

        ["dangerouslyDisableSandbox"] = value => Off(value)
            ? null
            : new AgentToolEffect(
                "dangerouslyDisableSandbox",
                Show(value),
                $"dangerouslyDisableSandbox is \"{Show(value)}\": this runs with sandboxing turned OFF.",
                AgentEffectWeight.Alarm),

        ["discard_changes"] = value => Off(value)
            ? null
            : new AgentToolEffect(
                "discard_changes",
                Show(value),
                $"discard_changes is \"{Show(value)}\": uncommitted work is thrown away instead of the call "
                + "refusing.",
                AgentEffectWeight.Alarm),

        ["replace_all"] = value => Off(value)
            ? null
            : new AgentToolEffect(
                "replace_all",
                Show(value),
                $"replace_all is \"{Show(value)}\": EVERY occurrence in the file is replaced, not only the "
                + "one shown below.",
                AgentEffectWeight.Note),

        // Bash's (and any tool's but the agent-spawning one, see SpawningAgent). Backlog 192: the studio
        // starts the CLI with background tasks off, and the pinned CLI then leaves this field out of the
        // schema it offers. Its code does not settle what a call that sends it anyway gets (refused, or run
        // in the foreground), so the sentence says what is known and no more.
        ["run_in_background"] = value => Off(value)
            ? null
            : new AgentToolEffect(
                "run_in_background",
                Show(value),
                $"run_in_background is \"{Show(value)}\", but this studio starts the CLI with "
                + $"{AgentCliSwitches.DisableBackgroundTasksVariable} set, so the CLI does not offer this field. "
                + "Whether it refuses the call or runs it in the foreground is not settled from the CLI's code; "
                + "if it does run in the background, it keeps running after you answer and what it does is not "
                + "shown here as it happens.",
                AgentEffectWeight.Note),
    };

    /// <summary>
    /// The tool that spawns another agent. The CLI's schema says "Agents run in the background by default",
    /// but that default is not what happens in THIS studio (backlog 192): every child is started with
    /// <see cref="AgentCliSwitches.DisableBackgroundTasksVariable"/> set, and the pinned CLI then launches an
    /// agent asynchronously only when its isolation is "remote" (which the isolation rule says loudly). So an
    /// absent value is described as the foreground run it really is (measured in the #50 trial: the
    /// subagent's result came back inside the turn), and an explicit true is described as the CLI treats it.
    /// Before this the card said BACKGROUND for the commonest Agent call there is, which was false.
    /// </summary>
    private static readonly Dictionary<string, Rule> SpawningAgent = new(StringComparer.Ordinal)
    {
        ["run_in_background"] = value =>
        {
            if (IsFalse(value))
            {
                return null;
            }

            if (string.IsNullOrEmpty(value))
            {
                return new AgentToolEffect(
                    "run_in_background",
                    "not sent",
                    "This agent runs in the FOREGROUND: this studio starts the CLI with "
                    + $"{AgentCliSwitches.DisableBackgroundTasksVariable} set, so the CLI waits for it and hands its "
                    + "result back inside this turn (unless its isolation is \"remote\", said above when it is). "
                    + "Its own tool calls are approved separately as they happen.",
                    AgentEffectWeight.Note);
            }

            return new AgentToolEffect(
                "run_in_background",
                Show(value),
                $"run_in_background is \"{Show(value)}\", but this studio starts the CLI with "
                + $"{AgentCliSwitches.DisableBackgroundTasksVariable} set: the CLI does not offer this field and "
                + "launches an agent in the background only when its isolation is \"remote\". If it accepts the "
                + "call, this agent runs in the FOREGROUND and hands its result back inside this turn; whether it "
                + "refuses the call instead is not settled from the CLI's code. Its own tool calls are approved "
                + "separately as they happen.",
                AgentEffectWeight.Note);
        },
    };

    /// <summary>Rules that belong to one tool, because the field means something else there.</summary>
    private static readonly Dictionary<string, Dictionary<string, Rule>> ByTool =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent"] = SpawningAgent,

            // The same tool under its older name. Measured on CLI 2.1.222 in the container: the hook
            // payload and the assistant's tool_use block both say "Agent", and only the run's final
            // permission_denials summary still says "Task". What this entry buys is the tool-specific
            // RULE: a name the studio failed to recognise would not merely mislabel the call, it would
            // fall back to the shared rule and say nothing about how the agent runs.
            ["Task"] = SpawningAgent,

            ["NotebookEdit"] = new(StringComparer.Ordinal)
            {
                ["edit_mode"] = value => value switch
                {
                    null or "" or "replace" => null,

                    "delete" => new AgentToolEffect(
                        "edit_mode",
                        "delete",
                        "edit_mode is \"delete\": the named cell is REMOVED. There is no replacement text to "
                        + "show because a deletion has none.",
                        AgentEffectWeight.Alarm),

                    "insert" => new AgentToolEffect(
                        "edit_mode",
                        "insert",
                        "edit_mode is \"insert\": this adds a new cell rather than changing the one named.",
                        AgentEffectWeight.Note),

                    _ => new AgentToolEffect(
                        "edit_mode",
                        Show(value),
                        $"edit_mode is \"{Show(value)}\", which this studio does not recognise, so it cannot "
                        + "say whether this replaces, adds or removes a cell.",
                        AgentEffectWeight.Alarm),
                },
            },

            ["ExitWorktree"] = new(StringComparer.Ordinal)
            {
                ["action"] = value => value switch
                {
                    null or "" or "keep" => null,

                    "remove" => new AgentToolEffect(
                        "action",
                        "remove",
                        "action is \"remove\": the worktree AND its branch are deleted from disk.",
                        AgentEffectWeight.Alarm),

                    _ => new AgentToolEffect(
                        "action",
                        Show(value),
                        $"action is \"{Show(value)}\", which this studio does not recognise, so it cannot say "
                        + "whether the worktree survives this.",
                        AgentEffectWeight.Alarm),
                },
            },
        };

    private static readonly Dictionary<string, Rule> None = new(StringComparer.Ordinal);

    /// <summary>
    /// What this call's arguments MEAN beyond where they point, loudest first, or empty when there is
    /// nothing to add.
    /// </summary>
    /// <param name="input">
    /// The tool's arguments as the CLI sent them. Read for their VALUE only: nothing here is a path, a
    /// containment answer or a permission, so the JSON kind that <c>AgentToolInput</c> guards does not
    /// come into it. A field sent as something other than a string still flattens to text that does not
    /// match a quiet value, and the fail-closed branch then says the studio cannot vouch for it.
    /// </param>
    public static IReadOnlyList<AgentToolEffect> Of(string? toolName, IReadOnlyDictionary<string, string> input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var declared = !string.IsNullOrWhiteSpace(toolName) && ByTool.TryGetValue(toolName, out var byTool)
            ? byTool
            : None;

        var effects = new List<AgentToolEffect>();
        foreach (var field in Order)
        {
            if (!declared.TryGetValue(field, out var rule) && !Anywhere.TryGetValue(field, out rule))
            {
                continue;
            }

            var value = input.TryGetValue(field, out var raw) ? raw?.Trim() : null;
            if (rule(value) is { } effect)
            {
                effects.Add(effect);
            }
        }

        return effects;
    }

    /// <summary>True when a boolean-shaped field is absent or plainly false, the only quiet readings.</summary>
    private static bool Off(string? value) => string.IsNullOrEmpty(value) || IsFalse(value);

    private static bool IsFalse(string? value) =>
        string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>The value on one line and capped, so it cannot become the prompt.</summary>
    private static string Show(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var single = value.ReplaceLineEndings(" ").Trim();
        return single.Length <= MaxValueLength ? single : single[..(MaxValueLength - 3)] + "...";
    }
}
