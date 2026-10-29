namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// Where ANY tool names the thing it acts on, read or write.
/// <para>
/// <see cref="AgentToolSafety"/> answers this for the tools that may run unattended, because it has to:
/// its list is what makes auto-allowing one a deliberate act. This answers it for the rest, which is a
/// different job with a different failure mode. Nothing here decides whether a call runs (ADR 0028's
/// trust branch keys on the resolved target's containment, not on these names); it decides what the user is
/// TOLD about the call they are answering, and being silent about a destination is how a change outside
/// the workspace copy got approved.
/// </para>
/// <para>
/// So an undeclared tool falls back to the field names that are a location whatever sends them, rather
/// than to nothing. Guessing wrong there costs an extra line on a prompt that was going to be shown
/// anyway; guessing nothing costs the user the one fact they are deciding on.
/// </para>
/// </summary>
public static class AgentToolPaths
{
    /// <summary>
    /// Field names that carry a location in every tool that has one, best first. Kept in step with
    /// <see cref="AgentToolSafety"/>'s read targets on purpose: a tool that reads with "file_path"
    /// writes with it too.
    /// </summary>
    private static readonly string[] PathShaped = ["file_path", "notebook_path", "path"];

    /// <summary>
    /// The CHANGING tools whose path field is worth naming outright, so the common case never rests on
    /// the fallback. Anything not here still gets <see cref="PathShaped"/>.
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyList<string>> Declared = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Write"] = ["file_path"],
        ["Edit"] = ["file_path"],
        ["MultiEdit"] = ["file_path"],
        ["NotebookEdit"] = ["notebook_path"],
    };

    /// <summary>
    /// The input fields that could name a path for this tool, best first. A read-only tool answers with
    /// the fields <see cref="AgentToolSafety"/> already declared for it, so one call cannot be scoped by
    /// one field and described by another.
    /// </summary>
    public static IReadOnlyList<string> FieldsOf(string? toolName)
    {
        // A declared read answers for itself, including with nothing: a tool that queries the harness
        // rather than the disk has no location to name, and the fallback must not invent it one.
        if (AgentToolSafety.TargetOf(toolName) is { } read)
        {
            return read.PathFields;
        }

        return !string.IsNullOrWhiteSpace(toolName) && Declared.TryGetValue(toolName, out var declared)
            ? declared
            : PathShaped;
    }
}
