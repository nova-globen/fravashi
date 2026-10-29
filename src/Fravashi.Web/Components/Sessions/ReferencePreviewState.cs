using Fravashi.Domain.Wiki;

namespace Fravashi.Web.Components.Sessions;

/// <summary>
/// Which reference a session pane's preview sidebar shows (work item #351), or none. One per pane, cascaded to the
/// transcript inside it, so a click anywhere in the conversation opens the preview of the pane it is in. The wiki page
/// holds one too (work item #365), and there a reference can also be a file path in the workspace.
/// </summary>
public sealed class ReferencePreviewState
{
    /// <summary>Raised after the shown reference changes.</summary>
    public event Action? Changed;

    /// <summary>The reference on show, or null when the sidebar is closed or shows a file.</summary>
    public WikiReference? Current { get; private set; }

    /// <summary>The workspace-relative path of the file on show (work item #365), or null.</summary>
    public string? CurrentFile { get; private set; }

    /// <summary>True while anything is on show.</summary>
    public bool IsOpen => Current is not null || CurrentFile is not null;

    /// <summary>Shows a reference, replacing whatever was on show.</summary>
    public void Show(WikiReference reference)
    {
        Current = reference;
        CurrentFile = null;
        Changed?.Invoke();
    }

    /// <summary>Shows a file of the workspace by its relative path, replacing whatever was on show.</summary>
    public void ShowFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Current = null;
        CurrentFile = path;
        Changed?.Invoke();
    }

    /// <summary>Closes the sidebar.</summary>
    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        Current = null;
        CurrentFile = null;
        Changed?.Invoke();
    }
}
