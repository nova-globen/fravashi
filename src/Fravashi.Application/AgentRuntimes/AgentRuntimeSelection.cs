using Fravashi.Application.Persistence;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.AgentRuntimes;

/// <summary>
/// Reads and writes the user's chosen set of coding-agent runtimes (S10) - persisted as a comma-separated list
/// of <see cref="AgentTool"/> names under <see cref="SettingKey"/> in the settings table, so the choice survives
/// a refresh, a restart, and re-entry from Settings. Unknown/whitespace entries are ignored on read.
/// </summary>
public static class AgentRuntimeSelection
{
    /// <summary>The settings-table key holding the selected runtimes.</summary>
    public const string SettingKey = "AgentRuntimes:Selected";

    /// <summary>The selected runtimes, in catalog order. Empty when nothing is selected yet.</summary>
    public static IReadOnlyList<AgentTool> Read(IConfigurationStore config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var raw = config.Get(SettingKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var selected = new HashSet<AgentTool>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<AgentTool>(part, ignoreCase: true, out var tool))
            {
                selected.Add(tool);
            }
        }

        // Return in the stable catalog order, not the persisted order.
        return AgentRuntimeCatalog.Tools.Where(selected.Contains).ToList();
    }

    /// <summary>Persists the selected runtimes (in catalog order, de-duplicated).</summary>
    public static void Write(IConfigurationStore config, IEnumerable<AgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(tools);
        var set = tools.ToHashSet();
        var ordered = AgentRuntimeCatalog.Tools.Where(set.Contains).Select(t => t.ToString());
        config.Set(SettingKey, string.Join(",", ordered));
    }

    /// <summary>True when the runtime is currently selected.</summary>
    public static bool IsSelected(IConfigurationStore config, AgentTool tool) => Read(config).Contains(tool);
}
