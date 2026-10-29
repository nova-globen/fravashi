namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// Which calls to the studio's OWN tools run without asking a person (backlog 77, decided 2026-09-21).
/// <para>
/// ADR 0020 gates WORKSPACE mutations and deliberately not graph mutations, because writing the work item and
/// the plan is how a session clears the gate. The first live run of that loop showed the other half of the
/// sentence was missing: five of its six approvals were the agent creating the work item, writing the plan
/// and linking them, so a one-line change cost six clicks and the loop could not run unattended at all. The
/// owner's answer was that it should.
/// </para>
/// <para>
/// It is an explicit list of exact names, and it is narrower than "every studio tool" on purpose:
/// </para>
/// <list type="bullet">
/// <item>READS of the graph run. They name no location and return the studio's own stores.</item>
/// <item>CREATIONS and LINKS run. They add to the graph and destroy nothing.</item>
/// <item>A CHANGE to something that already exists still asks: the wiki keeps no history, so an agent
/// allowed to rewrite any document unattended could overwrite one a person wrote, and "never destroy the
/// owner's data" outranks a saved click. <c>wiki_write</c> is therefore unattended only when it creates,
/// which is when it carries no <c>number</c>. <c>prototype_render</c> follows the same rule (backlog 215): it
/// creates a prototype of a design with no <c>number</c>, and moves an existing one's pin with one, so only the
/// first runs; <c>prototype_adjust</c> always asks, its card listing each operation in words.</item>
/// <item><c>work_item_scenario_write</c> asks too, always (backlog 191): it REPLACES an item's whole
/// scenario list, which is the same "can overwrite what a person wrote" shape as a wiki change, and a
/// step's <c>command</c> is the authoring half of an execution <c>work_item_scenario_run</c> carries out
/// later with no command text of its own to show again. The write's own approval card is the only place a
/// person ever sees that text, so letting the write itself run unseen would mean nobody ever saw it at
/// all.</item>
/// <item>READS of the host repository's git run (backlog 150): status, log, show, diff and blame go through
/// the bridge into the workspace's own folder with a fixed argument vector, and change nothing there. They
/// show the agent what the person sees on the Git page, which is the repository it is already working in,
/// plus its history; asking for each of those would make history unusable without making anything safer.</item>
/// <item>READS of the repository's CI runs run (backlog 153): they ask the git host with the token the PR panel
/// already holds, carry nothing of the workspace out but the repository's own name, and change nothing.</item>
/// <item>ACTS ON THE PAGE a person let the session open run (backlog 159): reading it, a screenshot, a click, a key
/// press and closing it. Opening a URL and filling a field ask, because the first chooses where the session goes and
/// what the address carries, and the second decides what leaves in a form.</item>
/// <item>Everything that CHANGES something outside the graph still asks, whatever its prefix: a git commit,
/// a push, a tag, or an attempt branch or commit touches the host repository, a scenario run executes a command, a
/// design approval is a person's act by definition, and a quick change is built around one confirmed step.</item>
/// </list>
/// <para>
/// A name that is not here asks, including a studio tool added tomorrow. The tool server still enforces the
/// session's grant, so a read-only session cannot write whatever this says.
/// </para>
/// </summary>
public static class AgentGraphTools
{
    private const string Prefix = AgentWorkGate.StudioToolPrefix;

    private static readonly HashSet<string> Reads = new(StringComparer.Ordinal)
    {
        Prefix + "studio_version",
        Prefix + "wiki_find",
        Prefix + "wiki_read",
        Prefix + "mockup_check",
        Prefix + "mockup_schema",
        Prefix + "prototype_check",
        Prefix + "work_items_find",
        Prefix + "work_item_read",
        Prefix + "work_items_startable",
        Prefix + "work_item_releases",
        Prefix + "session_work_read",
        Prefix + "session_governance_read",
        Prefix + "session_attention",
    };

    private static readonly HashSet<string> CreationsAndLinks = new(StringComparer.Ordinal)
    {
        Prefix + "work_item_create",
        Prefix + "work_item_link",
        Prefix + "wiki_link",
        Prefix + "session_work_link",
        Prefix + "session_plan_link",
    };

    private static readonly HashSet<string> HostReads = new(StringComparer.Ordinal)
    {
        Prefix + "git_status",
        Prefix + "git_log",
        Prefix + "git_show",
        Prefix + "git_diff",
        Prefix + "git_blame",
    };

    private static readonly HashSet<string> ForgeReads = new(StringComparer.Ordinal)
    {
        Prefix + "ci_runs",
        Prefix + "ci_run_log",
    };

    private static readonly HashSet<string> OnTheOpenPage = new(StringComparer.Ordinal)
    {
        Prefix + "browser_read",
        Prefix + "browser_screenshot",
        Prefix + "browser_click",
        Prefix + "browser_press",
        Prefix + "browser_close",
    };

    private const string WikiWrite = Prefix + "wiki_write";

    // Backlog 215 (plan @40): the same rule as wiki_write. With no number it creates a prototype of a design and
    // replaces nothing; with a number it rewrites an existing prototype's pin, so it asks. prototype_adjust is not
    // here at all: every adjustment changes an existing document and asks, its card listing each operation.
    private const string PrototypeRender = Prefix + "prototype_render";

    /// <summary>
    /// Why this call runs without asking, or null when it has to ask. The name is matched ordinally and
    /// exactly, so a case variant or a tool from another server with a similar name asks.
    /// </summary>
    public static string? RunsUnattended(string? toolName, IReadOnlyDictionary<string, string> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrEmpty(toolName))
        {
            return null;
        }

        if (Reads.Contains(toolName))
        {
            return "This reads the studio's own graph and changes nothing.";
        }

        if (CreationsAndLinks.Contains(toolName))
        {
            return "This adds to the studio's graph, which is how a session clears a gate, and replaces nothing.";
        }

        if (HostReads.Contains(toolName))
        {
            return "This reads the workspace repository's git history on the host, through the bridge, and changes nothing.";
        }

        if (OnTheOpenPage.Contains(toolName))
        {
            return "This acts on the page a person already let this session open, in the session's own browser.";
        }

        if (ForgeReads.Contains(toolName))
        {
            return "This reads the repository's CI runs from its git host with the studio's own token, which stays in the studio, and changes nothing.";
        }

        if (string.Equals(toolName, WikiWrite, StringComparison.Ordinal)
            && (!input.TryGetValue("number", out var number) || string.IsNullOrWhiteSpace(number)))
        {
            return "This creates a new document in the studio's wiki and replaces nothing.";
        }

        if (string.Equals(toolName, PrototypeRender, StringComparison.Ordinal)
            && (!input.TryGetValue("number", out var prototype) || string.IsNullOrWhiteSpace(prototype)))
        {
            return "This creates a new prototype of a design in the studio's wiki and replaces nothing.";
        }

        return null;
    }
}
