namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// Which capability one tool call needs, and whether the session's role holds it (backlog 88).
/// <para>
/// The mapping is deliberately coarse and default-deny. Anything the studio does not recognise as a read
/// or as a command counts as changing the workspace, so a tool nobody anticipated is withheld from a role
/// that may not change things rather than waved through because its name was unfamiliar.
/// </para>
/// </summary>
public static class AgentRoleGate
{
    /// <summary>What the agent is told when its role does not hold what the call needs.</summary>
    public static string SentenceFor(AgentRole role, RoleCapabilities needed)
    {
        ArgumentNullException.ThrowIfNull(role);

        var what = needed switch
        {
            RoleCapabilities.ChangeWorkspace => "change files in the workspace",
            RoleCapabilities.RunCommands => "run commands",
            RoleCapabilities.WriteGraph => $"write to the {ProductName.Short} graph",
            _ => "do that",
        };

        return $"This session is running as the {role.Name}, and that role does not {what}. This is not an "
            + "error and it is not a mistake to be worked around: the role is a statement about what this "
            + "conversation is for. Do the part that is yours, say plainly what is left, and let the person "
            + "change the role or hand the rest to a session that holds it.";
    }

    /// <summary>
    /// What a call needs. <paramref name="readOnly"/> is the studio's own answer about the tool, and
    /// <paramref name="isStudioTool"/> says it is one of the studio's own graph tools.
    /// </summary>
    public static RoleCapabilities Needs(string? toolName, bool readOnly, bool isStudioTool)
    {
        if (readOnly)
        {
            return RoleCapabilities.Read;
        }

        if (isStudioTool)
        {
            // A studio tool that is not read-only changes the graph. Which graph tools are which is the
            // tool server's own business; it enforces that separately against the same role.
            return RoleCapabilities.WriteGraph;
        }

        // A command can do anything, including change files, so it is its own capability and a role that
        // may run commands is a role that is trusted with rather a lot.
        return IsCommand(toolName) ? RoleCapabilities.RunCommands : RoleCapabilities.ChangeWorkspace;
    }

    /// <summary>The verdict for one call: null when the role holds what it needs, a sentence when not.</summary>
    public static string? Check(AgentRole? role, string? toolName, bool readOnly, bool isStudioTool)
    {
        if (role is null)
        {
            // No role is the unrestricted interactive case, which is what a session has until somebody
            // chooses. It is not the same as a role that holds nothing.
            return null;
        }

        var needed = Needs(toolName, readOnly, isStudioTool);
        return role.Holds(needed) ? null : SentenceFor(role, needed);
    }

    /// <summary>
    /// The tools that run something. Matched exactly and ordinally on the names the CLIs actually send,
    /// so a case variant or a lookalike falls through to the stricter answer rather than the looser one.
    /// </summary>
    private static bool IsCommand(string? toolName) =>
        toolName is "Bash" or "BashOutput" or "KillShell" or "KillBash";
}
