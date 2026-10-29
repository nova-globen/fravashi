using Fravashi.Application.Persistence;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>
/// Which conversation is a folder's current studio assistant topic (work item #359): what Home's box continues, after a
/// restart too. One settings row per folder; New topic removes it, so the next message starts a fresh assistant
/// conversation. Only a pointer: whether the conversation still exists, is the assistant and can be continued is
/// asked of the registry every time it is read.
/// </summary>
public static class StudioAssistantTopic
{
    /// <summary>The settings-table key prefix.</summary>
    public const string Prefix = "StudioAssistant:Topic:";

    /// <summary>The folder's current topic, or null.</summary>
    public static AgentSessionId? Read(IConfigurationStore config, string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(config);
        return !string.IsNullOrWhiteSpace(workspaceId)
            && Guid.TryParse(config.Get(Prefix + workspaceId), out var id)
                ? new AgentSessionId(id)
                : null;
    }

    /// <summary>Makes this conversation the folder's current topic.</summary>
    public static void Write(IConfigurationStore config, string workspaceId, AgentSessionId session)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            config.Set(Prefix + workspaceId, session.Value.ToString("D"));
        }
    }

    /// <summary>The folder has no current topic any more (New topic).</summary>
    public static void Clear(IConfigurationStore config, string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            config.Remove(Prefix + workspaceId);
        }
    }
}
