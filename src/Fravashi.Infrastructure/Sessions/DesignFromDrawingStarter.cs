using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Mockups;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>What "New design from a drawing" did: the conversation it started, or why it started none.</summary>
public sealed record DesignFromDrawingStart(LiveAgentSession? Session, string? Refusal)
{
    public static DesignFromDrawingStart Refused(string why) => new(null, why);
}

/// <summary>
/// Starts the conversation that turns a drawing into a design (backlog 52, plan @43). The drawing rides on the first
/// turn with the studio's instruction (<see cref="DesignFromDrawing.Instruction"/>), recorded as the studio's prompt
/// with the image shown above it; the AGENT then creates the design document through <c>mockup_schema</c>,
/// <c>mockup_check</c> and <c>wiki_write</c>. Nothing here writes to the wiki, so the image can never become the
/// design: the only document that results is the one holding the agent's tree.
/// <para>
/// Whether the runtime can see the drawing at all is read from its descriptor's
/// <see cref="AgentRuntimeDescriptor.SeesImages"/> before anything starts, and a runtime that cannot is refused
/// in words, because a model handed an image it cannot read still answers, with a tree made of the instruction.
/// </para>
/// </summary>
public sealed class DesignFromDrawingStarter
{
    private readonly LiveSessionRegistry _registry;
    private readonly AgentRuntimeDescriptor _runtime;

    /// <param name="runtime">The runtime conversations run on; <see cref="ConversationRuntime.Descriptor"/> unless a test says otherwise.</param>
    public DesignFromDrawingStarter(LiveSessionRegistry registry, AgentRuntimeDescriptor? runtime = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _runtime = runtime ?? ConversationRuntime.Descriptor;
    }

    /// <summary>Why this studio cannot turn a drawing into a design, or null when it can.</summary>
    public string? Unavailable => DesignFromDrawing.CannotSeeImages(_runtime);

    /// <summary>
    /// Starts the conversation in the window's workspace copy, or says why not: the runtime cannot see images, no
    /// workspace is open, no drawing was given, or the registry refused (a conversation already running in the
    /// folder, a spending ceiling), in the registry's own words.
    /// </summary>
    public DesignFromDrawingStart Start(
        string? workspaceId, string? workingDirectory, AgentImageAttachment? drawing, string? note)
    {
        if (Unavailable is { } cannot)
        {
            return DesignFromDrawingStart.Refused(cannot);
        }

        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(workingDirectory))
        {
            return DesignFromDrawingStart.Refused(
                "This window has no workspace folder with a contained copy, and a conversation runs in one. Open a "
                + $"folder with {ProductName.Short}, then start again.");
        }

        if (drawing is null)
        {
            return DesignFromDrawingStart.Refused("Choose or paste the drawing first.");
        }

        try
        {
            var session = _registry.Start(new SessionStartRequest(
                workspaceId,
                workingDirectory,
                DesignFromDrawing.Instruction(note),
                Title: DesignFromDrawing.ConversationTitle,
                Images: [drawing],
                FromStudio: true));
            return new DesignFromDrawingStart(session, null);
        }
        catch (InvalidOperationException ex)
        {
            return DesignFromDrawingStart.Refused(ex.Message);
        }
    }
}
