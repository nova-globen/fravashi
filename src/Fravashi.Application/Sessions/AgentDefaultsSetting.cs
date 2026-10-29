using Fravashi.Application.Framework;
using Fravashi.Application.Persistence;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>
/// One role's default model and effort (work item #357): what an agent of that role starts on when nothing more
/// specific says otherwise. A null part is "Default": it passes no flag at all, exactly as a null model and a null
/// effort do everywhere else (decision 7), so an install that never set anything behaves as it did before.
/// </summary>
/// <param name="Model">A model id from the curated catalog (the same ids the session's model menu offers), or null.</param>
/// <param name="Effort">An effort level from the closed set the session's effort menu offers, or null.</param>
public sealed record AgentDefault(string? Model, AgentEffort? Effort)
{
    /// <summary>Nothing set: no model flag and no effort flag.</summary>
    public static AgentDefault None { get; } = new(null, null);
}

/// <summary>
/// The three defaults Settings &gt; Agents &gt; Default models and effort holds (work item #357).
/// </summary>
/// <param name="NewConversation">What a new conversation (the Sessions area and Home's box) starts on.</param>
/// <param name="StudioAssistant">
/// What the studio assistant starts on: Home's box pre-selects it in its pickers (work item #359).
/// </param>
/// <param name="AutopilotBuilder">What the autopilot's building agent starts on.</param>
public sealed record AgentDefaults(AgentDefault NewConversation, AgentDefault StudioAssistant, AgentDefault AutopilotBuilder)
{
    /// <summary>Nothing set anywhere: today's behaviour.</summary>
    public static AgentDefaults None { get; } = new(AgentDefault.None, AgentDefault.None, AgentDefault.None);
}

/// <summary>
/// The default models and effort as the settings table holds them (work item #357). Precedence, from strongest:
/// a person's own pick in a session; a work item's suggested model and effort (for the item it is on); these
/// defaults; otherwise today's behaviour (no flag, or the dispatch's own builder model). The commit reviewer is not
/// governed by any of it: its model is fixed in its own section (decided 2026-10-10), and the operator is not
/// governed either.
/// </summary>
public static class AgentDefaultsSetting
{
    /// <summary>The settings-table key prefix every default lives under.</summary>
    public const string Prefix = "AgentDefaults:";

    /// <summary>The new conversation's model key.</summary>
    public const string NewConversationModelKey = Prefix + "NewConversation:Model";

    /// <summary>The new conversation's effort key.</summary>
    public const string NewConversationEffortKey = Prefix + "NewConversation:Effort";

    /// <summary>The studio assistant's model key.</summary>
    public const string StudioAssistantModelKey = Prefix + "StudioAssistant:Model";

    /// <summary>The studio assistant's effort key.</summary>
    public const string StudioAssistantEffortKey = Prefix + "StudioAssistant:Effort";

    /// <summary>The autopilot builder's model key.</summary>
    public const string AutopilotBuilderModelKey = Prefix + "AutopilotBuilder:Model";

    /// <summary>The autopilot builder's effort key.</summary>
    public const string AutopilotBuilderEffortKey = Prefix + "AutopilotBuilder:Effort";

    /// <summary>
    /// The model ids a default may name: the curated catalog the session's model menu is built from, without its own
    /// <c>default</c> entry (Default is null here, as it is in the menu).
    /// </summary>
    public static IReadOnlyList<string> ModelIds { get; } =
        [.. AgentModelCatalog.For(AgentTool.Claude).Select(m => m.Id).Where(id => id != "default")];

    /// <summary>
    /// The three defaults as stored. Anything missing, blank or outside the closed sets reads as Default, never as a
    /// guess, so a hand-edited row can never put a free string on the command line.
    /// </summary>
    public static AgentDefaults Read(IConfigurationStore config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new AgentDefaults(
            ReadOne(config, NewConversationModelKey, NewConversationEffortKey),
            ReadOne(config, StudioAssistantModelKey, StudioAssistantEffortKey),
            ReadOne(config, AutopilotBuilderModelKey, AutopilotBuilderEffortKey));
    }

    /// <summary>Stores the three defaults; a Default part is stored as no row at all.</summary>
    public static void Write(IConfigurationStore config, AgentDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(defaults);

        WriteOne(config, NewConversationModelKey, NewConversationEffortKey, defaults.NewConversation);
        WriteOne(config, StudioAssistantModelKey, StudioAssistantEffortKey, defaults.StudioAssistant);
        WriteOne(config, AutopilotBuilderModelKey, AutopilotBuilderEffortKey, defaults.AutopilotBuilder);
    }

    /// <summary>The model id when it is one a default may name, otherwise null (Default).</summary>
    public static string? ValidModel(string? id)
    {
        var trimmed = id?.Trim();
        return !string.IsNullOrEmpty(trimmed) && ModelIds.Contains(trimmed, StringComparer.Ordinal) ? trimmed : null;
    }

    private static AgentDefault ReadOne(IConfigurationStore config, string modelKey, string effortKey) =>
        new(ValidModel(config.Get(modelKey)), AgentEfforts.Parse(config.Get(effortKey)?.Trim()));

    private static void WriteOne(IConfigurationStore config, string modelKey, string effortKey, AgentDefault value)
    {
        if (ValidModel(value.Model) is { } model)
        {
            config.Set(modelKey, model);
        }
        else
        {
            config.Remove(modelKey);
        }

        if (value.Effort is { } effort)
        {
            config.Set(effortKey, effort.CliValue());
        }
        else
        {
            config.Remove(effortKey);
        }
    }
}

/// <summary>
/// A project's own default model and effort for a new conversation (item #373, the setup's Agents step; the host
/// settled the plan's question 3 as a per-project layer). Each part overrides the install's
/// <see cref="AgentDefaults.NewConversation"/> only when it is set, so setting one folder's project up never changes
/// another project's agents, and a project that never chose anything behaves exactly as before. The precedence, from
/// strongest: a person's own pick in a session; a work item's suggestion; this; the install's default; no flag.
/// </summary>
public static class ProjectAgentDefaults
{
    /// <summary>The model key for a project.</summary>
    public static string ModelKey(Domain.Projects.ProjectId project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return $"{AgentDefaultsSetting.Prefix}Project:{project.Value}:Model";
    }

    /// <summary>The effort key for a project.</summary>
    public static string EffortKey(Domain.Projects.ProjectId project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return $"{AgentDefaultsSetting.Prefix}Project:{project.Value}:Effort";
    }

    /// <summary>The project's own parts as stored; a part never set, or outside the closed sets, is null.</summary>
    public static AgentDefault Read(IConfigurationStore config, Domain.Projects.ProjectId project)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new AgentDefault(
            AgentDefaultsSetting.ValidModel(config.Get(ModelKey(project))),
            AgentEfforts.Parse(config.Get(EffortKey(project))?.Trim()));
    }

    /// <summary>Stores the project's own parts; a null part is stored as no row, which is "the install's default".</summary>
    public static void Write(IConfigurationStore config, Domain.Projects.ProjectId project, AgentDefault value)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(value);
        if (AgentDefaultsSetting.ValidModel(value.Model) is { } model)
        {
            config.Set(ModelKey(project), model);
        }
        else
        {
            config.Remove(ModelKey(project));
        }

        if (value.Effort is { } effort)
        {
            config.Set(EffortKey(project), effort.CliValue());
        }
        else
        {
            config.Remove(EffortKey(project));
        }
    }

    /// <summary>
    /// What a new conversation in <paramref name="project"/> starts on: each of the project's own parts where it has one,
    /// else the install's. A folder in no project gets the install's.
    /// </summary>
    public static AgentDefault NewConversation(IConfigurationStore config, Domain.Projects.ProjectId? project)
    {
        ArgumentNullException.ThrowIfNull(config);
        var install = AgentDefaultsSetting.Read(config).NewConversation;
        if (project is null)
        {
            return install;
        }

        var own = Read(config, project);
        return new AgentDefault(own.Model ?? install.Model, own.Effort ?? install.Effort);
    }
}
