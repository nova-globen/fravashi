namespace Fravashi.Web.Components.Sessions;

/// <summary>
/// A right-click inside the conversation, on its way from the thing that was clicked to the transcript
/// that owns the menu (backlog 110).
/// </summary>
/// <param name="X">Viewport x of the pointer, where the menu opens.</param>
/// <param name="Y">Viewport y of the pointer.</param>
/// <param name="MessageText">The RAW text of the message under the pointer, or null when the pointer
/// was not over one (the empty space below the last turn, a tool card, a note). The menu's
/// "Copy message" item is offered disabled in that case rather than hidden, so the menu does not
/// change shape depending on where in the pane it was opened.</param>
public sealed record TranscriptContextRequest(double X, double Y, string? MessageText);
