using Fravashi.Domain.AgentSessions;

namespace Fravashi.Web.Components.Sessions;

/// <summary>
/// Pairs a user's message with the images pasted into it (backlog 26). The registry records the images as
/// their own row immediately before the message, so the pairing is positional and needs no id. A studio prompt
/// can carry one too (backlog 52: the drawing a design is made from), paired the same way.
/// </summary>
public static class TranscriptImages
{
    /// <summary>The images for the user or studio message at <paramref name="index"/>, or null when it carried none.</summary>
    public static IReadOnlyList<AgentImageAttachment>? For(IReadOnlyList<AgentEvent> transcript, int index) =>
        index > 0
        && index < transcript.Count
        && transcript[index] is MessageEmitted { Role: TranscriptRole.User or TranscriptRole.StudioPrompt }
        && transcript[index - 1] is UserImagesAttached attached
            ? attached.Images
            : null;
}
