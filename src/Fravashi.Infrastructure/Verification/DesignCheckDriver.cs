using System.Globalization;
using Fravashi.Application.Mockups;
using Fravashi.Application.Verification;

namespace Fravashi.Infrastructure.Verification;

/// <summary>What driving one screen on one side produced.</summary>
/// <param name="Observation">Every phase at every viewport attempted, reached or not.</param>
/// <param name="Screenshots">One per phase and viewport whose page loaded, in the order taken.</param>
/// <param name="Console">What the page complained about, each entry prefixed with where, capped.</param>
public sealed record DesignDrive(
    DesignObservation Observation, IReadOnlyList<DesignCheckScreenshot> Screenshots, IReadOnlyList<string> Console);

/// <summary>
/// Drives one design screen's phases on one page and reads what it shows (backlog 223, ADR 0030 decision 4, plan @47).
/// For each viewport the design names and each phase in the design's order: a FRESH load, the phase's steps, then the
/// observation, so a dialog one phase opened never colours the next. A step's control is found in the page's own aria
/// snapshot by role and name through <see cref="DesignMatcher.SameName"/>, the first in document order, and acted on by
/// its reference, so the drive and the judge read the same tree with the same folding. A control that is not there makes
/// the phase NOT REACHED, and the drive carries on with the next one. No Playwright type is used here.
/// </summary>
public static class DesignCheckDriver
{
    /// <summary>The viewport height every phase is read at; the design names widths only.</summary>
    public const int ViewportHeight = 800;

    /// <summary>The width of the one view a design with no named viewport is read at.</summary>
    public const int BaseWidth = 1280;

    /// <summary>The most console entries kept per side and screen.</summary>
    public const int MaxConsole = 200;

    /// <summary>The longest a console entry is kept.</summary>
    public const int MaxConsoleLength = 300;

    /// <summary>Roles not measured: rows, cells and options are sample data, generics and bare text are layout and prose.</summary>
    private static readonly HashSet<string> Unmeasured = new(StringComparer.Ordinal)
    {
        "row", "rowgroup", "cell", "generic", "option", "text",
    };

    /// <summary>Roles whose visible text is judged for legibility (ADR 0030 decision 5).</summary>
    private static readonly HashSet<string> Read = new(StringComparer.Ordinal) { "button", "heading" };

    /// <summary>Controls a change is made to by clicking them.</summary>
    private static readonly HashSet<string> Toggles = new(StringComparer.Ordinal) { "checkbox", "radio", "switch" };

    /// <summary>
    /// Drives <paramref name="screen"/> on <paramref name="page"/>. <paramref name="budget"/> ends the drive's time: what
    /// is still to do when it fires is not reached, with <paramref name="budgetSpent"/> as the reason. Cancelling
    /// <paramref name="cancellationToken"/> abandons the drive.
    /// </summary>
    public static async Task<DesignDrive> DriveAsync(
        IDesignPage page,
        DesignScreen screen,
        CancellationToken budget,
        string budgetSpent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(budgetSpent);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(budget, cancellationToken);
        var phases = new List<ObservedPhase>();
        var shots = new List<DesignCheckScreenshot>();
        var console = new List<string>();
        var spent = false;

        var viewports = screen.Phases.Count == 0 ? [] : screen.Phases[0].Views.Select(v => (v.Viewport, v.Width)).ToArray();
        foreach (var (viewport, width) in viewports)
        {
            var sized = false;
            for (var p = 0; p < screen.Phases.Count; p++)
            {
                var phase = screen.Phases[p];
                if (!phase.Drivable)
                {
                    // The design itself cannot reach it; the matcher says so.
                    continue;
                }

                if (spent)
                {
                    phases.Add(new ObservedPhase(phase.Name, viewport, width, null, budgetSpent));
                    continue;
                }

                var where = string.Create(
                    CultureInfo.InvariantCulture, $"[screen {screen.Index}, {phase.Name}, {viewport ?? "base"}]");
                try
                {
                    if (!sized)
                    {
                        await page.SetViewportAsync(width ?? BaseWidth, ViewportHeight, linked.Token).ConfigureAwait(false);
                        sized = true;
                    }

                    var (observed, png) = await ObserveAsync(page, phase, viewport, width, linked.Token).ConfigureAwait(false);
                    phases.Add(observed);
                    if (png is not null)
                    {
                        shots.Add(new DesignCheckScreenshot(
                            DesignCheckScreenshot.KeyFor(screen.Index, viewport, p, phase.Name), screen.Index, phase.Name, viewport, png));
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    spent = true;
                    phases.Add(new ObservedPhase(phase.Name, viewport, width, null, budgetSpent));
                }
                finally
                {
                    Keep(console, where, page.TakeConsole());
                }
            }
        }

        return new DesignDrive(new DesignObservation(screen.Index, phases), shots, console);
    }

    /// <summary>One phase at one viewport: load, steps, observation, picture. A page failure is the phase's reason.</summary>
    private static async Task<(ObservedPhase Phase, byte[]? Png)> ObserveAsync(
        IDesignPage page, DesignPhase phase, string? viewport, int? width, CancellationToken cancellationToken)
    {
        var loaded = false;
        string? notReached = null;
        IReadOnlyList<ObservedNode>? nodes = null;
        try
        {
            await page.LoadAsync(cancellationToken).ConfigureAwait(false);
            await page.SettleAsync(false, cancellationToken).ConfigureAwait(false);
            loaded = true;

            for (var s = 0; s < phase.Steps.Count && notReached is null; s++)
            {
                var step = phase.Steps[s];
                var tree = AriaSnapshot.Parse(await page.SnapshotAsync(cancellationToken).ConfigureAwait(false));
                var (reference, act, why) = Plan(step, tree);
                if (why is not null)
                {
                    notReached = why;
                    break;
                }

                await page.ActAsync(reference!, act, cancellationToken).ConfigureAwait(false);
                var last = s == phase.Steps.Count - 1;
                await page.SettleAsync(last && phase.Kind == DesignPhaseKind.Dialog, cancellationToken).ConfigureAwait(false);
            }

            if (notReached is null)
            {
                var tree = AriaSnapshot.Parse(await page.SnapshotAsync(cancellationToken).ConfigureAwait(false));
                nodes = await ProbeAsync(page, tree, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (DesignPageException ex)
        {
            notReached = ex.Message;
        }
        catch (FormatException ex)
        {
            notReached = "the page's outline could not be read: " + ex.Message;
        }

        byte[]? png = null;
        if (loaded)
        {
            try
            {
                // Taken where the drive stopped too: a picture of the page whose opener was not found is the evidence.
                png = await page.ScreenshotAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DesignPageException)
            {
                // A missing picture is not a finding; the observation stands.
            }
        }

        var observed = nodes is not null
            ? new ObservedPhase(phase.Name, viewport, width, nodes, null)
            : new ObservedPhase(phase.Name, viewport, width, null, notReached ?? "the phase was not reached");
        return (observed, png);
    }

    /// <summary>
    /// Which element to act on for <paramref name="step"/> and how, or why it cannot be: the first element in document
    /// order of the step's role whose name folds equal to the step's (any name when the step gives none; the words, in
    /// a name or inline text, when the step has no role). An element printed without a reference is acted on through
    /// its nearest ancestor that has one.
    /// </summary>
    public static (string? Reference, DesignPageAct Act, string? NotReached) Plan(DesignStep step, IReadOnlyList<ObservedNode> tree)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(tree);

        if (step.Role is null && step.Name is null)
        {
            return (null, DesignPageAct.Click, $"{step.Anchor} has no role or name a page can be searched for");
        }

        var found = Find(tree, [], n => step.Role is null
            ? DesignMatcher.SameName(step.Name, n.Name) || DesignMatcher.SameName(step.Name, n.Text)
            : string.Equals(n.Role, step.Role, StringComparison.Ordinal) && (step.Name is null || DesignMatcher.SameName(step.Name, n.Name)));
        if (found is not { } control)
        {
            return (null, DesignPageAct.Click, Describe(step) + " was not found");
        }

        if (step.Action == DesignStepAction.Open && step.Event == MockupEvent.Select)
        {
            var row = Find(control.Node.Children, [control.Node, .. control.Ancestors], n =>
                n.Role is "listitem" or "treeitem" or "option" || (n.Role == "row" && Holds(n, "cell")));
            return row is { } chosen && ReferenceOf(chosen) is { } at
                ? (at, DesignPageAct.Click, null)
                : (null, DesignPageAct.Click, Describe(step) + " has no row to choose");
        }

        var act = step.Action == DesignStepAction.Open && step.Event == MockupEvent.Change
            ? Toggles.Contains(control.Node.Role) ? DesignPageAct.Click
                : control.Node.Role == "combobox" ? DesignPageAct.ChooseSecondOption
                : DesignPageAct.Type
            : DesignPageAct.Click;
        return ReferenceOf(control) is { } reference
            ? (reference, act, null)
            : (null, act, Describe(step) + " was found but the page gave it nothing to act on");
    }

    /// <summary>How a step's control is named in a reason, spelled as #222's recordings spell it.</summary>
    public static string Describe(DesignStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var name = step.Name is null ? string.Empty : $" \"{step.Name}\"";
        var role = step.Role ?? "the words";
        return $"{role}{name} ({step.Anchor})";
    }

    private sealed record Place(ObservedNode Node, IReadOnlyList<ObservedNode> Ancestors);

    private static Place? Find(IReadOnlyList<ObservedNode> nodes, IReadOnlyList<ObservedNode> ancestors, Func<ObservedNode, bool> wanted)
    {
        foreach (var node in nodes)
        {
            if (wanted(node))
            {
                return new Place(node, ancestors);
            }

            if (Find(node.Children, [node, .. ancestors], wanted) is { } inside)
            {
                return inside;
            }
        }

        return null;
    }

    private static bool Holds(ObservedNode node, string role) =>
        node.Children.Any(c => c.Role == role || Holds(c, role));

    private static string? ReferenceOf(Place place) =>
        place.Node.Ref ?? place.Ancestors.Select(a => a.Ref).FirstOrDefault(r => r is not null);

    /// <summary>
    /// Measures what the matcher judges: the box of every element that is not a row, cell, option, generic or bare text
    /// (the recording's rule, plan @46 section 4), the contrast of every button and heading, and the name the AI-mode
    /// snapshot left out (an element it printed unnamed that is not one of those).
    /// </summary>
    private static async Task<IReadOnlyList<ObservedNode>> ProbeAsync(
        IDesignPage page, IReadOnlyList<ObservedNode> nodes, CancellationToken cancellationToken)
    {
        var probed = new List<ObservedNode>(nodes.Count);
        foreach (var node in nodes)
        {
            var measured = !Unmeasured.Contains(node.Role);
            var ask = new DesignProbeAsk(measured, Read.Contains(node.Role), measured && node.Name is null);
            var probe = node.Ref is { } reference && ask.Any
                ? await page.ProbeAsync(reference, node.Role, ask, cancellationToken).ConfigureAwait(false)
                : null;
            var children = await ProbeAsync(page, node.Children, cancellationToken).ConfigureAwait(false);
            if (probe is null)
            {
                probed.Add(node with { Children = children });
                continue;
            }

            probed.Add(node with
            {
                Name = node.Name ?? probe.Name,
                Children = children,
                Box = probe.Box,
                Contrast = probe.Contrast,
            });
        }

        return probed;
    }

    private static void Keep(List<string> console, string where, IReadOnlyList<string> said)
    {
        foreach (var entry in said)
        {
            if (console.Count >= MaxConsole)
            {
                return;
            }

            var line = where + " " + entry;
            console.Add(line.Length <= MaxConsoleLength ? line : line[..MaxConsoleLength]);
        }
    }
}
