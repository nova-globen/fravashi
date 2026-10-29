namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// What a session may do with tools, as a ROLE holds it (backlog 88). It is deliberately a small,
/// closed vocabulary rather than a list of tool names: a role is a statement about the kind of work
/// somebody does, and a list of names would have to be revised every time a CLI adds one.
/// </summary>
[Flags]
public enum RoleCapabilities
{
    /// <summary>Nothing at all. A role holding this can hold a conversation and nothing else.</summary>
    None = 0,

    /// <summary>Read files, search, and read the graph. Every role has this.</summary>
    Read = 1,

    /// <summary>Write to the Fravashi graph: work items, documents, plans, evidence.</summary>
    WriteGraph = 2,

    /// <summary>Change the workspace: edit, write, delete, move files.</summary>
    ChangeWorkspace = 4,

    /// <summary>Run commands. Separate from changing files because a command can do anything.</summary>
    RunCommands = 8,
}

/// <summary>
/// A role a session runs under (backlog 88). The vision names four and describes the loop in terms of
/// them; this is the answer to its open question, and the answer is "one agent under role-scoped
/// instructions and a role-scoped capability set", not four separate agent configurations.
/// <para>
/// The reason is where enforcement can actually live. The CLI's own tool list is part of a PINNED
/// contract (ADR 0023) that may only move by a live run, so a role that tried to restrict the CLI's
/// flags would be a promise the studio could not keep. What the studio does own is the approval path and
/// its own tool server, and both are on the far side of every call, so a capability withheld there is
/// withheld in fact rather than by request.
/// </para>
/// </summary>
/// <param name="Id">A stable lower-snake id, used in storage and on the wire.</param>
/// <param name="Name">What a person calls it.</param>
/// <param name="Capabilities">What a session under this role may do.</param>
/// <param name="Instructions">
/// What the role is for, in the words the agent is given. Projected into the session so the model reads
/// it, and shown to the user so the two are looking at the same statement.
/// </param>
public sealed record AgentRole(
    string Id,
    string Name,
    RoleCapabilities Capabilities,
    string Instructions)
{
    /// <summary>Whether a session under this role holds every one of these capabilities.</summary>
    public bool Holds(RoleCapabilities wanted) => (Capabilities & wanted) == wanted;
}

/// <summary>
/// The roles the product ships with: the four the vision names, plus the unrestricted one an ordinary
/// interactive session runs under when nobody has chosen.
/// <para>
/// They are defaults rather than a closed set. The catalogue they live in is editable, because a team's
/// roles are theirs; what is fixed is the CAPABILITY vocabulary, so a role somebody writes still says
/// something the studio can enforce.
/// </para>
/// </summary>
public static class AgentRoles
{
    /// <summary>The id of the role a session runs under when nobody has chosen one.</summary>
    public const string DefaultId = "interactive";

    /// <summary>Grooming, triage, sequencing, readiness. Moves Triage to Backlog to Ready.</summary>
    public static AgentRole Analyst { get; } = new(
        "analyst",
        "Analyst",
        RoleCapabilities.Read | RoleCapabilities.WriteGraph,
        "You are the analyst. You groom, triage and sequence work, and you decide when an item is ready. "
        + "Read the code and the graph as much as you need. Write to the graph: create and refine work "
        + "items, write acceptance criteria that can actually be checked, record what a change is blocked "
        + "on and who it is waiting for. You do NOT change the workspace and you do not run commands; if "
        + "the work needs doing, say so in the item and let the implementer do it.");

    /// <summary>Mockup to prototype to approved design. Produces the design gate.</summary>
    public static AgentRole Designer { get; } = new(
        "designer",
        "Designer",
        RoleCapabilities.Read | RoleCapabilities.WriteGraph,
        "You are the designer. You take a work item from a description to a design somebody can build "
        + "against: the mockup, then the prototype, then the approved design the verifier checks the built "
        + "thing against. Read anything. Write to the graph, including the design references on the item. "
        + "You do NOT change the workspace and you do not run commands.");

    /// <summary>Building. Moves Ready to In Progress to In Review.</summary>
    public static AgentRole Implementer { get; } = new(
        "implementer",
        "Implementer",
        RoleCapabilities.Read | RoleCapabilities.WriteGraph | RoleCapabilities.ChangeWorkspace
            | RoleCapabilities.RunCommands,
        "You are the implementer. You build what the work item and its plan describe, inside the scope and "
        + "the change budget they declare. Keep the graph current as you go: move the item, record what you "
        + "did, and say plainly when the plan turns out to be wrong rather than quietly doing something "
        + "else. When the work is built and the suite is green, move it to In Review.");

    /// <summary>Verification. Moves In Review to Done, or bounces it back.</summary>
    public static AgentRole Verifier { get; } = new(
        "verifier",
        "Reviewer / Verifier",
        RoleCapabilities.Read | RoleCapabilities.WriteGraph | RoleCapabilities.RunCommands,
        "You are the verifier. You check that what was built does what the item's acceptance criteria say, "
        + "by running the scenarios rather than by reading the diff and agreeing with it. Record what you "
        + "ran and what happened as evidence on the item. You may run commands and you may NOT change the "
        + "workspace: a verifier that fixes what it finds is no longer checking anything. Move the item to "
        + "Done when it passes, and back with a reason when it does not.");

    /// <summary>What an ordinary interactive session runs under: everything, with a person watching.</summary>
    public static AgentRole Interactive { get; } = new(
        DefaultId,
        "Interactive",
        RoleCapabilities.Read | RoleCapabilities.WriteGraph | RoleCapabilities.ChangeWorkspace
            | RoleCapabilities.RunCommands,
        "You are working with a person who is reading every change before it happens. Do what they ask, "
        + "and tell them what you find.");

    /// <summary>Every role the product ships with, in the order the loop runs them.</summary>
    public static IReadOnlyList<AgentRole> All { get; } =
        [Interactive, Analyst, Designer, Implementer, Verifier];

    /// <summary>The shipped role with this id, or null.</summary>
    public static AgentRole? Find(string? id) =>
        All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
}
