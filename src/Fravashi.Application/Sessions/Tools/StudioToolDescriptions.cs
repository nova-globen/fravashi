using System.Globalization;
using System.Text.Json;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Sessions.Tools;

/// <summary>
/// What the studio knows about a call's target before it runs, where it is cheap to know (work item #351): read by the
/// approval card so "Move #343 to In Progress" can say "from Ready". Every field is optional; a sentence without one
/// simply leaves it out.
/// </summary>
/// <param name="CurrentStatus">The work item's status right now, for a call that names one.</param>
public sealed record StudioToolFacts(WorkItemStatus? CurrentStatus = null);

/// <summary>
/// The ONE place a studio tool call is put into words a person can read (work item #351): a transcript row's label in
/// the past tense ("Linked this conversation to #343") and an approval card's sentence saying what the call WILL do
/// ("Move #343 from Ready to In Progress"). Both are built from the call's arguments, so they share one table and
/// cannot drift apart.
/// <para>
/// Built-in tools (Read, Grep, Bash and the rest) are not described here: their rows already read as what they are.
/// Every tool the studio's tool server registers must have an entry, which a test holds by enumerating the registered
/// tools. Neither form ever replaces the raw name and arguments: the row shows them when it is opened and the card
/// keeps them under "What it wants to do".
/// </para>
/// <para>
/// Arguments arrive as the flattened strings the approval broker and the transcript carry. A missing or unreadable
/// argument is left out of the words rather than guessed ("Moved a work item to Done"), because a row stored before
/// the arguments were kept has none at all.
/// </para>
/// </summary>
public static partial class StudioToolDescriptions
{
    private delegate string Words(Args arguments, StudioToolFacts facts);

    private sealed record Entry(Words Label, Words Sentence);

    private static readonly Dictionary<string, Entry> Table = Build();

    /// <summary>Every studio tool name described here, bare (no <c>mcp__fravashi__</c> prefix).</summary>
    public static IReadOnlyCollection<string> Described => Table.Keys;

    /// <summary>True when the tool (bare or prefixed name) has words here.</summary>
    public static bool Describes(string? toolName) => BareName(toolName) is { } bare && Table.ContainsKey(bare);

    /// <summary>
    /// The bare name of a studio tool: <c>mcp__fravashi__work_item_read</c> and <c>work_item_read</c> both give
    /// <c>work_item_read</c>. Any other MCP server's tool, and every built-in, gives null.
    /// </summary>
    public static string? BareName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        if (toolName.StartsWith(AgentWorkGate.StudioToolPrefix, StringComparison.Ordinal))
        {
            return toolName[AgentWorkGate.StudioToolPrefix.Length..];
        }

        return toolName.StartsWith("mcp__", StringComparison.Ordinal) ? null : toolName;
    }

    /// <summary>The row label for a call, in the past tense, or null for a tool this does not describe.</summary>
    public static string? Label(string? toolName, IReadOnlyDictionary<string, string>? arguments) =>
        Find(toolName) is { } entry ? Tidy(entry.Label(new Args(arguments), new StudioToolFacts())) : null;

    /// <summary>
    /// One sentence saying what the call will do, for its approval card, or null for a tool this does not describe.
    /// <paramref name="facts"/> carries what the studio already knows about the target, where that was cheap to learn.
    /// </summary>
    public static string? ApprovalSentence(
        string? toolName, IReadOnlyDictionary<string, string>? arguments, StudioToolFacts? facts = null) =>
        Find(toolName) is { } entry
            ? Tidy(entry.Sentence(new Args(arguments), facts ?? new StudioToolFacts()))
            : null;

    /// <summary>
    /// A tool's name as a person reads it, where only the name is known (work item #356): a studio tool by its bare
    /// name in words ("work item update"), another MCP server's tool as "tool (server)", and every built-in (Bash,
    /// Read, Edit) exactly as it is, because those already read as what they are. Never null; a blank name is "a tool".
    /// </summary>
    public static string DisplayName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return "a tool";
        }

        var name = toolName.Trim();
        if (name.StartsWith(AgentWorkGate.StudioToolPrefix, StringComparison.Ordinal))
        {
            var bare = name[AgentWorkGate.StudioToolPrefix.Length..];
            return bare.Length == 0 ? "a studio tool" : bare.Replace('_', ' ');
        }

        if (name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var rest = name["mcp__".Length..];
            var split = rest.IndexOf("__", StringComparison.Ordinal);
            return split <= 0
                ? rest.Replace('_', ' ')
                : rest[(split + 2)..].Replace('_', ' ') + " (" + rest[..split] + ")";
        }

        return name;
    }

    /// <summary>
    /// What a waiting call is, for a line a person reads outside the approval card (work item #356): the approval
    /// sentence when the tool is described ("Update #23: description"), else <see cref="DisplayName"/>.
    /// </summary>
    public static string ForPeople(
        string? toolName, IReadOnlyDictionary<string, string>? arguments, StudioToolFacts? facts = null) =>
        ApprovalSentence(toolName, arguments, facts) ?? DisplayName(toolName);

    /// <summary>
    /// Text that was written with a raw MCP tool name in it (a stored parked reason, an older line), with every such
    /// name put into words by <see cref="DisplayName"/> and quoted (work item #356). Built-in names are left alone.
    /// </summary>
    public static string Humanize(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : McpName().Replace(text, match => "\"" + DisplayName(match.Value) + "\"");

    [System.Text.RegularExpressions.GeneratedRegex(@"\bmcp__[A-Za-z0-9_\-]+")]
    private static partial System.Text.RegularExpressions.Regex McpName();

    /// <summary>The work item number a call acts on, when it names one: what an approval card looks up for its facts.</summary>
    public static int? WorkItemNumber(string? toolName, IReadOnlyDictionary<string, string>? arguments) =>
        BareName(toolName) switch
        {
            "work_item_read" or "work_item_update" or "work_item_transition" or "work_item_evidence"
                or "work_item_scenario_write" or "work_item_scenario_run" or "work_item_attach_image"
                or "session_start_building" => new Args(arguments).Number("number"),
            _ => null,
        };

    /// <summary>"in_progress" or <see cref="WorkItemStatus.InProgress"/> as a person says it: "In Progress".</summary>
    public static string StatusWords(WorkItemStatus status) => status switch
    {
        WorkItemStatus.Triage => "Triage",
        WorkItemStatus.Backlog => "Backlog",
        WorkItemStatus.Ready => "Ready",
        WorkItemStatus.InProgress => "In Progress",
        WorkItemStatus.InReview => "In Review",
        WorkItemStatus.Done => "Done",
        WorkItemStatus.Cancelled => "Cancelled",
        _ => status.ToString(),
    };

    private static Entry? Find(string? toolName) =>
        BareName(toolName) is { } bare && Table.TryGetValue(bare, out var entry) ? entry : null;

    /// <summary>One line, no doubled spaces, no trailing space before punctuation.</summary>
    private static string Tidy(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static Dictionary<string, Entry> Build()
    {
        var table = new Dictionary<string, Entry>(StringComparer.Ordinal);

        void Add(string name, Words label, Words sentence) => table.Add(name, new Entry(label, sentence));

        // ---- work items ------------------------------------------------------------------------------------
        Add("work_items_find",
            (a, _) => "Searched work items" + a.Quoted("text", " for ") + a.Suffix("status", " in "),
            (a, _) => "Search work items" + a.Quoted("text", " for ") + a.Suffix("status", " in "));
        Add("work_item_read",
            (a, _) => "Read " + a.Item("number"),
            (a, _) => "Read " + a.Item("number"));
        Add("work_items_startable",
            (_, _) => "Listed the work items ready to start",
            (_, _) => "List the work items ready to start");
        Add("work_item_releases",
            (a, _) => a.Text("version") is { } version ? "Read release " + version : "Listed the releases",
            (a, _) => a.Text("version") is { } version ? "Read release " + version : "List the releases");
        Add("work_item_create",
            (a, _) => "Created a work item" + a.Short("title", ": "),
            (a, _) => "Create a " + (a.Text("level") ?? "work item").Replace('_', ' ')
                      + (a.Text("level") is null ? string.Empty : " work item")
                      + a.Status("status", " in ") + a.Short("title", ": "));
        Add("work_item_update",
            (a, _) => "Updated " + a.Item("number") + Fields(a, ": "),
            (a, _) => "Update " + a.Item("number") + Fields(a, ": "));
        Add("work_item_transition",
            (a, _) => "Moved " + a.Item("number") + a.Status("status", " to ") + a.Suffix("resolution", ", resolved as "),
            (a, f) => "Move " + a.Item("number")
                      + (f.CurrentStatus is { } now ? " from " + StatusWords(now) : string.Empty)
                      + a.Status("status", " to ") + a.Suffix("resolution", ", resolved as "));
        Add("work_item_evidence",
            (a, _) => "Recorded evidence on " + a.Item("number") + Criterion(a),
            (a, _) => "Record evidence on " + a.Item("number") + Criterion(a));
        Add("work_item_link",
            (a, _) => (a.Flag("remove") ? "Unlinked: " : "Linked: ") + ItemLink(a),
            (a, _) => (a.Flag("remove") ? "Remove the link: " : "Link: ") + ItemLink(a));
        Add("work_item_scenario_write",
            (a, _) => "Wrote " + Count(a.Count("scenarios"), "test scenario") + " on " + a.Item("number"),
            (a, _) => "Write " + Count(a.Count("scenarios"), "test scenario") + " on " + a.Item("number"));
        Add("work_item_scenario_run",
            (a, _) => "Ran scenario" + a.Suffix("scenario", " ") + " of " + a.Item("number"),
            (a, _) => "Run scenario" + a.Suffix("scenario", " ") + " of " + a.Item("number"));
        Add("work_item_attach_image",
            (a, _) => "Attached " + ImageRef(a) + " to " + a.Item("number"),
            (a, _) => "Attach " + ImageRef(a) + " from this conversation to " + a.Item("number") + a.Quoted("name", " as "));

        // ---- the wiki --------------------------------------------------------------------------------------
        Add("wiki_find",
            (a, _) => "Searched the wiki" + a.Quoted("text", " for ") + a.Suffix("type", ", type "),
            (a, _) => "Search the wiki" + a.Quoted("text", " for ") + a.Suffix("type", ", type "));
        Add("wiki_read",
            (a, _) => "Read " + a.Wiki("number"),
            (a, _) => "Read " + a.Wiki("number"));
        Add("wiki_write",
            (a, _) => a.Number("number") is null
                ? "Wrote a new " + (a.Text("type") ?? "document") + a.Short("title", ": ")
                : "Wrote " + (a.Text("type") is { } type ? type + " " : string.Empty) + a.Wiki("number"),
            (a, _) => a.Number("number") is null
                ? "Create a new " + (a.Text("type") ?? "wiki document") + a.Short("title", ": ")
                : "Rewrite " + (a.Text("type") is { } type ? type + " " : string.Empty) + a.Wiki("number")
                  + a.Short("title", ": "));
        Add("wiki_link",
            (a, _) => a.Flag("remove")
                ? "Unlinked " + a.Wiki("number") + " from " + WikiTarget(a)
                : "Linked " + a.Wiki("number") + " to " + WikiTarget(a),
            (a, _) => a.Flag("remove")
                ? "Remove the link from " + a.Wiki("number") + " to " + WikiTarget(a)
                : "Link " + a.Wiki("number") + " to " + WikiTarget(a));

        // ---- designs and prototypes -----------------------------------------------------------------------
        Add("mockup_check",
            (a, _) => "Checked a mockup" + a.WikiSuffix("number", " for "),
            (a, _) => "Check a mockup" + a.WikiSuffix("number", " for "));
        Add("mockup_schema",
            (_, _) => "Read the mockup schema",
            (_, _) => "Read the mockup schema");
        Add("prototype_check",
            (a, _) => "Checked a prototype" + a.WikiSuffix("number", " for "),
            (a, _) => "Check a prototype" + a.WikiSuffix("number", " for "));
        Add("prototype_render",
            (a, _) => "Rendered " + Prototype(a) + a.WikiSuffix("design", " from design "),
            (a, _) => "Render " + Prototype(a) + a.WikiSuffix("design", " from design "));
        Add("prototype_adjust",
            (a, _) => "Adjusted prototype " + a.Wiki("number") + Changes(a),
            (a, _) => "Adjust prototype " + a.Wiki("number") + Changes(a));
        Add("design_approve",
            (a, _) => "Approved design " + a.Wiki("designNumber") + " for " + a.Item("workItemNumber"),
            (a, _) => "Approve design " + a.Wiki("designNumber") + " for " + a.Item("workItemNumber"));

        // ---- this conversation -----------------------------------------------------------------------------
        Add("session_work_read",
            (_, _) => "Read this conversation's work items",
            (_, _) => "Read this conversation's work items");
        Add("session_work_link",
            (a, _) => a.Flag("remove")
                ? "Unlinked this conversation from " + a.Item("number")
                : "Linked this conversation to " + a.Item("number"),
            (a, _) => a.Flag("remove")
                ? "Unlink this conversation from " + a.Item("number")
                : "Link this conversation to " + a.Item("number"));
        Add("session_plan_link",
            (a, _) => a.Flag("remove")
                ? "Took this conversation off plan " + a.Wiki("number")
                : "Put this conversation under plan " + a.Wiki("number"),
            (a, _) => a.Flag("remove")
                ? "Take this conversation off plan " + a.Wiki("number")
                : "Put this conversation under plan " + a.Wiki("number"));
        Add("session_governance_read",
            (_, _) => "Read what governs this conversation",
            (_, _) => "Read what governs this conversation");
        Add("session_attention",
            (a, _) => "Read the attention counts" + a.ItemSuffix("number", " for "),
            (a, _) => "Read the attention counts" + a.ItemSuffix("number", " for "));
        // Work item #359: the studio assistant's two own tools, which always ask; the card says exactly what will happen.
        Add("session_start_building",
            (a, _) => "Started a building conversation for " + a.Item("number"),
            (a, _) => "Start a building conversation in this folder for " + a.Item("number")
                      + ", linked to it, on its suggested model and effort (nothing starts if an editing conversation is already running here)");
        Add("autopilot_dispatch",
            (a, _) => "Dispatched the autopilot for " + Items(a, "numbers"),
            (a, _) => "Dispatch the autopilot for " + Items(a, "numbers")
                      + " with the last run's settings, marking them for the autopilot first (refused if any other item is marked)");
        Add("quick_change",
            (a, _) => "Started a quick change" + a.Short("summary", ": "),
            (a, _) => "Start a quick change (a work item, a plan and a done-check, linked here)" + a.Short("summary", ": "));

        // ---- git -------------------------------------------------------------------------------------------
        Add("attempt_branch",
            (_, _) => "Put this work on its own branch",
            (_, _) => "Put this conversation's work on its own git branch");
        Add("attempt_commit",
            (a, _) => "Committed the attempt" + a.Short("summary", ": "),
            (a, _) => "Commit the attempt" + a.Short("summary", ": "));
        Add("git_status",
            (_, _) => "Read git status",
            (_, _) => "Read git status");
        Add("git_log",
            (a, _) => "Read the git log" + a.Suffix("reference", " of ") + a.Suffix("path", " for "),
            (a, _) => "Read the git log" + a.Suffix("reference", " of ") + a.Suffix("path", " for "));
        Add("git_show",
            (a, _) => "Showed commit " + Sha(a.Text("sha")) + a.Suffix("path", ", ") + a.Suffix("file", ", "),
            (a, _) => "Show commit " + Sha(a.Text("sha")) + a.Suffix("path", ", ") + a.Suffix("file", ", "));
        Add("git_diff",
            (a, _) => "Read the diff" + Range(a) + a.Suffix("path", " of "),
            (a, _) => "Read the diff" + Range(a) + a.Suffix("path", " of "));
        Add("git_blame",
            (a, _) => "Read the blame of" + a.Suffix("path", " "),
            (a, _) => "Read the blame of" + a.Suffix("path", " "));
        Add("git_commit",
            (a, _) => "Committed" + Subject(a, ": "),
            (a, _) => "Commit " + (a.Count("paths") is { } files ? Count(files, "file") : "the approved changes")
                      + Subject(a, ": "));
        Add("git_push",
            (a, _) => a.Flag("check")
                ? "Checked whether origin has the commit"
                : a.Text("tag") is { } tag ? "Pushed tag " + tag : "Pushed the current branch",
            (a, _) => a.Flag("check")
                ? "Ask origin whether it already has the commit (pushes nothing)"
                : a.Text("tag") is { } tag ? "Push tag " + tag + " to origin" : "Push the current branch to origin");
        Add("git_tag",
            (a, _) => "Tagged" + a.Suffix("name", " ") + a.Short("message", ": "),
            (a, _) => "Create tag" + a.Suffix("name", " ") + a.Short("message", ": "));

        // ---- CI --------------------------------------------------------------------------------------------
        Add("ci_runs",
            (a, _) => "Read the CI runs" + a.Suffix("workflow", " of ") + ShaSuffix(a),
            (a, _) => "Read the CI runs" + a.Suffix("workflow", " of ") + ShaSuffix(a));
        Add("ci_run_log",
            (a, _) => "Read the log of CI run" + a.Suffix("run", " ") + a.Suffix("job", ", job "),
            (a, _) => "Read the log of CI run" + a.Suffix("run", " ") + a.Suffix("job", ", job "));

        // ---- the browser -----------------------------------------------------------------------------------
        Add("browser_open",
            (a, _) => "Opened" + a.Suffix("url", " "),
            (a, _) => "Open" + a.Suffix("url", " ") + " in this conversation's browser");
        Add("browser_read",
            (_, _) => "Read the browser page",
            (_, _) => "Read the browser page");
        Add("browser_click",
            (a, _) => "Clicked " + Element(a),
            (a, _) => "Click " + Element(a) + " on the browser page");
        Add("browser_fill",
            (a, _) => "Filled " + Element(a) + (a.Flag("submit") ? " and pressed Enter" : string.Empty),
            (a, _) => "Fill " + Element(a) + a.Quoted("value", " with ")
                      + (a.Flag("submit") ? " and press Enter" : string.Empty));
        Add("browser_press",
            (a, _) => "Pressed" + a.Suffix("key", " "),
            (a, _) => "Press" + a.Suffix("key", " ") + " on the browser page");
        Add("browser_screenshot",
            (_, _) => "Took a screenshot of the browser page",
            (_, _) => "Take a screenshot of the browser page");
        Add("browser_close",
            (_, _) => "Closed the browser page",
            (_, _) => "Close the browser page");

        // ---- the studio ------------------------------------------------------------------------------------
        Add("studio_version",
            (_, _) => "Read the studio's version",
            (_, _) => "Read the studio's version");
        Add("studio_upgrade",
            (a, _) => "Asked to upgrade the studio" + a.Suffix("version", " to "),
            (a, _) => "Upgrade the studio" + a.Suffix("version", " to ") + " when this turn ends");

        return table;
    }

    // ---- phrases shared by several entries -----------------------------------------------------------------

    /// <summary>Which fields a work item update changes, in words: ": description and acceptance criteria".</summary>
    private static string Fields(Args a, string lead)
    {
        var names = a.Keys
            .Where(key => key is not "number" and not "version")
            .Select(key => key switch
            {
                "acceptanceCriteria" => "acceptance criteria",
                "nextAction" => "next action",
                "suggestedModel" => "suggested model",
                "suggestedEffort" => "suggested effort",
                _ => key.Replace('_', ' '),
            })
            .ToList();
        return names.Count == 0 ? string.Empty : lead + JoinAnd(names);
    }

    private static string Criterion(Args a) =>
        (a.Text("criterion") is { } criterion ? ", criterion " + criterion : string.Empty)
        + (a.Text("result") is { } result ? ": " + result : string.Empty);

    private static string ItemLink(Args a)
    {
        var type = a.Text("type") switch
        {
            "blocks" => "blocks",
            "blocked_by" => "is blocked by",
            "relates" => "relates to",
            "duplicates" => "duplicates",
            "duplicated_by" => "is duplicated by",
            "supersedes" => "supersedes",
            "superseded_by" => "is superseded by",
            { } other => other.Replace('_', ' '),
            null => "relates to",
        };
        return a.Item("fromNumber") + " " + type + " " + a.Item("toNumber");
    }

    private static string WikiTarget(Args a)
    {
        var target = a.Text("target");
        return a.Text("kind") switch
        {
            "work_item" when int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var n) => "#" + Num(n),
            "wiki_item" when int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var n) => "@" + Num(n),
            "pull_request" when int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var n) => "!" + Num(n),
            "repository_file" when target is not null => target,
            _ => target ?? "something",
        };
    }

    private static string ImageRef(Args a) => a.Number("image") is { } n ? "[Image " + Num(n) + "]" : "an image";

    /// <summary>The work items an array argument names, in words: "#12, #14 and #15", from its JSON.</summary>
    private static string Items(Args a, string key)
    {
        if (a.Text(key) is not { } text || !text.StartsWith('['))
        {
            return "the named work items";
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return "the named work items";
            }

            var numbers = document.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n > 0)
                .Select(e => "#" + Num(e.GetInt32()))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return numbers.Count == 0 ? "the named work items" : JoinAnd(numbers);
        }
        catch (JsonException)
        {
            return "the named work items";
        }
    }

    private static string Prototype(Args a) =>
        a.Number("number") is null ? "a prototype" : "prototype " + a.Wiki("number");

    private static string Changes(Args a) =>
        a.Count("operations") is { } n ? ": " + Count(n, "change") : string.Empty;

    private static string Subject(Args a, string lead)
    {
        var message = a.Text("message");
        if (message is null)
        {
            return string.Empty;
        }

        var first = message.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return first.Length == 0 ? string.Empty : lead + Shorten(first, 100);
    }

    private static string Range(Args a)
    {
        var range = a.Text("base") is { } from
            ? " " + Sha(from) + (a.Text("head") is { } to ? ".." + Sha(to) : string.Empty)
            : string.Empty;
        return range + (a.Flag("staged") ? " of what is staged" : string.Empty);
    }

    private static string ShaSuffix(Args a) => a.Text("sha") is { } sha ? " for " + Sha(sha) : string.Empty;

    private static string Sha(string? sha) =>
        sha is null ? "?" : sha.Length == 40 && sha.All(char.IsAsciiHexDigit) ? sha[..10] : sha;

    private static string Element(Args a)
    {
        if (a.Text("role") is { } role)
        {
            return "the " + role + a.Quoted("name", " ");
        }

        if (a.Text("label") is { } label)
        {
            return "the field \"" + Shorten(label, 60) + "\"";
        }

        if (a.Text("text") is { } text)
        {
            return "\"" + Shorten(text, 60) + "\"";
        }

        return a.Text("selector") is { } selector ? Shorten(selector, 60) : "an element";
    }

    private static string Count(int? n, string noun) => n switch
    {
        null => noun + "s",
        1 => "1 " + noun,
        _ => Num(n.Value) + " " + noun + "s",
    };

    private static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Shorten(string text, int max)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= max)
        {
            return flat;
        }

        var head = flat[..(max - 3)];
        var space = head.LastIndexOf(' ');
        return (space > max / 2 ? head[..space] : head).TrimEnd() + "...";
    }

    private static readonly Dictionary<string, string> StatusNames = new(StringComparer.Ordinal)
    {
        ["triage"] = StatusWords(WorkItemStatus.Triage),
        ["backlog"] = StatusWords(WorkItemStatus.Backlog),
        ["ready"] = StatusWords(WorkItemStatus.Ready),
        ["in_progress"] = StatusWords(WorkItemStatus.InProgress),
        ["in_review"] = StatusWords(WorkItemStatus.InReview),
        ["done"] = StatusWords(WorkItemStatus.Done),
        ["cancelled"] = StatusWords(WorkItemStatus.Cancelled),
    };

    /// <summary>A call's arguments, read forgivingly: absent, blank and unreadable all read as absent.</summary>
    private sealed class Args(IReadOnlyDictionary<string, string>? values)
    {
        private readonly IReadOnlyDictionary<string, string> _values = values ?? new Dictionary<string, string>();

        public IEnumerable<string> Keys => _values.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key);

        public string? Text(string key)
        {
            if (!_values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();

            // A JSON string that arrived flattened with its quotes still reads as its text.
            return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"' ? trimmed[1..^1] : trimmed;
        }

        public int? Number(string key) =>
            Text(key) is { } text
            && int.TryParse(text.TrimStart('#', '@', '!'), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n > 0
                ? n
                : null;

        public bool Flag(string key) => string.Equals(Text(key), "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>How many items an array argument holds, from its JSON or from a stored "N items" summary.</summary>
        public int? Count(string key)
        {
            if (Text(key) is not { } text)
            {
                return null;
            }

            if (text.StartsWith('['))
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    return document.RootElement.ValueKind == JsonValueKind.Array
                        ? document.RootElement.GetArrayLength()
                        : null;
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            var space = text.IndexOf(' ', StringComparison.Ordinal);
            return space > 0
                   && text.EndsWith(" items", StringComparison.Ordinal)
                   && int.TryParse(text[..space], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? n
                : null;
        }

        public string Item(string key) => Number(key) is { } n ? "#" + Num(n) : "a work item";

        public string Wiki(string key) => Number(key) is { } n ? "@" + Num(n) : "a wiki document";

        public string ItemSuffix(string key, string lead) => Number(key) is { } n ? lead + "#" + Num(n) : string.Empty;

        public string WikiSuffix(string key, string lead) => Number(key) is { } n ? lead + "@" + Num(n) : string.Empty;

        public string Suffix(string key, string lead) => Text(key) is { } text ? lead + Shorten(text, 80) : string.Empty;

        public string Short(string key, string lead) => Text(key) is { } text ? lead + Shorten(text, 80) : string.Empty;

        public string Quoted(string key, string lead) => Text(key) is { } text ? lead + "\"" + Shorten(text, 60) + "\"" : string.Empty;

        public string Status(string key, string lead) =>
            Text(key) is { } status
                ? lead + (StatusNames.TryGetValue(status, out var words) ? words : status.Replace('_', ' '))
                : string.Empty;
    }
}
