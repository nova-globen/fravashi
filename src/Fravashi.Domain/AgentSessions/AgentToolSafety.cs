namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// Where a read-only tool names the thing it is about to read. A tool with no
/// <see cref="PathFields"/> reads nothing off the filesystem at all (it queries the harness), so there
/// is no path to contain.
/// </summary>
/// <param name="PathFields">
/// Input keys that carry a location, best first. The first one present wins; the others are the
/// spellings a different tool of the same family uses.
/// </param>
/// <param name="PathRequired">
/// True when the tool's own schema makes the path mandatory, so a call without it is malformed and
/// must be asked about rather than assumed to mean the working directory.
/// </param>
/// <param name="PatternField">
/// An input key holding a PATH pattern resolved against the tool's directory (Glob's <c>pattern</c>,
/// Grep's <c>glob</c> filter). It is glob syntax, not a path: braces expand to whole segments and a
/// backslash escapes the character after it, so it can spell a traversal several ways over and is
/// contained separately (<c>AgentReadScope</c>). A pattern the tool matches against CONTENT rather
/// than a location (Grep's <c>pattern</c> is a regex) must never be named here.
/// </param>
public sealed record AgentReadTarget(
    IReadOnlyList<string> PathFields,
    bool PathRequired,
    string? PatternField = null)
{
    /// <summary>A tool that observes the harness rather than the disk.</summary>
    public static AgentReadTarget NothingOnDisk { get; } = new([], PathRequired: false);

    /// <summary>True when running this tool would open something on the filesystem.</summary>
    public bool ReadsTheFileSystem => PathFields.Count > 0 || PatternField is not null;
}

/// <summary>
/// Which agent tools the studio auto-allows without asking (decision 5, 2026-08-04): reads run
/// unattended, everything that can CHANGE something asks the user every single time. There is
/// deliberately no "remember this tool for the session": one approval must never cover an edit the
/// user has not seen. ADR 0028 adds one bounded exception that lives in the broker, not here: a folder whose
/// person chose to trust edits lets an edit that provably lands inside the copy run, and records it.
/// <para>
/// The list is an allowlist, so a tool the CLI grows in a later release asks by default. Network tools
/// (WebFetch, WebSearch) are NOT here: they do not mutate the workspace, but they carry its content off
/// the machine, which is a decision for the user to make.
/// </para>
/// <para>
/// "Read-only" is not the same as "harmless". A read is only unattended when it stays INSIDE the
/// session's contained copy: the same container holds the studio's SQLite database, its Data Protection
/// key ring, and a PID 1 whose environment carries the host-bridge token, and every one of those is a
/// file an unscoped <c>Read</c> would hand over silently. So each entry declares WHERE it reads, and the
/// caller (<c>AgentReadScope</c>) decides containment before anything is auto-allowed. A tool whose path
/// cannot be worked out asks, like every other unknown here.
/// </para>
/// </summary>
public static class AgentToolSafety
{
    /// <summary>
    /// The complete auto-allow set and, for each entry, where it reads. Every tool that touches the
    /// filesystem MUST declare its path fields here: that is what makes adding one a deliberate act
    /// rather than a silent hole.
    /// </summary>
    private static readonly Dictionary<string, AgentReadTarget> Targets = new(StringComparer.OrdinalIgnoreCase)
    {
        // Their path arguments are mandatory in the CLI's own schema, so a call without one is
        // malformed and gets asked about.
        ["Read"] = new(["file_path"], PathRequired: true),
        ["NotebookRead"] = new(["notebook_path"], PathRequired: true),
        ["LS"] = new(["path"], PathRequired: true),

        // Their path argument is optional and defaults to the child's working directory, which IS the
        // contained copy. Glob's pattern is resolved against that directory and can traverse out of it.
        // Grep's own pattern is the regex it searches FOR, so it is content, not a location; its
        // "glob" is a name filter over what the walk already found, which cannot leave the search path
        // today, and is contained anyway rather than resting on that.
        ["Glob"] = new(["path"], PathRequired: false, PatternField: "pattern"),
        ["Grep"] = new(["path"], PathRequired: false, PatternField: "glob"),

        // These ask the harness, not the disk.
        ["ToolSearch"] = AgentReadTarget.NothingOnDisk,
        ["TaskGet"] = AgentReadTarget.NothingOnDisk,
        ["TaskList"] = AgentReadTarget.NothingOnDisk,
        ["TaskOutput"] = AgentReadTarget.NothingOnDisk,
    };

    /// <summary>The names in the auto-allow set. Being on it is necessary, never sufficient.</summary>
    public static IReadOnlySet<string> ReadOnlyTools { get; } =
        new HashSet<string>(Targets.Keys, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the tool only observes. It still has to prove it observes something INSIDE the
    /// session's copy before it runs unattended.
    /// </summary>
    public static bool IsReadOnly(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && ReadOnlyTools.Contains(toolName);

    /// <summary>Where the tool reads, or null when it is not an auto-allowed read at all.</summary>
    public static AgentReadTarget? TargetOf(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && Targets.TryGetValue(toolName, out var target) ? target : null;
}
