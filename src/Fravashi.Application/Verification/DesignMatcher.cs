using System.Globalization;
using System.Text;
using Fravashi.Application.Mockups;

namespace Fravashi.Application.Verification;

/// <summary>What a check found wrong (ADR 0030 decision 6).</summary>
public enum DesignFindingKind
{
    /// <summary>Nothing of the role and name is there.</summary>
    Missing,

    /// <summary>A control of the role sits where the design puts it, under another name.</summary>
    DifferentName,

    /// <summary>
    /// Backlog 290: something of the design's name sits where the design puts it, as another role a person finds or acts
    /// on (a link built as a button, or the reverse). Before this it was reported MISSING, which hid what was there.
    /// </summary>
    DifferentRole,

    /// <summary>It is there, but before something the design puts ahead of it.</summary>
    OutOfOrder,

    /// <summary>It is there, but not inside the element the design puts it in.</summary>
    OutsideItsContainer,

    /// <summary>It is not where the design's row or column puts it relative to the item before it.</summary>
    NotLaidOutAsDesigned,

    /// <summary>Its text contrast is under 3:1.</summary>
    Illegible,

    /// <summary>Its phase could not be reached, so it was not checked: never passed, never failed (backlog 55).</summary>
    NotRun,
}

/// <summary>How a phase at one viewport came out.</summary>
public enum DesignViewOutcome
{
    /// <summary>Everything the design asks was found as it asks.</summary>
    Matched,

    /// <summary>At least one finding.</summary>
    Mismatched,

    /// <summary>The phase was not reached; every expectation in it is NOT RUN.</summary>
    NotRun,
}

/// <summary>One thing that differs from the design, in the design's terms.</summary>
/// <param name="Kind">What differs.</param>
/// <param name="Role">The role the design asks, or null when it asks for words.</param>
/// <param name="ExpectedName">The name the design gives, as the design spells it, or null.</param>
/// <param name="Anchor">The design node, spelled as <c>data-mk-anchor</c> is.</param>
/// <param name="Part">Which part of the node.</param>
/// <param name="Column">A column header's key, or null.</param>
/// <param name="Phase">The phase's name.</param>
/// <param name="Viewport">The viewport's name, or null for a design that names none.</param>
/// <param name="Observed">
/// What was there instead: the other name (different name), the role it was there as (different role), the ratio (illegible), the anchor of the item it should
/// follow (layout), the container's anchor (outside), or why it was not run.
/// </param>
/// <param name="Sentence">The finding as a person reads it.</param>
public sealed record DesignFinding(
    DesignFindingKind Kind,
    string? Role,
    string? ExpectedName,
    string Anchor,
    DesignPart Part,
    string? Column,
    string Phase,
    string? Viewport,
    string? Observed,
    string Sentence);

/// <summary>An element the page shows that the design does not name: listed, never a failure (ADR 0030 decision 5).</summary>
/// <param name="Role">Its role.</param>
/// <param name="Name">Its accessible name, or null.</param>
/// <param name="Ref">Its snapshot reference, or null.</param>
public sealed record DesignExtra(string Role, string? Name, string? Ref);

/// <summary>One phase at one viewport.</summary>
/// <param name="Phase">The phase's name.</param>
/// <param name="Viewport">The viewport's name, or null.</param>
/// <param name="Outcome">How it came out.</param>
/// <param name="Findings">What differs, in the design's order.</param>
/// <param name="Extras">What the page shows that the design does not name, in the page's order.</param>
public sealed record DesignMatchView(
    string Phase, string? Viewport, DesignViewOutcome Outcome, IReadOnlyList<DesignFinding> Findings, IReadOnlyList<DesignExtra> Extras);

/// <summary>A screen checked against a page (ADR 0030 decision 6).</summary>
/// <param name="Screen">The design screen's index.</param>
/// <param name="Anchor">The design screen's anchor.</param>
/// <param name="Views">One per phase and viewport, in the design's order.</param>
public sealed record DesignMatchReport(int Screen, string Anchor, IReadOnlyList<DesignMatchView> Views)
{
    /// <summary>True exactly when nothing differs and nothing went unchecked. Extras never count.</summary>
    public bool Matches => Views.All(v => v.Findings.Count == 0);

    /// <summary>Every finding, view by view.</summary>
    public IEnumerable<DesignFinding> Findings => Views.SelectMany(v => v.Findings);
}

/// <summary>
/// Compares what a page showed with what a pinned design asks (backlog 222, ADR 0030 decisions 4 to 6, plan @46). Pure
/// and deterministic, and every tolerance is a rule a person can read:
/// <list type="bullet">
/// <item>Names compare after whitespace collapse and case folding, nothing looser.</item>
/// <item>Order is a subsequence, read greedily front to back, so a moved element is reported where the design reaches it.</item>
/// <item>Not found in order, an expectation is OUT OF ORDER when its exact role and name appear earlier, else DIFFERENT NAME
/// when a control of its role sits in its place, else DIFFERENT ROLE when something of its name and another listed role sits
/// in its place (backlog 290), else MISSING.</item>
/// <item>Containment and legibility (3:1 for buttons and headings) are judged on what was found by role and name.</item>
/// <item>A row's children sit left to right and a column's top to bottom, with 2 px slack, among the children whose
/// every member was found in order.</item>
/// <item>A phase not reached is NOT RUN. Elements the design does not name are listed, never failed.</item>
/// </list>
/// </summary>
public static class DesignMatcher
{
    /// <summary>The legibility floor: text contrast under this is illegible.</summary>
    public const double ContrastFloor = 3.0;

    /// <summary>How far a row's or column's next item may overlap the one before it, in CSS pixels.</summary>
    public const double LayoutSlack = 2.0;

    /// <summary>Roles judged for legibility: what a person must read to act or find their way.</summary>
    private static readonly HashSet<string> Legible = new(StringComparer.Ordinal) { "button", "heading" };

    /// <summary>
    /// Roles listed as extras: every role the design asks for anything, plus the controls it has no component for. Rows,
    /// cells, options and prose are sample data or text and are not listed.
    /// </summary>
    private static readonly HashSet<string> Listed = new(
        DesignRoles.Components.Values.Concat(DesignRoles.Inputs.Values)
            .Append(DesignRoles.ScreenTitle).Append(DesignRoles.Heading).Append(DesignRoles.Tab).Append(DesignRoles.ColumnHeader)
            .Where(r => r.Asked).Select(r => r.Role)
            .Concat(["switch", "slider", "menuitem"]),
        StringComparer.Ordinal);

    /// <summary>Checks <paramref name="observation"/> against <paramref name="screen"/>.</summary>
    public static DesignMatchReport Match(DesignScreen screen, DesignObservation observation)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(observation);

        var views = new List<DesignMatchView>();
        foreach (var phase in screen.Phases)
        {
            foreach (var view in phase.Views)
            {
                var seen = observation.Phases.FirstOrDefault(p =>
                    string.Equals(p.Phase, phase.Name, StringComparison.Ordinal)
                    && string.Equals(p.Viewport, view.Viewport, StringComparison.Ordinal));
                var rest = observation.Phases.FirstOrDefault(p =>
                    string.Equals(p.Phase, DesignExpectations.AtRest, StringComparison.Ordinal)
                    && string.Equals(p.Viewport, view.Viewport, StringComparison.Ordinal));

                var notRun = !phase.Drivable ? "nothing on this screen the design shows opens it"
                    : seen is null ? "no observation was made of this phase"
                    : seen.Nodes is null ? seen.NotReached ?? "the phase was not reached"
                    : null;
                views.Add(notRun is not null
                    ? NotRun(phase, view, notRun)
                    : new ViewCheck(phase, view, seen!.Nodes!, phase.Kind == DesignPhaseKind.AtRest ? null : rest?.Nodes).Run());
            }
        }

        return new DesignMatchReport(screen.Index, screen.Anchor, views);
    }

    /// <summary>A name as it is compared: whitespace runs collapsed to one space, trimmed, case folded. Nothing else.</summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var folded = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = folded.Length > 0;
                continue;
            }

            if (space)
            {
                folded.Append(' ');
                space = false;
            }

            folded.Append(char.ToUpperInvariant(c));
        }

        return folded.ToString();
    }

    /// <summary>True when two names are the same under <see cref="Fold"/>.</summary>
    public static bool SameName(string? expected, string? observed) =>
        expected is not null && observed is not null && string.Equals(Fold(expected), Fold(observed), StringComparison.Ordinal);

    private static DesignMatchView NotRun(DesignPhase phase, DesignView view, string reason)
    {
        var findings = view.Expectations
            .Select(e => Finding(DesignFindingKind.NotRun, e, view, reason, $"was not checked: {reason}"))
            .ToArray();
        return new DesignMatchView(phase.Name, view.Viewport, DesignViewOutcome.NotRun, findings, []);
    }

    private static DesignFinding Finding(DesignFindingKind kind, DesignExpectation e, DesignView view, string? observed, string says) =>
        new(kind, e.Role, e.Name, e.Anchor, e.Part, e.Column, e.Phase, view.Viewport, observed,
            string.Create(CultureInfo.InvariantCulture, $"In \"{e.Phase}\"{At(view.Viewport)}, the design's {Describe(e)} {says}."));

    private static string At(string? viewport) => viewport is null ? string.Empty : " at " + viewport;

    /// <summary>How a sentence names an expectation: <c>button "Add person" (add-person)</c>.</summary>
    private static string Describe(DesignExpectation e)
    {
        var what = e.Role is null ? "text" :e.Part == DesignPart.ColumnHeader ? "column header" : e.Role;
        return e.Name is null ? $"{what} ({e.Anchor})" : $"{what} \"{e.Name}\" ({e.Anchor})";
    }

    /// <summary>How an expectation was found.</summary>
    private enum How
    {
        InOrder,
        OutOfOrder,
        Renamed,
        OtherRole,
    }

    /// <summary>One phase at one viewport, over its observation flattened in document order.</summary>
    private sealed class ViewCheck
    {
        private readonly DesignPhase phase;
        private readonly DesignView view;
        private readonly IReadOnlyList<ObservedNode>? rest;
        private readonly List<ObservedNode> nodes = [];
        private readonly List<int> ends = [];
        private readonly bool[] claimed;
        private readonly (int Node, How How)?[] found;

        public ViewCheck(DesignPhase phase, DesignView view, IReadOnlyList<ObservedNode> observed, IReadOnlyList<ObservedNode>? rest)
        {
            this.phase = phase;
            this.view = view;
            this.rest = rest;
            foreach (var node in observed)
            {
                Flatten(node);
            }

            claimed = new bool[nodes.Count];
            found = new (int, How)?[view.Expectations.Count];
        }

        public DesignMatchView Run()
        {
            var findings = new List<DesignFinding>[view.Expectations.Count];
            var cursor = 0;
            for (var k = 0; k < view.Expectations.Count; k++)
            {
                var e = view.Expectations[k];
                findings[k] = [];
                var at = Find(e, cursor, nodes.Count);
                if (at >= 0)
                {
                    Claim(k, at, How.InOrder);
                    cursor = at + 1;
                    continue;
                }

                var before = Find(e, 0, cursor);
                if (before >= 0)
                {
                    Claim(k, before, How.OutOfOrder);
                    findings[k].Add(Finding(
                        DesignFindingKind.OutOfOrder, e, view, null, "is there, but before something the design puts ahead of it"));
                    continue;
                }

                var renamed = InItsPlace(k, cursor);
                if (renamed >= 0)
                {
                    Claim(k, renamed, How.Renamed);
                    cursor = renamed + 1;
                    var name = nodes[renamed].Name;
                    findings[k].Add(Finding(
                        DesignFindingKind.DifferentName, e, view, name,
                        name is null ? "has no name" : $"is named \"{name}\""));
                    continue;
                }

                var other = AsAnotherRole(k, cursor);
                if (other >= 0)
                {
                    Claim(k, other, How.OtherRole);
                    cursor = other + 1;
                    var role = nodes[other].Role;
                    findings[k].Add(Finding(DesignFindingKind.DifferentRole, e, view, role, $"is there as a {role}, not a {e.Role}"));
                    continue;
                }

                findings[k].Add(Finding(DesignFindingKind.Missing, e, view, null, "is missing"));
            }

            for (var k = 0; k < view.Expectations.Count; k++)
            {
                Contained(k, findings[k]);
                Legibility(k, findings[k]);
            }

            var all = findings.SelectMany(f => f).Concat(Layout()).ToArray();
            return new DesignMatchView(
                phase.Name, view.Viewport, all.Length == 0 ? DesignViewOutcome.Matched : DesignViewOutcome.Mismatched, all, Extras());
        }

        private void Flatten(ObservedNode node)
        {
            var index = nodes.Count;
            nodes.Add(node);
            ends.Add(0);
            foreach (var child in node.Children)
            {
                Flatten(child);
            }

            ends[index] = nodes.Count;
        }

        private void Claim(int expectation, int node, How how)
        {
            claimed[node] = true;
            found[expectation] = (node, how);
        }

        /// <summary>The first unclaimed node in [from, to) that is exactly what <paramref name="e"/> asks.</summary>
        private int Find(DesignExpectation e, int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                if (!claimed[i] && Is(e, nodes[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Exactly what <paramref name="e"/> asks: its role and folded name, or, for words, its folded words.</summary>
        private static bool Is(DesignExpectation e, ObservedNode node)
        {
            if (e.Role is null)
            {
                return SameName(e.Name, node.Name) || SameName(e.Name, node.Text);
            }

            return string.Equals(e.Role, node.Role, StringComparison.Ordinal) && (e.Name is null || SameName(e.Name, node.Name));
        }

        /// <summary>
        /// A control of the expectation's role in its place: unclaimed, from the cursor up to where the next later
        /// expectation is found in order, and not itself exactly what a later expectation asks. -1 when there is none or
        /// the design asks words, which have no role to stand in for them.
        /// </summary>
        private int InItsPlace(int k, int cursor)
        {
            var e = view.Expectations[k];
            if (e.Role is null)
            {
                return -1;
            }

            var (later, end) = Window(k, cursor);
            for (var i = cursor; i < end; i++)
            {
                if (!claimed[i] && string.Equals(nodes[i].Role, e.Role, StringComparison.Ordinal) && !later.Any(l => Is(l, nodes[i])))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Backlog 290: an element of the expectation's NAME and another role a person finds or acts on, in the same place
        /// <see cref="InItsPlace"/> looks, and not itself exactly what a later expectation asks. -1 when there is none, or
        /// when the design asks words or gives no name, since then nothing says it is the same thing.
        /// </summary>
        private int AsAnotherRole(int k, int cursor)
        {
            var e = view.Expectations[k];
            if (e.Role is null || e.Name is null)
            {
                return -1;
            }

            var (later, end) = Window(k, cursor);
            for (var i = cursor; i < end; i++)
            {
                if (!claimed[i] && !string.Equals(nodes[i].Role, e.Role, StringComparison.Ordinal) && Listed.Contains(nodes[i].Role)
                    && SameName(e.Name, nodes[i].Name) && !later.Any(l => Is(l, nodes[i])))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Where an expectation not found in order may stand: from the cursor up to where the next later expectation is
        /// found in order, with those later expectations.
        /// </summary>
        private (DesignExpectation[] Later, int End) Window(int k, int cursor)
        {
            var later = view.Expectations.Skip(k + 1).ToArray();
            foreach (var next in later)
            {
                var at = Find(next, cursor, nodes.Count);
                if (at >= 0)
                {
                    return (later, at);
                }
            }

            return (later, nodes.Count);
        }

        private bool Exact(int k, out int node)
        {
            node = found[k] is { How: How.InOrder or How.OutOfOrder } f ? f.Node : -1;
            return node >= 0;
        }

        private void Contained(int k, List<DesignFinding> into)
        {
            var e = view.Expectations[k];
            if (e.Within is not { } within || !Exact(k, out var node))
            {
                return;
            }

            var container = -1;
            for (var c = 0; c < view.Expectations.Count; c++)
            {
                if (c != k && view.Expectations[c].Part == DesignPart.Node
                    && string.Equals(view.Expectations[c].Anchor, within, StringComparison.Ordinal))
                {
                    container = c;
                    break;
                }
            }

            if (container < 0 || !Exact(container, out var outer))
            {
                return;
            }

            if (node <= outer || node >= ends[outer])
            {
                into.Add(Finding(DesignFindingKind.OutsideItsContainer, e, view, within, $"is not inside {within}"));
            }
        }

        private void Legibility(int k, List<DesignFinding> into)
        {
            var e = view.Expectations[k];
            if (e.Role is null || !Legible.Contains(e.Role) || !Exact(k, out var node) || nodes[node].Contrast is not { } ratio
                || ratio >= ContrastFloor)
            {
                return;
            }

            var shown = ratio.ToString("0.00", CultureInfo.InvariantCulture);
            into.Add(Finding(
                DesignFindingKind.Illegible, e, view, shown, $"has text contrast {shown}:1, under the 3:1 floor"));
        }

        /// <summary>
        /// The design's rows and columns, outermost first. An item is placed by the union of the boxes found for its
        /// members, and only when every member was found in order: a moved element necessarily breaks its container's
        /// direction, and saying so twice is noise.
        /// </summary>
        private IEnumerable<DesignFinding> Layout()
        {
            foreach (var relation in view.Relations)
            {
                (DesignRelationItem Item, ObservedBox Box)? before = null;
                foreach (var item in relation.Items)
                {
                    if (Place(item) is not { } box)
                    {
                        continue;
                    }

                    if (before is { } a)
                    {
                        var row = relation.Direction == MockupLayout.Row;
                        var fits = row ? box.X >= a.Box.Right - LayoutSlack : box.Y >= a.Box.Bottom - LayoutSlack;
                        if (!fits)
                        {
                            var e = Representative(item);
                            var what = string.Equals(e.Anchor, item.Anchor, StringComparison.Ordinal)
                                ? Describe(e)
                                : $"{item.Anchor} (holding {Describe(e)})";
                            var says = row
                                ? $"is not right of {a.Item.Anchor} as the design's row {relation.Container} says"
                                : $"is not below {a.Item.Anchor} as the design's column {relation.Container} says";
                            yield return new DesignFinding(
                                DesignFindingKind.NotLaidOutAsDesigned, e.Role, e.Name, item.Anchor, DesignPart.Node, null,
                                e.Phase, view.Viewport, a.Item.Anchor,
                                $"In \"{e.Phase}\"{At(view.Viewport)}, the design's {what} {says}.");
                        }
                    }

                    before = (item, box);
                }
            }
        }

        private ObservedBox? Place(DesignRelationItem item)
        {
            ObservedBox? box = null;
            for (var k = 0; k < view.Expectations.Count; k++)
            {
                if (!item.Members.Contains(view.Expectations[k].Anchor, StringComparer.Ordinal))
                {
                    continue;
                }

                if (found[k] is not { How: How.InOrder } f)
                {
                    return null;
                }

                if (nodes[f.Node].Box is { } b)
                {
                    box = box is null ? b : box.Union(b);
                }
            }

            return box;
        }

        /// <summary>The expectation a layout finding names: the item's own, else the first inside it.</summary>
        private DesignExpectation Representative(DesignRelationItem item) =>
            view.Expectations.FirstOrDefault(e => e.Part == DesignPart.Node && string.Equals(e.Anchor, item.Anchor, StringComparison.Ordinal))
            ?? view.Expectations.First(e => item.Members.Contains(e.Anchor, StringComparer.Ordinal));

        /// <summary>
        /// What the page shows that the design does not name, of a role a person finds or acts on. Outside the at-rest
        /// phase, what the page showed at rest too is left out: a dialog's snapshot still holds the page behind it.
        /// </summary>
        private IReadOnlyList<DesignExtra> Extras()
        {
            var atRest = new HashSet<(string, string)>();
            foreach (var node in Walk(rest ?? []))
            {
                atRest.Add((node.Role, Fold(node.Name ?? string.Empty)));
            }

            var extras = new List<DesignExtra>();
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (!claimed[i] && Listed.Contains(node.Role) && !atRest.Contains((node.Role, Fold(node.Name ?? string.Empty))))
                {
                    extras.Add(new DesignExtra(node.Role, node.Name, node.Ref));
                }
            }

            return extras;
        }

        private static IEnumerable<ObservedNode> Walk(IEnumerable<ObservedNode> roots)
        {
            foreach (var node in roots)
            {
                yield return node;
                foreach (var inner in Walk(node.Children))
                {
                    yield return inner;
                }
            }
        }
    }
}
