using System.Globalization;
using Fravashi.Application.Mockups;

namespace Fravashi.Application.Verification;

/// <summary>What a pinned design asks a built page to contain, screen by screen (backlog 221, plan @45).</summary>
/// <param name="Screens">One entry per screen, in document order.</param>
/// <param name="Unchecked">
/// Every override property that could change what is expected but that no phase applies, in tree order: an <c>at</c>
/// or starting-state override's <c>input</c>, <c>placeholder</c>, <c>a11y</c> or <c>children</c>. The prototype
/// renders none of them either, so applying them would fail the prototype against its own design (ADR 0030 decision
/// 3). Listed rather than silently dropped.
/// </param>
public sealed record DesignExpectationSet(IReadOnlyList<DesignScreen> Screens, IReadOnlyList<DesignUnchecked> Unchecked);

/// <summary>One screen's phases.</summary>
/// <param name="Index">The screen's position in the document.</param>
/// <param name="Anchor">The screen's anchor, spelled as <c>data-mk-anchor</c> is.</param>
/// <param name="Phases">At rest first, then each dialog and later tab in the order they were reached.</param>
public sealed record DesignScreen(int Index, string Anchor, IReadOnlyList<DesignPhase> Phases);

/// <summary>Which moment of a screen a phase is.</summary>
public enum DesignPhaseKind
{
    /// <summary>The screen as it loads.</summary>
    AtRest,

    /// <summary>A dialog some action opens, opened.</summary>
    Dialog,

    /// <summary>A tab panel after the first, its tab chosen.</summary>
    Tab,
}

/// <summary>
/// One moment of a screen and what it reveals (ADR 0030 decision 4). A phase holds ONLY what it reveals: at rest leaves
/// out the dialogs something opens and the later tab panels, a dialog's phase is the dialog and what is in it, and a
/// tab's phase is its panel's contents.
/// </summary>
/// <param name="Name">
/// How a report names it: <c>at rest</c>, <c>add-dialog opened by Add person</c>, <c>two chosen by Two</c> when the
/// panel has an id, <c>Activity tab</c> when it does not (its anchor, a tree pointer, is not a name a person reads).
/// </param>
/// <param name="Kind">Which kind of moment it is.</param>
/// <param name="Target">The anchor of the dialog or panel it reveals; null at rest.</param>
/// <param name="Steps">What a driver does, from the screen at rest, to reach it, in order.</param>
/// <param name="Drivable">
/// False when nothing on the screen the design shows can reach it (a dialog no reachable control opens). Its
/// expectations are still listed, so a check reports them NOT RUN rather than leaving them out.
/// </param>
/// <param name="Views">One per named viewport, in document order; one with a null viewport when the design names none.</param>
public sealed record DesignPhase(
    string Name,
    DesignPhaseKind Kind,
    string? Target,
    IReadOnlyList<DesignStep> Steps,
    bool Drivable,
    IReadOnlyList<DesignView> Views);

/// <summary>What a driver does to one control.</summary>
public enum DesignStepAction
{
    /// <summary>Raise the event whose action opens a dialog.</summary>
    Open,

    /// <summary>Choose a tab.</summary>
    Choose,
}

/// <summary>One thing a driver does to reach a phase: act on the control the design names.</summary>
/// <param name="Action">What it does.</param>
/// <param name="Event">The event the design hangs it off: a list or table's <c>select</c> means choosing a row.</param>
/// <param name="Anchor">The control's design anchor.</param>
/// <param name="Role">The role to find it by in a built page, or null when the design asks it no role.</param>
/// <param name="Name">The accessible name to find it by, or null when the design gives it none.</param>
public sealed record DesignStep(DesignStepAction Action, MockupEvent Event, string Anchor, string? Role, string? Name);

/// <summary>A phase as it should be at one viewport, with that viewport's <c>at</c> applied.</summary>
/// <param name="Viewport">The viewport's name, or null for a design that names none.</param>
/// <param name="Width">Its width in CSS pixels, or null.</param>
/// <param name="Expectations">What a person must find, in the design's order.</param>
/// <param name="Relations">The rows and columns to check, outermost first.</param>
public sealed record DesignView(
    string? Viewport, int? Width, IReadOnlyList<DesignExpectation> Expectations, IReadOnlyList<DesignRelation> Relations);

/// <summary>
/// Which part of a node an expectation is, when it is not the node's own element. Backlog 238 (plan @52): the SAME enum
/// names the part a prototype's overlay anchor addresses (<see cref="Prototypes.PrototypeAnchor.Part"/>), so the verifier
/// and the overlay never name one thing two ways.
/// </summary>
public enum DesignPart
{
    /// <summary>The node's own element.</summary>
    Node,

    /// <summary>A screen's title; for the overlay, a dialog's title too.</summary>
    Title,

    /// <summary>The tab a panel is chosen by.</summary>
    Tab,

    /// <summary>One of a table's column headers; <see cref="DesignExpectation.Column"/> names which.</summary>
    ColumnHeader,

    /// <summary>
    /// A table's, list's or tree's caption. Addressed by the overlay only: the expectations ask for a caption's WORDS on
    /// the node itself (<see cref="Node"/>), so they never emit this part.
    /// </summary>
    Caption,
}

/// <summary>One thing a built page must contain.</summary>
/// <param name="Role">
/// The ARIA role to find (from <see cref="DesignRoles"/>), or null when the design asks for the WORDS
/// <paramref name="Name"/> only, in whatever element (plain text, a container's caption).
/// </param>
/// <param name="Name">The accessible name the design gives it, or null when it gives none (then the role alone is asked).</param>
/// <param name="Anchor">The design node, spelled as <c>data-mk-anchor</c> is: its id, else its tree pointer.</param>
/// <param name="Part">Which part of the node; a title, a tab and a column header share their node's anchor.</param>
/// <param name="Column">A column header's record key; null otherwise.</param>
/// <param name="Phase">The name of the phase that holds it.</param>
/// <param name="Within">
/// The anchor of the asked element that must contain it (the dialog for a dialog's contents, the table for its column
/// headers), or null.
/// </param>
public sealed record DesignExpectation(
    string? Role, string? Name, string Anchor, DesignPart Part, string? Column, string Phase, string? Within);

/// <summary>
/// A <c>row</c>'s children must sit left to right, a <c>column</c>'s top to bottom (ADR 0030 decision 5). Only children
/// the phase shows and that hold at least one expectation are listed, and only when there are two or more.
/// </summary>
/// <param name="Container">The laid out node's anchor.</param>
/// <param name="Direction">Row or column, with the viewport's <c>at</c> applied.</param>
/// <param name="Items">Its children, in order.</param>
public sealed record DesignRelation(string Container, MockupLayout Direction, IReadOnlyList<DesignRelationItem> Items);

/// <summary>One child in a relation.</summary>
/// <param name="Anchor">The child's anchor.</param>
/// <param name="Members">
/// The anchors of the expectations inside it, itself included, in order: a child that is only a container is placed by
/// the box of what was found for these.
/// </param>
public sealed record DesignRelationItem(string Anchor, IReadOnlyList<string> Members);

/// <summary>An override property no phase applies.</summary>
/// <param name="Anchor">The node's anchor.</param>
/// <param name="Viewport">The viewport whose <c>at</c> carries it, or null for a starting state's.</param>
/// <param name="State">The starting state whose override carries it, or null for a viewport's.</param>
/// <param name="Property">The property, spelled as the schema spells it.</param>
public sealed record DesignUnchecked(string Anchor, string? Viewport, string? State, string Property);

/// <summary>
/// Turns a design tree into what a built page must contain (backlog 221, ADR 0030 decisions 2, 4 and 5, plan @45).
/// Pure and deterministic: the same tree always gives the same answer, in the same order. Sample data (data sets,
/// items, rows, a field's value, options) is never expected, column headers are; a node whose starting state is
/// <c>hidden</c> is not expected, with everything in it; nothing about the look is. Roles come from
/// <see cref="DesignRoles"/> only.
/// </summary>
public static class DesignExpectations
{
    /// <summary>The name of every screen's first phase.</summary>
    public const string AtRest = "at rest";

    /// <summary>What <paramref name="document"/> asks a built page to contain.</summary>
    public static DesignExpectationSet For(MockupDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var opened = new HashSet<string>(StringComparer.Ordinal);
        for (var s = 0; s < document.Screens.Count; s++)
        {
            foreach (var place in MockupView.Walk(document.Screens[s], ScreenPath(s)))
            {
                foreach (var action in place.Node.On?.Values ?? [])
                {
                    if (action.Open is { } dialog)
                    {
                        opened.Add(dialog);
                    }
                }
            }
        }

        var screens = new List<DesignScreen>();
        for (var s = 0; s < document.Screens.Count; s++)
        {
            screens.Add(new ScreenPass(document, document.Screens[s], ScreenPath(s), s, opened).Run());
        }

        return new DesignExpectationSet(screens, Unchecked(document));
    }

    private static string ScreenPath(int index) => "/screens/" + index.ToString(CultureInfo.InvariantCulture);

    private static string ChildPath(string parent, int index) =>
        parent + "/children/" + index.ToString(CultureInfo.InvariantCulture);

    private static string AnchorOf(MockupNode node, string path) => node.Id ?? path;

    /// <summary>
    /// A node at a viewport in its starting state, with only what the renderer expresses applied: text, layout, cols and
    /// a table's columns (plan @45), the viewport first and the state on top, as <c>MockupView.Resolve</c> orders them.
    /// </summary>
    private static MockupNode Resolved(MockupNode node, string? viewport)
    {
        var resolved = node;
        if (viewport is not null && node.At is { } at && at.TryGetValue(viewport, out var wide))
        {
            resolved = Expressed(resolved, wide);
        }

        if (node.State is { } state && node.States is { } states && states.TryGetValue(state, out var look))
        {
            resolved = Expressed(resolved, look);
        }

        return resolved;
    }

    private static MockupNode Expressed(MockupNode node, MockupNode over) => node with
    {
        Text = over.Text ?? node.Text,
        Layout = over.Layout ?? node.Layout,
        Cols = over.Cols ?? node.Cols,
        Columns = Kept(node.Columns, over.Columns),
    };

    /// <summary>
    /// A table's columns under an override's: the node's own columns whose key the override keeps, in the node's order
    /// and with the node's headings, which is exactly what the renderer shows (it hides the dropped ones by key and
    /// never writes a heading of the override's).
    /// </summary>
    private static IReadOnlyList<MockupColumn>? Kept(IReadOnlyList<MockupColumn>? columns, IReadOnlyList<MockupColumn>? over)
    {
        if (columns is null || over is null)
        {
            return columns;
        }

        var keys = over.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        return columns.Where(c => keys.Contains(c.Key)).ToArray();
    }

    private static bool HiddenAtRest(MockupNode node) => node.State == MockupState.Hidden;

    private static IReadOnlyList<DesignUnchecked> Unchecked(MockupDocument document)
    {
        var found = new List<DesignUnchecked>();
        for (var s = 0; s < document.Screens.Count; s++)
        {
            foreach (var place in Places(document.Screens[s], ScreenPath(s)))
            {
                var anchor = AnchorOf(place.Node, place.Path);
                foreach (var viewport in document.Viewports)
                {
                    if (place.Node.At is { } at && at.TryGetValue(viewport.Name, out var over))
                    {
                        found.AddRange(Unapplied(over).Select(p => new DesignUnchecked(anchor, viewport.Name, null, p)));
                    }
                }

                if (place.Node.State is { } state && place.Node.States is { } states && states.TryGetValue(state, out var look))
                {
                    found.AddRange(Unapplied(look).Select(p => new DesignUnchecked(anchor, null, MockupVocabulary.Name(state), p)));
                }
            }
        }

        return found;
    }

    /// <summary>The override properties that would change a role, a name or which elements exist, and are not applied.</summary>
    private static IEnumerable<string> Unapplied(MockupNode over)
    {
        if (over.Input is not null)
        {
            yield return "input";
        }

        if (over.Placeholder is not null)
        {
            yield return "placeholder";
        }

        if (over.A11y is not null)
        {
            yield return "a11y";
        }

        if (over.Children is not null)
        {
            yield return "children";
        }
    }

    /// <summary>Every node under <paramref name="root"/>, in document order, with its pointer path.</summary>
    private static IEnumerable<(MockupNode Node, string Path)> Places(MockupNode root, string path)
    {
        yield return (root, path);
        if (root.Children is not { } children)
        {
            yield break;
        }

        for (var i = 0; i < children.Count; i++)
        {
            foreach (var place in Places(children[i], ChildPath(path, i)))
            {
                yield return place;
            }
        }
    }

    /// <summary>One phase while it is being worked out: what reveals it and which nodes it shows.</summary>
    private sealed class Pending(
        string name, DesignPhaseKind kind, MockupNode root, string path, IReadOnlyList<DesignStep> steps, bool drivable)
    {
        public string Name { get; } = name;
        public DesignPhaseKind Kind { get; } = kind;
        public MockupNode Root { get; } = root;
        public string Path { get; } = path;
        public IReadOnlyList<DesignStep> Steps { get; } = steps;
        public bool Drivable { get; } = drivable;
        public List<(MockupNode Node, string Path)> Shown { get; } = [];
    }

    /// <summary>A dialog or later tab panel found while walking a phase, not yet given a phase of its own.</summary>
    private sealed record Deferred(MockupNode Node, string Path, int Index, Pending From);

    /// <summary>One screen: its phases as a fixed point over what each phase reveals.</summary>
    private sealed class ScreenPass(
        MockupDocument document, MockupNode screen, string path, int index, IReadOnlySet<string> opened)
    {
        private readonly List<Pending> phases = [];
        private readonly List<Deferred> dialogs = [];
        private readonly List<Deferred> panels = [];

        public DesignScreen Run()
        {
            Add(new Pending(AtRest, DesignPhaseKind.AtRest, screen, path, [], true));

            while (dialogs.Count > 0 || panels.Count > 0)
            {
                var progress = false;
                foreach (var dialog in dialogs.ToArray())
                {
                    if (Opener(dialog) is { } opener)
                    {
                        dialogs.Remove(dialog);
                        Add(DialogPhase(dialog, opener.Phase, opener.Step));
                        progress = true;
                    }
                }

                foreach (var panel in panels.ToArray())
                {
                    panels.Remove(panel);
                    Add(TabPhase(panel));
                    progress = true;
                }

                if (!progress)
                {
                    // Nothing shown on this screen opens it: listed, so a check reports it NOT RUN (ADR 0030 decision 4).
                    var lost = dialogs[0];
                    dialogs.RemoveAt(0);
                    var anchor = AnchorOf(lost.Node, lost.Path);
                    Add(new Pending(
                        $"{anchor} with nothing on this screen to open it", DesignPhaseKind.Dialog, lost.Node, lost.Path, [], false));
                }
            }

            return new DesignScreen(index, AnchorOf(screen, path), phases.Select(Finish).ToArray());
        }

        private void Add(Pending phase)
        {
            phases.Add(phase);
            Reveal(phase, phase.Root, phase.Path, true);
        }

        /// <summary>Records what a phase shows and defers what it does not reveal itself.</summary>
        private void Reveal(Pending phase, MockupNode node, string at, bool root)
        {
            if (HiddenAtRest(node))
            {
                return;
            }

            if (!root && node.Type == MockupComponent.Dialog && node.Id is { } id && opened.Contains(id))
            {
                dialogs.Add(new Deferred(node, at, 0, phase));
                return;
            }

            phase.Shown.Add((node, at));
            var children = node.Children ?? [];
            for (var i = 0; i < children.Count; i++)
            {
                if (node.Type == MockupComponent.Tabs && i > 0)
                {
                    if (!HiddenAtRest(children[i]))
                    {
                        panels.Add(new Deferred(children[i], ChildPath(at, i), i, phase));
                    }

                    continue;
                }

                Reveal(phase, children[i], ChildPath(at, i), false);
            }
        }

        /// <summary>The first node, in phase order then tree order, whose action opens the dialog.</summary>
        private (Pending Phase, DesignStep Step)? Opener(Deferred dialog)
        {
            var id = dialog.Node.Id!;
            foreach (var phase in phases)
            {
                foreach (var (node, at) in phase.Shown)
                {
                    foreach (var @event in Enum.GetValues<MockupEvent>())
                    {
                        if (node.On is { } on && on.TryGetValue(@event, out var action)
                            && string.Equals(action.Open, id, StringComparison.Ordinal))
                        {
                            var role = DesignRoles.Of(node);
                            return (phase, new DesignStep(
                                DesignStepAction.Open, @event, AnchorOf(node, at),
                                role.Asked ? role.Role : null, role.Asked ? NameOf(Resolved(node, null)) : null));
                        }
                    }
                }
            }

            return null;
        }

        private static Pending DialogPhase(Deferred dialog, Pending from, DesignStep step) =>
            new(
                $"{AnchorOf(dialog.Node, dialog.Path)} opened by {step.Name ?? step.Anchor}",
                DesignPhaseKind.Dialog,
                dialog.Node,
                dialog.Path,
                [.. from.Steps, step],
                from.Drivable);

        private Pending TabPhase(Deferred panel)
        {
            var anchor = AnchorOf(panel.Node, panel.Path);
            var tab = Resolved(panel.Node, null).Text;
            var step = new DesignStep(DesignStepAction.Choose, MockupEvent.Click, anchor, DesignRoles.Tab.Role, tab);
            var by = tab ?? "tab " + (panel.Index + 1).ToString(CultureInfo.InvariantCulture);

            // A node with no id has no name a person reads in its anchor (the anchor is its tree pointer), so the
            // phase's NAME is the tab a person chose instead; the anchor stays the tree pointer wherever one is
            // needed. The anchor form is always unique (an id or a tree pointer, never shared), the readable form
            // is not (two tab groups can each have an id-less panel named "Details"), so a readable name that would
            // collide with a phase already on this screen falls back to the anchor form instead.
            var readable = tab is not null ? $"{tab} tab" : by;
            var name = panel.Node.Id is null && !phases.Exists(p => string.Equals(p.Name, readable, StringComparison.Ordinal))
                ? readable
                : $"{anchor} chosen by {by}";
            return new Pending(
                name, DesignPhaseKind.Tab, panel.Node, panel.Path, [.. panel.From.Steps, step], panel.From.Drivable);
        }

        private DesignPhase Finish(Pending phase)
        {
            IReadOnlyList<(string? Name, int? Width)> viewports = document.Viewports.Count > 0
                ? document.Viewports.Select(v => ((string?)v.Name, (int?)v.Width)).ToArray()
                : [(null, null)];
            var views = viewports.Select(v =>
            {
                var view = new ViewPass(phase, v.Name, opened);
                view.Emit(phase.Root, phase.Path, true, null, phase.Kind == DesignPhaseKind.Tab);
                return new DesignView(v.Name, v.Width, view.Expectations, view.Relations);
            }).ToArray();

            var target = phase.Kind == DesignPhaseKind.AtRest ? null : AnchorOf(phase.Root, phase.Path);
            return new DesignPhase(phase.Name, phase.Kind, target, phase.Steps, phase.Drivable, views);
        }
    }

    /// <summary>
    /// The accessible name of a node's own element, with its viewport and starting state already resolved: the rules
    /// plan @45 records, each the one the renderer's markup gives under accname.
    /// </summary>
    private static string? NameOf(MockupNode node) => node.Type switch
    {
        MockupComponent.Field => node.A11y?.Label ?? node.Text ?? node.Placeholder,
        MockupComponent.List or MockupComponent.Tree or MockupComponent.Image => node.A11y?.Label,
        _ => node.A11y?.Label ?? node.Text,
    };

    /// <summary>One phase at one viewport.</summary>
    private sealed class ViewPass(Pending phase, string? viewport, IReadOnlySet<string> opened)
    {
        public List<DesignExpectation> Expectations { get; } = [];
        public List<DesignRelation> Relations { get; } = [];

        /// <summary>
        /// Emits what <paramref name="node"/> and everything in it ask for, and returns the anchors emitted. A panel that
        /// is a tab's content does not repeat its text: that is its tab's name, expected where the tabs are.
        /// </summary>
        public List<string> Emit(MockupNode node, string at, bool root, string? within, bool tabPanel)
        {
            var members = new List<string>();
            if (HiddenAtRest(node) || (!root && node.Type == MockupComponent.Dialog && node.Id is { } id && opened.Contains(id)))
            {
                return members;
            }

            var resolved = Resolved(node, viewport);
            var anchor = AnchorOf(node, at);
            var role = DesignRoles.Of(node);
            var inner = within;

            switch (node.Type)
            {
                case MockupComponent.Window:
                    if (resolved.Text is { } title)
                    {
                        Expect(members, DesignRoles.ScreenTitle.Role, title, anchor, DesignPart.Title, null, within);
                    }

                    break;

                case MockupComponent.Panel or MockupComponent.Container or MockupComponent.Toolbar or MockupComponent.Form:
                    if (!tabPanel && resolved.Text is { } caption)
                    {
                        Expect(members, null, caption, anchor, DesignPart.Node, null, within);
                    }

                    break;

                case MockupComponent.Dialog:
                    Expect(members, role.Role, NameOf(resolved), anchor, DesignPart.Node, null, within);
                    inner = anchor;
                    break;

                case MockupComponent.Tabs:
                    Tabs(node, at, within, members);
                    return members;

                case MockupComponent.List or MockupComponent.Tree:
                    Expect(members, role.Role, NameOf(resolved), anchor, DesignPart.Node, null, within);
                    if (resolved.Text is { } listCaption)
                    {
                        Expect(members, null, listCaption, anchor, DesignPart.Node, null, within);
                    }

                    break;

                case MockupComponent.Table:
                    Expect(members, role.Role, NameOf(resolved), anchor, DesignPart.Node, null, within);
                    foreach (var column in resolved.Columns ?? [])
                    {
                        Expect(members, DesignRoles.ColumnHeader.Role, column.Text ?? column.Key, anchor, DesignPart.ColumnHeader, column.Key, anchor);
                    }

                    break;

                case MockupComponent.Text when !role.Asked:
                    if (resolved.Text is { } words)
                    {
                        Expect(members, null, words, anchor, DesignPart.Node, null, within);
                    }

                    break;

                case MockupComponent.Image when node.A11y?.Decorative is true:
                    break;

                default:
                    Expect(members, role.Role, NameOf(resolved), anchor, DesignPart.Node, null, within);
                    break;
            }

            Children(resolved, node, at, inner, members);
            return members;
        }

        private void Tabs(MockupNode node, string at, string? within, List<string> members)
        {
            var panels = node.Children ?? [];
            for (var i = 0; i < panels.Count; i++)
            {
                var panelResolved = Resolved(panels[i], viewport);
                Expect(members, DesignRoles.Tab.Role, panelResolved.Text, AnchorOf(panels[i], ChildPath(at, i)), DesignPart.Tab, null, within);
            }

            if (panels.Count > 0)
            {
                members.AddRange(Emit(panels[0], ChildPath(at, 0), false, within, true));
            }
        }

        /// <summary>Walks the children and, for a row or a column with two or more placed children, records the relation.</summary>
        private void Children(MockupNode resolved, MockupNode node, string at, string? within, List<string> members)
        {
            var slot = Relations.Count;
            var items = new List<DesignRelationItem>();
            var children = node.Children ?? [];
            for (var i = 0; i < children.Count; i++)
            {
                var childPath = ChildPath(at, i);
                var inside = Emit(children[i], childPath, false, within, false);
                members.AddRange(inside);
                if (inside.Count > 0)
                {
                    items.Add(new DesignRelationItem(AnchorOf(children[i], childPath), inside.Distinct(StringComparer.Ordinal).ToArray()));
                }
            }

            if (resolved.Layout is MockupLayout.Row or MockupLayout.Column && items.Count >= 2)
            {
                Relations.Insert(slot, new DesignRelation(AnchorOf(node, at), resolved.Layout.Value, items));
            }
        }

        private void Expect(
            List<string> members, string? role, string? name, string anchor, DesignPart part, string? column, string? within)
        {
            Expectations.Add(new DesignExpectation(role, name, anchor, part, column, phase.Name, within));
            members.Add(anchor);
        }
    }
}
