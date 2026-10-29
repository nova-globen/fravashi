namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// wslc's host alias, and the alarm a call that names it owes the user (backlog 187, ADR 0029's D2 consequence).
/// <para>
/// On wslc every container on the default network reaches EVERY service bound to the host's 127.0.0.1 through
/// <see cref="Name"/> (<see cref="Address"/>), which wslc forwards to the host's loopback. The studio container is
/// on that network and its agents run in it, so a call naming it can reach a dev database, adb or a dev server on
/// this PC. Podman never allowed that. The owner kept the engine and guards it here instead.
/// </para>
/// <para>
/// Matching is case-insensitive substring on the whole argument text, on purpose: a false alarm costs a glance,
/// a silence costs the service. What it does NOT see, stated rather than implied: a name assembled at run time,
/// another name that resolves to the same address, and anything a process does after it was approved. This is
/// about what AGENTS reach; the studio process reaches the bridge at this name on purpose and never passes here.
/// </para>
/// </summary>
public static class AgentHostAlias
{
    /// <summary>The name wslc resolves to the host.</summary>
    public const string Name = "host.wslc.internal";

    /// <summary>The address that name resolves to inside a wslc container.</summary>
    public const string Address = "169.254.73.254";

    /// <summary>The sentence shared by the approval alarm and the browser refusal.</summary>
    public const string Why =
        "wslc forwards that address to every service listening on this PC's own loopback (127.0.0.1), "
        + "so it reaches services on this PC, not on the internet";

    /// <summary>True when this text names the alias in either spelling, in any case.</summary>
    public static bool IsNamedIn(string? text) =>
        !string.IsNullOrEmpty(text)
        && (text.Contains(Name, StringComparison.OrdinalIgnoreCase)
            || text.Contains(Address, StringComparison.Ordinal));

    /// <summary>
    /// Studio tools whose calls only store an argument's text, never execute it, and reach nothing with it
    /// (backlog 191): the work-item and wiki tools that create, read, change or link what is already text in
    /// the graph. A call naming the alias in one of these cannot talk to a PC service, so the alarm on it
    /// only taught the person approving to click past it, which cost the alarm where it matters (the #187
    /// trial: WebFetch, Bash, browser_open).
    /// <para>
    /// Closed and exact, matched against the name as the CLI actually sends it
    /// (<c>AgentWorkGate.StudioToolPrefix + bareName</c>, e.g. <c>mcp__fravashi__work_item_update</c>): a
    /// prefix match on "work_item_" would also catch <c>work_item_scenario_run</c>, which EXECUTES a command
    /// and belongs on the alarm's side, not here. <c>work_item_scenario_write</c> is NOT on this list either,
    /// even though it only stores text at the moment it is called: a step's <c>command</c> field is not
    /// text that stays text, it is the authoring half of an execution that <c>work_item_scenario_run</c>
    /// later carries out, and that run's own arguments are just a number and a scenario id
    /// (<c>ScenarioRunTool</c>), with nothing that resolves the stored command back onto its approval card.
    /// So the write's own card is the ONLY place a person ever sees the command text before it runs, and
    /// exempting it would write <c>curl http://host.wslc.internal:5432/</c> with no alarm and then run it
    /// with no alarm. <c>AgentHostAliasExemptToolsTests</c> pins this set to an exact literal list, so adding
    /// or removing a name is a decision a test forces rather than a drift nobody notices, and
    /// <c>AgentApprovalHostAliasTests</c>/<c>AgentGraphToolsTests</c> prove each entry names a real tool and
    /// that neither scenario tool is one of them.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> ExemptTools = new HashSet<string>(StringComparer.Ordinal)
    {
        AgentWorkGate.StudioToolPrefix + "work_item_create",
        AgentWorkGate.StudioToolPrefix + "work_item_update",
        AgentWorkGate.StudioToolPrefix + "work_item_transition",
        AgentWorkGate.StudioToolPrefix + "work_item_link",

        // Follow-up filed 2026-10-22 (record evidence against a criterion): its summary and reference are stored
        // as text on the item and never fetched or run.
        AgentWorkGate.StudioToolPrefix + "work_item_evidence",
        AgentWorkGate.StudioToolPrefix + "work_item_read",
        AgentWorkGate.StudioToolPrefix + "work_items_find",
        AgentWorkGate.StudioToolPrefix + "work_items_startable",
        AgentWorkGate.StudioToolPrefix + "wiki_find",
        AgentWorkGate.StudioToolPrefix + "wiki_read",
        AgentWorkGate.StudioToolPrefix + "wiki_write",
        AgentWorkGate.StudioToolPrefix + "wiki_link",
    };

    /// <summary>True when this exact tool name is one of <see cref="ExemptTools"/>.</summary>
    public static bool IsExempt(string? toolName) =>
        !string.IsNullOrEmpty(toolName) && ExemptTools.Contains(toolName);

    /// <summary>One alarm per argument that names the alias, or empty.</summary>
    public static IReadOnlyList<AgentToolEffect> Naming(IReadOnlyDictionary<string, string> input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var effects = new List<AgentToolEffect>();
        foreach (var (field, argument) in input)
        {
            if (!IsNamedIn(argument))
            {
                continue;
            }

            var spelling = argument.Contains(Name, StringComparison.OrdinalIgnoreCase) ? Name : Address;
            effects.Add(new AgentToolEffect(
                field,
                spelling,
                $"{field} names {spelling}: this reaches a SERVICE ON THIS PC through wslc's host alias. {Why}. "
                + "Approving lets this call talk to whatever is listening there (a dev database, adb, a dev server).",
                AgentEffectWeight.Alarm));
        }

        return effects;
    }

    /// <summary>
    /// The same alarm, scoped by the tool it is for (backlog 191): empty when <paramref name="toolName"/> is
    /// one of <see cref="ExemptTools"/>, because that call reaches nothing an alarm would be warning about.
    /// Every other tool, named by any other string including an unrecognised one, keeps the full match.
    /// </summary>
    public static IReadOnlyList<AgentToolEffect> Naming(string? toolName, IReadOnlyDictionary<string, string> input) =>
        IsExempt(toolName) ? [] : Naming(input);
}
