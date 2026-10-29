using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Application.Verification;

/// <summary>What the design phase of a scenario run produced (backlog 224).</summary>
/// <param name="Steps">One result per design screen, appended after the scenario's own steps; empty when nothing applies.</param>
/// <param name="ArtefactRef">Where the report, consoles, logs and screenshots were kept, or null when nothing was kept.</param>
/// <param name="Sentence">What the run's summary adds about the design, or null.</param>
/// <param name="Details">What the evidence's details carry under <c>design</c>.</param>
public sealed record DesignVerificationResult(
    IReadOnlyList<StepResult> Steps, string? ArtefactRef, string? Sentence, JsonNode Details);

/// <summary>
/// The design phase of a scenario run (backlog 224, ADR 0030, plan @63). When a scenario that uses the application runs
/// on an item with an approved design, each screen of the PINNED design (<see cref="DesignApproval.ApprovedFor"/>, never
/// the document's current version) becomes one more step of the run, checked by <see cref="IDesignCheck"/> against the
/// run's own application and its prototype, side by side.
/// <para>
/// <b>Which page shows which screen.</b> The design names screens, not routes, so the scenario's own commands say which
/// pages it reaches: every <c>$VERIFY_APP_URL</c> with the path after it. A screen is the reached path whose last segment
/// folds to its id or its title; the FIRST screen falls back to the application's root, which every such scenario reaches.
/// Any other screen without a page is NOT RUN, never passed. A target is always the run's own address plus a path, so the
/// host never changes, and the check refuses anything its browser policy refuses before it opens anything.
/// </para>
/// <para>
/// <b>The rule per screen</b> is #55's: FAILED when the build differs from the design in anything but a phase it could not
/// reach, or its console has an entry; NOT RUN when a phase could not be driven, the prototype itself did not match (the
/// checker would disagree with its own renderer, ADR 0030 decision 3), or nothing could be checked; PASSED only when every
/// phase at every viewport matched and the console stayed empty. The application's logs are kept, not judged: a user's
/// application has no log grammar the studio could read an error from.
/// </para>
/// </summary>
public sealed partial class DesignVerification(DesignApproval approval, IDesignCheck check, IVerificationArtifacts artifacts)
{
    /// <summary>What the details say about an item with no approved design.</summary>
    public const string NoDesign = "This item has no approved design, so the application was not checked against one.";

    /// <summary>The prefix of a design step's id; the rest is the screen's id, or its index when it has none.</summary>
    public const string StepPrefix = "design:";

    /// <summary>How many findings a step's detail names before it points at the report.</summary>
    public const int FindingsInDetail = 6;

    /// <summary>The design phase of one run. <paramref name="blocked"/> is why nothing may run (an earlier step failed, no application).</summary>
    /// <param name="item">The work item, whose approved design is checked.</param>
    /// <param name="applicationUrl">The run's application address, or null when there is none.</param>
    /// <param name="steps">The scenario's own steps, whose commands say which pages it reaches.</param>
    /// <param name="blocked">Why the check may not run, or null.</param>
    /// <param name="logs">Reads the application's logs once the check is done.</param>
    /// <param name="cancellationToken">Abandons the check.</param>
    /// <param name="marker">Backlog 226: the run's window marker, passed to the check unchanged, or null.</param>
    public async Task<DesignVerificationResult> RunAsync(
        WorkItem item,
        string? applicationUrl,
        IReadOnlyList<ScenarioStep> steps,
        string? blocked,
        Func<CancellationToken, Task<string?>> logs,
        CancellationToken cancellationToken = default,
        WindowMarker? marker = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(logs);

        var design = await approval.ApprovedFor(item, cancellationToken).ConfigureAwait(false);
        if (design is null)
        {
            // Said, never failed: an item nobody drew a design for runs exactly as it did before.
            return new DesignVerificationResult([], null, null, JsonValue.Create(NoDesign)!);
        }

        var named = string.Create(CultureInfo.InvariantCulture, $"design @{design.Number} \"{design.Title}\" at version {design.Version}");
        var read = DesignMockup.Read(design.Body);
        if (!read.IsValid)
        {
            var why = $"The approved {named} holds no tree the check can read: "
                + string.Join("; ", read.Errors.Take(3).Select(e => e.ToString()));
            return new DesignVerificationResult(
                [new StepResult(StepPrefix + "tree", StepOutcome.NotRun, why)],
                null,
                $"The {named} was not checked: its tree could not be read.",
                DesignNode(design.Number, design.Title, design.Version, null, why, []));
        }

        var tree = read.Tree!.Document!;
        var reached = ReachedPaths(steps);
        var screens = tree.Screens.Select((screen, index) => (Screen: screen, Index: index, Path: PathFor(screen, index, reached))).ToList();

        string? notChecked = blocked
            ?? (applicationUrl is null ? "The run has no application to check." : null)
            ?? check.UnavailableReason;

        var targets = applicationUrl is null
            ? []
            : screens.Where(s => s.Path is not null)
                .Select(s => new DesignCheckTarget(s.Index, applicationUrl.TrimEnd('/') + s.Path))
                .ToList();

        DesignCheckResult? result = null;
        if (notChecked is null && targets.Count > 0)
        {
            result = await check.CheckAsync(new DesignCheckRequest(tree, targets) { Marker = marker }, cancellationToken).ConfigureAwait(false);
            notChecked = result.Refusal is { } refusal ? "The check did not run: " + refusal : null;
        }

        var outcomes = new List<ScreenOutcome>();
        foreach (var (screen, index, path) in screens)
        {
            var label = Label(screen, index);
            var checkedScreen = result?.Screens.FirstOrDefault(s => s.Screen == index);
            outcomes.Add(path is null
                ? new ScreenOutcome(index, screen, label, null, StepOutcome.NotRun,
                    $"No page the scenario reaches is named for screen {label}, so it was not checked. Reach it in a step as "
                    + $"\"$VERIFY_APP_URL/{Fold(screen.Id ?? screen.Text ?? string.Empty)}\".", [], 0)
                : notChecked is not null || checkedScreen is null
                    ? new ScreenOutcome(index, screen, label, null, StepOutcome.NotRun,
                        notChecked ?? "The check returned nothing for this screen.", [], 0)
                    : Judge(index, screen, label, checkedScreen));
        }

        if (marker is not null)
        {
            // Backlog 226: what the evidence's details carry withholds the run's token too.
            outcomes = [.. outcomes.Select(o => o with { Detail = marker.Withhold(o.Detail), Findings = [.. o.Findings.Select(marker.Withhold)] })];
        }

        string? reference = null;
        if (result is { Refusal: null })
        {
            var logText = await logs(cancellationToken).ConfigureAwait(false);
            var kept = Artifacts(named, outcomes, result, logText);
            if (marker is not null)
            {
                // Backlog 226: a run's window token is in no evidence file, whatever a page or its log happened to say.
                kept = kept with { Texts = [.. kept.Texts.Select(t => t with { Text = marker.Withhold(t.Text) })] };
            }

            reference = await artifacts.SaveAsync(kept, cancellationToken).ConfigureAwait(false);
        }

        var stepResults = outcomes
            .Select(o => new StepResult(
                StepPrefix + (o.Screen.Id ?? o.Index.ToString(CultureInfo.InvariantCulture)),
                o.Outcome,
                Withheld(marker, $"Screen {o.Label} of {named}"
                    + (o.Url is null ? string.Empty : " at " + o.Url)
                    + ": " + o.Detail
                    + (reference is null || o.Url is null ? string.Empty : $" The report and screenshots are in {reference}."))))
            .ToList();

        var sentence = result is { Refusal: null }
            ? $"Checked against the approved {named}" + (reference is null ? "; its evidence could not be written to disk." : $"; evidence in {reference}.")
            : $"The approved {named} was not checked: " + (notChecked ?? "no screen had a page to check.");

        return new DesignVerificationResult(
            stepResults, reference, sentence, DesignNode(design.Number, design.Title, design.Version, reference, null, outcomes));
    }

    private static string Withheld(WindowMarker? marker, string text) => marker is null ? text : marker.Withhold(text);

    /// <summary>How one screen came out, by the rule in the class's remarks.</summary>
    private static ScreenOutcome Judge(int index, MockupNode screen, string label, DesignScreenCheck checkedScreen)
    {
        var build = checkedScreen.Build;
        var differences = build.Report.Findings.Where(f => f.Kind != DesignFindingKind.NotRun).Select(Say).ToList();
        var notRun = build.Report.Findings.Count(f => f.Kind == DesignFindingKind.NotRun);
        var console = build.Console.Count;
        var findings = build.Report.Findings.Select(Say).ToList();

        if (differences.Count > 0 || console > 0)
        {
            var said = new StringBuilder();
            if (differences.Count > 0)
            {
                said.Append(CultureInfo.InvariantCulture, $"{differences.Count} difference(s) from the design: ")
                    .Append(string.Join(" ", differences.Take(FindingsInDetail)));
                if (differences.Count > FindingsInDetail)
                {
                    said.Append(CultureInfo.InvariantCulture, $" And {differences.Count - FindingsInDetail} more in the report.");
                }
            }

            if (console > 0)
            {
                said.Append(said.Length > 0 ? " " : string.Empty)
                    .Append(CultureInfo.InvariantCulture, $"The build's console has {console} error(s); they are in console.txt.");
            }

            if (notRun > 0)
            {
                said.Append(CultureInfo.InvariantCulture, $" {notRun} expectation(s) were NOT RUN.");
            }

            return new ScreenOutcome(index, screen, label, checkedScreen.Url, StepOutcome.Failed, said.ToString(), findings, console);
        }

        if (notRun > 0)
        {
            var first = build.Report.Findings.First(f => f.Kind == DesignFindingKind.NotRun);
            return new ScreenOutcome(index, screen, label, checkedScreen.Url, StepOutcome.NotRun,
                string.Create(CultureInfo.InvariantCulture, $"{notRun} expectation(s) were NOT RUN because a phase could not be driven, so this is not a pass. ")
                + Say(first), findings, 0);
        }

        if (!checkedScreen.Prototype.Report.Matches)
        {
            // ADR 0030 decision 3: the prototype is the checker's own oracle. When it does not match its own design the
            // checker disagrees with its renderer, so a verdict on the build would not be one.
            return new ScreenOutcome(index, screen, label, checkedScreen.Url, StepOutcome.NotRun,
                "The build matched, but the design's own prototype did not, so the checker disagrees with its renderer and "
                + "this verdict cannot be trusted. The prototype's findings are in the report.", findings, 0);
        }

        var views = build.Report.Views.Count;
        return new ScreenOutcome(index, screen, label, checkedScreen.Url, StepOutcome.Passed,
            string.Create(CultureInfo.InvariantCulture, $"matched the design in all {views} phase(s) and viewport(s), and the build's console stayed empty."),
            findings, 0);
    }

    /// <summary>A finding in the design's terms: its kind in capitals, then the matcher's sentence.</summary>
    public static string Say(DesignFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return KindLabel(finding.Kind) + ": " + finding.Sentence;
    }

    /// <summary>ADR 0030 decision 6's words for each kind.</summary>
    public static string KindLabel(DesignFindingKind kind) => kind switch
    {
        DesignFindingKind.Missing => "MISSING",
        DesignFindingKind.DifferentName => "DIFFERENT NAME",
        DesignFindingKind.DifferentRole => "DIFFERENT ROLE",
        DesignFindingKind.OutOfOrder => "OUT OF ORDER",
        DesignFindingKind.OutsideItsContainer => "OUTSIDE ITS CONTAINER",
        DesignFindingKind.NotLaidOutAsDesigned => "NOT LAID OUT AS DESIGNED",
        DesignFindingKind.Illegible => "ILLEGIBLE",
        DesignFindingKind.NotRun => "NOT RUN",
        _ => kind.ToString().ToUpperInvariant(),
    };

    /// <summary>
    /// Every page the scenario's runnable steps reach: the path after each <c>$VERIFY_APP_URL</c> or <c>${VERIFY_APP_URL}</c>,
    /// with any fragment dropped; <c>/</c> for a bare address. In the order first written, each once.
    /// </summary>
    public static IReadOnlyList<string> ReachedPaths(IEnumerable<ScenarioStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var paths = new List<string>();
        foreach (var step in steps.Where(s => s.IsRunnable))
        {
            foreach (Match match in AppUrl().Matches(step.Command!))
            {
                var path = match.Groups["path"].Success ? match.Groups["path"].Value : "/";
                var hash = path.IndexOf('#', StringComparison.Ordinal);
                path = hash >= 0 ? path[..hash] : path;
                if (path.Length == 0)
                {
                    path = "/";
                }

                if (!paths.Contains(path, StringComparer.Ordinal))
                {
                    paths.Add(path);
                }
            }
        }

        return paths;
    }

    /// <summary>The path that shows <paramref name="screen"/>, or null when the scenario reaches none.</summary>
    public static string? PathFor(MockupNode screen, int index, IReadOnlyList<string> reached)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(reached);
        var names = new[] { screen.Id, screen.Text }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => Fold(n!))
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var path in reached)
        {
            var bare = path.Split('?')[0].TrimEnd('/');
            var last = bare[(bare.LastIndexOf('/') + 1)..];
            if (last.Length > 0 && names.Contains(Fold(Uri.UnescapeDataString(last))))
            {
                return path;
            }
        }

        // Every scenario this runs for uses the application, so its root is reached even when a script reads the address
        // in a way the pattern cannot see.
        return index == 0 ? "/" : null;
    }

    /// <summary>Lower-case letters and digits only, so <c>Team directory</c>, <c>team-directory</c> and <c>teamDirectory</c> agree.</summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var folded = new StringBuilder(text.Length);
        foreach (var c in text.Where(char.IsLetterOrDigit))
        {
            folded.Append(char.ToLowerInvariant(c));
        }

        return folded.ToString();
    }

    private static string Label(MockupNode screen, int index) =>
        (screen.Text, screen.Id) switch
        {
            ({ Length: > 0 } text, { Length: > 0 } id) => $"\"{text}\" ({id})",
            ({ Length: > 0 } text, _) => $"\"{text}\"",
            (_, { Length: > 0 } id) => $"({id})",
            _ => index.ToString(CultureInfo.InvariantCulture),
        };

    private static JsonNode DesignNode(
        int number, string title, long version, string? reference, string? unread, IReadOnlyList<ScreenOutcome> outcomes)
    {
        var node = new JsonObject
        {
            ["number"] = number,
            ["title"] = title,
            ["version"] = version,
            ["artefact"] = reference,
        };

        if (unread is not null)
        {
            node["unreadable"] = unread;
        }

        node["screens"] = new JsonArray([
            .. outcomes.Select(o => (JsonNode)new JsonObject
            {
                ["screen"] = o.Index,
                ["id"] = o.Screen.Id,
                ["title"] = o.Screen.Text,
                ["url"] = o.Url,
                ["outcome"] = o.Outcome.ToString(),
                ["findings"] = new JsonArray([.. o.Findings.Select(f => (JsonNode)JsonValue.Create(f)!)]),
                ["console"] = o.Console,
            }),
        ]);
        return node;
    }

    /// <summary>What goes to disk: the structural report, both consoles, the application's logs and every screenshot.</summary>
    private static VerificationArtifactSet Artifacts(string named, IReadOnlyList<ScreenOutcome> outcomes, DesignCheckResult result, string? logs)
    {
        var report = new StringBuilder()
            .Append("# Design check\n\n")
            .Append(CultureInfo.InvariantCulture, $"Checked against the approved {named} (the version approved, not the document's current one).\n")
            .Append("The structural check decides; the screenshots are evidence for a person (ADR 0030 decision 7).\n");

        var console = new StringBuilder();
        var pictures = new List<VerificationPicture>();
        foreach (var outcome in outcomes)
        {
            report.Append(CultureInfo.InvariantCulture, $"\n## Screen {outcome.Index} {outcome.Label}: {outcome.Outcome.ToString().ToUpperInvariant()}\n\n")
                .Append(outcome.Url is null ? string.Empty : $"Build address: {outcome.Url}\n\n")
                .Append(outcome.Detail).Append('\n');

            if (result.Screens.FirstOrDefault(s => s.Screen == outcome.Index) is not { } screen)
            {
                continue;
            }

            foreach (var (kind, side) in new[] { (DesignCheckSideKind.Build, screen.Build), (DesignCheckSideKind.Prototype, screen.Prototype) })
            {
                report.Append(CultureInfo.InvariantCulture, $"\n### {kind}: {(side.Report.Matches ? "matches" : "does not match")}\n\n");
                foreach (var view in side.Report.Views)
                {
                    report.Append(CultureInfo.InvariantCulture, $"- {view.Phase}{(view.Viewport is null ? string.Empty : ", " + view.Viewport)}: {view.Outcome}\n");
                    foreach (var finding in view.Findings)
                    {
                        report.Append("  - ").Append(Say(finding)).Append('\n');
                    }

                    foreach (var extra in view.Extras)
                    {
                        report.Append(CultureInfo.InvariantCulture, $"  - not in the design (listed, never failed): {extra.Role}{(extra.Name is null ? string.Empty : $" \"{extra.Name}\"")}\n");
                    }
                }

                console.Append(CultureInfo.InvariantCulture, $"# {kind}, screen {outcome.Index} {outcome.Label}: {side.Console.Count} entr(ies)\n");
                foreach (var line in side.Console)
                {
                    console.Append(line).Append('\n');
                }

                console.Append('\n');
                pictures.AddRange(side.Screenshots.Select(s => new VerificationPicture(s.FileName(kind), s.Png)));
            }

            report.Append("\nScreenshots, build beside prototype:\n");
            foreach (var key in screen.Build.Screenshots.Select(s => s.Key).Union(screen.Prototype.Screenshots.Select(s => s.Key)))
            {
                report.Append(CultureInfo.InvariantCulture, $"- {key}.build.png | {key}.prototype.png\n");
            }
        }

        return new VerificationArtifactSet(
            [
                new VerificationText(ReportFile, report.ToString()),
                new VerificationText(ConsoleFile, console.ToString()),
                new VerificationText(LogFile, logs ?? "(the application's output could not be read)\n"),
            ],
            pictures);
    }

    /// <summary>The structural report's file name.</summary>
    public const string ReportFile = "report.md";

    /// <summary>The consoles' file name.</summary>
    public const string ConsoleFile = "console.txt";

    /// <summary>The application's logs' file name.</summary>
    public const string LogFile = "app.log";

    [GeneratedRegex(@"\$\{?VERIFY_APP_URL\b\}?(?<path>/[^\s""'`;|&()<>\\$]*)?", RegexOptions.CultureInvariant)]
    private static partial Regex AppUrl();

    private sealed record ScreenOutcome(
        int Index,
        MockupNode Screen,
        string Label,
        string? Url,
        StepOutcome Outcome,
        string Detail,
        IReadOnlyList<string> Findings,
        int Console);
}
