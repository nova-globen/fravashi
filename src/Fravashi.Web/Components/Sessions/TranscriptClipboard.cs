using Microsoft.JSInterop;

namespace Fravashi.Web.Components.Sessions;

/// <summary>What sessionCopy.js reports back about one clipboard write.</summary>
/// <param name="Ok">True when the text reached the clipboard.</param>
/// <param name="Reason">Why not, when it did not. Shown to the reader verbatim.</param>
public sealed record CopyOutcome(bool Ok, string? Reason);

/// <summary>
/// The conversation's side of sessionCopy.js (backlog 110). One place, because three surfaces call it
/// (the message control, each code block's control, and the right-click menu) and a copy that silently
/// did nothing must be impossible to write by accident: every path here returns an outcome, and the
/// failures that are NOT the script's fault (a dead circuit, a prerender with no JS runtime at all) turn
/// into a reason rather than an exception the caller has to remember to catch.
/// </summary>
public static class TranscriptClipboard
{
    public const string CopyFunction = "fravashiSessionCopy.copy";
    public const string SelectionFunction = "fravashiSessionCopy.selection";

    public static async Task<CopyOutcome> CopyAsync(IJSRuntime js, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new CopyOutcome(false, "there was nothing to copy");
        }

        try
        {
            var outcome = await js.InvokeAsync<CopyOutcome?>(CopyFunction, text);
            return outcome ?? new CopyOutcome(false, "the clipboard gave no answer");
        }
        catch (JSDisconnectedException)
        {
            return new CopyOutcome(false, "the studio lost its connection to the window");
        }
        catch (JSException ex)
        {
            return new CopyOutcome(false, ex.Message);
        }
        catch (InvalidOperationException)
        {
            // Prerender: there is no JS runtime yet. Nothing was copied and saying so is the honest
            // answer; a button cannot be clicked before the first render anyway.
            return new CopyOutcome(false, "the window was not ready");
        }
    }

    /// <summary>
    /// What the reader has selected right now, or empty. A failure to ask is an empty selection, not an
    /// error: the only consequence is that the menu's Copy item is offered disabled.
    /// </summary>
    public static async Task<string> SelectionAsync(IJSRuntime js)
    {
        try
        {
            return await js.InvokeAsync<string?>(SelectionFunction) ?? string.Empty;
        }
        catch (JSDisconnectedException)
        {
            return string.Empty;
        }
        catch (JSException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
