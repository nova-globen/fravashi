using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Fravashi.Web.Components.Setup;

/// <summary>
/// Moves focus to a setup screen's heading on arrival (item #373, <c>docs/plans/first-open-setup.md</c> section 8), so a
/// screen reader announces the question first. It yields once before focusing: the router's FocusOnNavigate focuses the
/// page bar's h1 on the same navigation, and the step's own heading is the one that should end up with focus.
/// </summary>
internal static class SetupFocus
{
    public static async Task HeadingAsync(ElementReference heading)
    {
        try
        {
            await Task.Yield();
            await heading.FocusAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            // Prerendering, a test without a browser, or a circuit that has gone: there is nobody to give focus to.
        }
    }

    public static async Task ElementAsync(ElementReference element)
    {
        try
        {
            await element.FocusAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
        }
    }
}
