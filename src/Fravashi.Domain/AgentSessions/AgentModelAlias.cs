namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// The models a work item can suggest for the conversation that works on it (requested 2026-10-22, model and
/// effort per work item). A closed set of the three family ALIASES the session model menu already offers (the
/// curated Claude catalog's <c>opus</c>, <c>sonnet</c> and <c>haiku</c> entries, held equal by a test), because
/// an alias keeps meaning "the current one of that family" while the item waits on the board, where a dated id
/// would quietly age. A nullable value is the contract: null is "Default", which changes nothing about how the
/// conversation is started.
/// </summary>
public enum AgentModelAlias
{
    /// <summary>The <c>haiku</c> alias: the fastest and cheapest.</summary>
    Haiku,

    /// <summary>The <c>sonnet</c> alias.</summary>
    Sonnet,

    /// <summary>The <c>opus</c> alias.</summary>
    Opus,
}

/// <summary>The CLI ids of <see cref="AgentModelAlias"/>, written out rather than derived from member names.</summary>
public static class AgentModelAliases
{
    /// <summary>Every alias, cheapest first: the order a picker offers them in.</summary>
    public static IReadOnlyList<AgentModelAlias> All { get; } =
        [AgentModelAlias.Haiku, AgentModelAlias.Sonnet, AgentModelAlias.Opus];

    /// <summary>The exact id passed after <c>--model</c>.</summary>
    public static string CliId(this AgentModelAlias alias) => alias switch
    {
        AgentModelAlias.Haiku => "haiku",
        AgentModelAlias.Sonnet => "sonnet",
        AgentModelAlias.Opus => "opus",
        _ => throw new ArgumentOutOfRangeException(nameof(alias), alias, "Not a model alias the studio offers."),
    };

    /// <summary>The alias a CLI id names, exactly and ordinally, or null for anything else.</summary>
    public static AgentModelAlias? Parse(string? id)
    {
        foreach (var alias in All)
        {
            if (string.Equals(alias.CliId(), id, StringComparison.Ordinal))
            {
                return alias;
            }
        }

        return null;
    }
}
