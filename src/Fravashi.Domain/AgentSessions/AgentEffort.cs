namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// How hard the agent CLI is asked to think (requested 2026-10-22, model and effort per work item): the closed
/// set of levels the pinned Claude Code 2.1.280 documents for <c>--effort &lt;level&gt;</c> in its own --help. A
/// nullable <see cref="AgentEffort"/> is the whole contract: null is "Default", which passes no flag at all and
/// leaves the CLI on its own default, exactly as a null model does (decision 7). A free string never reaches
/// the command line; only <see cref="AgentEfforts.CliValue"/> of one of these members does.
/// </summary>
public enum AgentEffort
{
    /// <summary><c>--effort low</c>.</summary>
    Low,

    /// <summary><c>--effort medium</c>.</summary>
    Medium,

    /// <summary><c>--effort high</c>.</summary>
    High,

    /// <summary><c>--effort xhigh</c>.</summary>
    XHigh,

    /// <summary><c>--effort max</c>.</summary>
    Max,
}

/// <summary>The CLI values of <see cref="AgentEffort"/>, written out rather than derived from member names.</summary>
public static class AgentEfforts
{
    /// <summary>Every level, lowest first: the order a picker offers them in.</summary>
    public static IReadOnlyList<AgentEffort> All { get; } =
        [AgentEffort.Low, AgentEffort.Medium, AgentEffort.High, AgentEffort.XHigh, AgentEffort.Max];

    /// <summary>
    /// The exact value passed after <c>--effort</c>. Written out so renaming a member can never change what the CLI
    /// is told; an undefined value is a programming fault and throws rather than reaching the command line.
    /// </summary>
    public static string CliValue(this AgentEffort effort) => effort switch
    {
        AgentEffort.Low => "low",
        AgentEffort.Medium => "medium",
        AgentEffort.High => "high",
        AgentEffort.XHigh => "xhigh",
        AgentEffort.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "Not an effort level the agent CLI accepts."),
    };

    /// <summary>
    /// The level a CLI value names, exactly and ordinally, or null for anything else (blank included). Null is the
    /// honest reading of an unknown value: it is "Default", which passes nothing, never a guess at a level.
    /// </summary>
    public static AgentEffort? Parse(string? value)
    {
        foreach (var effort in All)
        {
            if (string.Equals(effort.CliValue(), value, StringComparison.Ordinal))
            {
                return effort;
            }
        }

        return null;
    }
}
