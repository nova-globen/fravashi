using Fravashi.Domain.AgentSessions;
using Fravashi.Web.Components.Shared;

namespace Fravashi.Web.Components.Sessions;

/// <summary>
/// The effort levels as house-dropdown options (requested 2026-10-22, model and effort per work item), beside the
/// model picker in the composer. The option value is the CLI's own word for the level, so
/// the closed set in <see cref="AgentEfforts"/> is the only thing that turns it back into a level, and the empty
/// string is Default, which passes no <c>--effort</c> at all.
/// </summary>
public static class SessionEffortOptions
{
    /// <summary>The option that passes no <c>--effort</c>, as <see cref="SessionModelOptions.CliDefaultValue"/> does for the model.</summary>
    public const string CliDefaultValue = "";

    /// <summary>
    /// Every option, Default first. The Default label is the one word "Effort" rather than "Default", because it sits
    /// beside the model picker whose own Default reads "Default", and two controls both reading "Default" in a 280px
    /// pane say nothing about which is which; a chosen level reads "high effort" for the same reason.
    /// </summary>
    public static IReadOnlyList<StudioDropdownOption> All { get; } =
    [
        new(CliDefaultValue, "Effort", "Passes no effort flag, so the agent CLI uses its own default."),
        .. AgentEfforts.All.Select(effort => new StudioDropdownOption(
            effort.CliValue(), effort.CliValue() + " effort", Note(effort))),
    ];

    /// <summary>The option value for a level, the empty string for Default.</summary>
    public static string ValueOf(AgentEffort? effort) => effort?.CliValue() ?? CliDefaultValue;

    /// <summary>The level an option value names, null for Default and for anything that is not a level.</summary>
    public static AgentEffort? Parse(string? value) => AgentEfforts.Parse(value);

    private static string Note(AgentEffort effort) => effort switch
    {
        AgentEffort.Low => "Quickest and cheapest; for small, clear changes.",
        AgentEffort.Medium => "A balance of speed and depth.",
        AgentEffort.High => "Thinks longer before acting.",
        AgentEffort.XHigh => "Thinks longer still; for hard problems.",
        AgentEffort.Max => "The most thinking the CLI offers, and the most spending.",
        _ => string.Empty,
    };
}
