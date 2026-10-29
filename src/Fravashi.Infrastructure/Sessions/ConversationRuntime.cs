using Fravashi.Application.AgentRuntimes;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// The runtime the studio's conversations run on, named once (backlog 52). The live registry's runner is
/// <see cref="ClaudeSessionRunner"/> in every wiring this version has, and the stored session row records the same
/// tool, so a feature that needs to know what a conversation can do (see images, for one) asks this rather than
/// guessing from which runtimes are selected in Settings.
/// </summary>
public static class ConversationRuntime
{
    /// <summary>The tool every conversation started here runs on.</summary>
    public const AgentTool Tool = AgentTool.Claude;

    /// <summary>Its facts, from the catalog.</summary>
    public static AgentRuntimeDescriptor Descriptor => AgentRuntimeCatalog.For(Tool);
}
