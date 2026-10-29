using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Prototypes;
using Fravashi.Application.Verification;
using Fravashi.Infrastructure.Browser;
using Fravashi.Infrastructure.Verification;
using Xunit;
using Xunit.Abstractions;

namespace Fravashi.Infrastructure.Tests.Verification;

/// <summary>
/// Backlog 223's live tier, the self-test of ADR 0030 decision 3: the REAL <see cref="PlaywrightDesignCheck"/> driving a
/// real headless Chromium. Every valid mockup fixture's prototype must match its own design on every screen, phase and
/// viewport, and the hand-built team-directory pages of spike @44 must give @44's verdicts. Like
/// <see cref="Browser.AgentBrowserLiveTests"/>, it runs only where <c>FRAVASHI_BROWSER_LIVE=1</c> is set (the studio
/// container, where the image bakes a browser) and looks for that browser only where the agent browser does; everywhere
/// else it is skipped with its reason. Set <c>FRAVASHI_DESIGN_CHECK_SHOTS</c> to a folder to keep the screenshots.
/// </summary>
public sealed class DesignCheckLiveTests : IAsyncLifetime
{
    private const string NoisyMarker = "design check proof";

    private readonly ITestOutputHelper _output;
    private HttpListener? _server;
    private string _url = string.Empty;
    private PlaywrightDesignCheck? _check;

    public DesignCheckLiveTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("FRAVASHI_BROWSER_LIVE") == "1";

    private const string SkipReason =
        "Set FRAVASHI_BROWSER_LIVE=1 where a Chromium is installed (the studio container) to drive a real browser.";

    /// <summary>Skipped with its reason at discovery time, because Assert.Skip needs xunit v3.</summary>
    private sealed class BrowserLiveFactAttribute : FactAttribute
    {
        public BrowserLiveFactAttribute() => Skip = Enabled ? null : SkipReason;
    }

    /// <summary>The same, for a theory.</summary>
    private sealed class BrowserLiveTheoryAttribute : TheoryAttribute
    {
        public BrowserLiveTheoryAttribute() => Skip = Enabled ? null : SkipReason;
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", name));

    private static string Page(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Verification", "Pages", name + ".html"));

    private static MockupDocument Design(string fixture)
    {
        var read = MockupReader.Read(Fixture(fixture));
        read.IsValid.Should().BeTrue(string.Join("; ", read.Errors));
        return read.Document!;
    }

    public Task InitializeAsync()
    {
        if (!Enabled)
        {
            return Task.CompletedTask;
        }

        var port = FreePort();
        _url = $"http://127.0.0.1:{port}/";
        _server = new HttpListener();
        _server.Prefixes.Add(_url);
        _server.Start();
        _ = Task.Run(ServeAsync);

        // Loopback is allowed so the test can serve its pages; one loopback port stays refused to prove the route runs.
        _check = new PlaywrightDesignCheck(new BrowserUrlPolicy(["http://127.0.0.1:1/"], allowLoopback: true));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server?.Close();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <c>/pages/{name}</c> is a hand-built page, <c>/pages/noisy</c> the faithful one with a console error and an image
    /// on the refused port, and <c>/prototype/{fixture}/{screen}</c> a fixture's prototype showing that screen.
    /// </summary>
    private async Task ServeAsync()
    {
        while (_server!.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _server.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            var parts = (context.Request.Url?.AbsolutePath ?? "/").Trim('/').Split('/');
            string? html = null;
            try
            {
                html = parts switch
                {
                    ["pages", "noisy"] => Page("faithful").Replace(
                        "</body>",
                        $"<img src=\"http://127.0.0.1:1/never.png\" alt=\"\"><script>console.error('{NoisyMarker}');</script></body>",
                        StringComparison.Ordinal),
                    ["pages", var name] => Page(name),
                    ["prototype", var fixture, var screen] => PrototypeRenderer.Render(Design(fixture), [], int.Parse(screen, System.Globalization.CultureInfo.InvariantCulture)).Html,
                    _ => null,
                };
            }
            catch (IOException)
            {
                html = null;
            }

            var bytes = Encoding.UTF8.GetBytes(html ?? "<!doctype html><title>Not found</title>");
            context.Response.StatusCode = html is null ? 404 : 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private async Task<DesignCheckResult> CheckAsync(MockupDocument design, params DesignCheckTarget[] targets)
    {
        _check!.UnavailableReason.Should().BeNull("FRAVASHI_BROWSER_LIVE=1 says a browser is here");
        var result = await _check.CheckAsync(new DesignCheckRequest(design, targets));
        result.Refusal.Should().BeNull();
        Keep(result);
        return result;
    }

    private void Keep(DesignCheckResult result)
    {
        if (Environment.GetEnvironmentVariable("FRAVASHI_DESIGN_CHECK_SHOTS") is not { Length: > 0 } folder)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        foreach (var screen in result.Screens)
        {
            var label = screen.Url.TrimEnd('/').Split('/').Last();
            foreach (var shot in screen.Build.Screenshots)
            {
                File.WriteAllBytes(Path.Combine(folder, label + "." + shot.FileName(DesignCheckSideKind.Build)), shot.Png.ToArray());
            }

            foreach (var shot in screen.Prototype.Screenshots)
            {
                File.WriteAllBytes(Path.Combine(folder, label + "." + shot.FileName(DesignCheckSideKind.Prototype)), shot.Png.ToArray());
            }
        }
    }

    private static string Say(DesignMatchReport report) =>
        string.Join(Environment.NewLine, report.Findings.Select(f => f.Sentence));

    public static TheoryData<string> ValidFixtures() =>
        new(["every-feature.json", "team-directory.attempt-3.json", "book-a-room-208-re-run.json"]);

    [BrowserLiveTheory]
    [MemberData(nameof(ValidFixtures))]
    public async Task Every_fixtures_prototype_matches_its_own_design_on_every_screen_phase_and_viewport(string fixture)
    {
        var design = Design(fixture);
        var targets = Enumerable.Range(0, design.Screens.Count)
            .Select(s => new DesignCheckTarget(s, $"{_url}prototype/{fixture}/{s}"))
            .ToArray();

        var result = await CheckAsync(design, targets);

        result.Screens.Should().HaveCount(design.Screens.Count);
        foreach (var screen in result.Screens)
        {
            _output.WriteLine($"{fixture} screen {screen.Screen}: {screen.Build.Report.Views.Count} views, {screen.Build.Screenshots.Count} pictures");
            screen.Prototype.Report.Matches.Should().BeTrue(Say(screen.Prototype.Report));
            screen.Build.Report.Matches.Should().BeTrue(Say(screen.Build.Report));
            screen.Build.Console.Should().BeEmpty();

            // Side by side: every phase and viewport attempted on one side has its picture, under the same key, on the other.
            var attempted = screen.Build.Observation.Phases.Count;
            screen.Build.Screenshots.Should().HaveCount(attempted);
            screen.Build.Screenshots.Select(s => s.Key).Should().Equal(screen.Prototype.Screenshots.Select(s => s.Key));
            screen.Build.Screenshots.Should().AllSatisfy(s => s.Png.Span[..4].ToArray().Should().Equal(0x89, 0x50, 0x4E, 0x47));
        }

        result.Matches.Should().BeTrue();
    }

    public static TheoryData<string> HandBuilt() => new(["faithful", "office-removed", "renamed", "swapped", "invisible"]);

    /// <summary>
    /// The five hand-built pages give the report #222 recorded for them, which <c>DesignMatcherTests</c> holds to @44's
    /// verdicts; the kinds are written out here as well, so this test says what it proves without that file.
    /// </summary>
    [BrowserLiveTheory]
    [MemberData(nameof(HandBuilt))]
    public async Task The_hand_built_pages_give_the_spikes_verdicts(string page)
    {
        var design = Design("team-directory.attempt-3.json");
        var screen = DesignExpectations.For(design).Screens[0];
        var recorded = DesignMatcher.Match(screen, DesignObservationText.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Verification", "Observations", $"team-directory.{page}.observation.txt"))));

        var result = await CheckAsync(design, new DesignCheckTarget(0, $"{_url}pages/{page}"));
        var build = result.Screens[0].Build.Report;
        _output.WriteLine(Say(build));

        result.Screens[0].Prototype.Report.Matches.Should().BeTrue(Say(result.Screens[0].Prototype.Report));
        build.Findings.Should().Equal(recorded.Findings);

        var kinds = build.Findings.Select(f => $"{f.Kind} {f.Role} {f.Anchor} {f.Viewport}").Distinct().ToArray();
        string[] expected = page switch
        {
            "faithful" => [],
            "office-removed" => ["Missing textbox office-field wide", "Missing textbox office-field narrow"],
            "renamed" =>
            [
                "DifferentName button add-person wide", "DifferentName button add-person narrow", .. NotRun("wide"), .. NotRun("narrow"),
            ],
            "swapped" => ["OutOfOrder table people-table wide", "OutOfOrder columnheader people-table wide",
                "OutOfOrder table people-table narrow", "OutOfOrder columnheader people-table narrow"],
            "invisible" => ["Illegible button add-person wide", "Illegible button add-person narrow"],
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };
        kinds.Should().Equal(expected);
        if (page == "invisible")
        {
            build.Findings.Should().AllSatisfy(f => f.Observed.Should().Be("1.00"));
        }
    }

    private static string[] NotRun(string viewport) =>
    [
        $"NotRun dialog add-dialog {viewport}", $"NotRun textbox name-field {viewport}", $"NotRun combobox role-field {viewport}",
        $"NotRun textbox office-field {viewport}", $"NotRun checkbox welcome-email {viewport}", $"NotRun button save-btn {viewport}",
        $"NotRun button cancel-btn {viewport}",
    ];

    /// <summary>The guide's Not found page, the design #290 teaches a link with.</summary>
    private static MockupDocument NotFoundDesign()
    {
        var example = System.Text.RegularExpressions.Regex.Matches(
                MockupGuide.Text, "```json\\r?\\n(?<body>.*?)\\r?\\n```", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => m.Groups["body"].Value)
            .Single(e => e.Contains("\"type\": \"link\"", StringComparison.Ordinal));
        var read = MockupReader.Read(example);
        read.IsValid.Should().BeTrue(string.Join("; ", read.Errors));
        return read.Document!;
    }

    [BrowserLiveFact]
    public async Task A_designed_link_matches_a_built_link_and_its_prototype_and_a_built_button_is_a_different_role()
    {
        // Backlog 290: in the browser's own accessibility tree, the prototype's link is a link, a page's real a href is
        // found by role link and name, and the same words on a button are reported as a different role naming the node.
        var design = NotFoundDesign();

        var link = await CheckAsync(design, new DesignCheckTarget(0, $"{_url}pages/not-found-link"));
        _output.WriteLine(Say(link.Screens[0].Build.Report));
        link.Screens[0].Prototype.Report.Matches.Should().BeTrue(Say(link.Screens[0].Prototype.Report));
        link.Screens[0].Build.Report.Matches.Should().BeTrue(Say(link.Screens[0].Build.Report));

        var button = await CheckAsync(design, new DesignCheckTarget(0, $"{_url}pages/not-found-button"));
        var report = button.Screens[0].Build.Report;
        _output.WriteLine(Say(report));
        report.Findings.Should().ContainSingle().Which.Should().Match<DesignFinding>(f =>
            f.Kind == DesignFindingKind.DifferentRole && f.Role == "link" && f.Anchor == "go-home" && f.Observed == "button");
    }

    [BrowserLiveFact]
    public async Task A_dialog_named_by_its_own_heading_through_aria_labelledby_is_found_by_that_name()
    {
        // #222's first recording of the faithful page could not name this dialog: Playwright's AI-mode snapshot leaves out
        // a name aria-labelledby takes from inside the element. The probe recovers it, confirmed by the role engine.
        var result = await CheckAsync(Design("team-directory.attempt-3.json"), new DesignCheckTarget(0, $"{_url}pages/labelledby"));

        result.Screens[0].Build.Report.Matches.Should().BeTrue(Say(result.Screens[0].Build.Report));
    }

    [BrowserLiveFact]
    public async Task The_builds_console_errors_and_refused_requests_come_back_with_the_report()
    {
        var result = await CheckAsync(Design("team-directory.attempt-3.json"), new DesignCheckTarget(0, $"{_url}pages/noisy"));
        var screen = result.Screens[0];

        screen.Build.Report.Matches.Should().BeTrue(Say(screen.Build.Report));
        screen.Build.Console.Should().Contain(c => c.StartsWith("[screen 0, at rest, wide] console error: " + NoisyMarker, StringComparison.Ordinal));
        screen.Build.Console.Should().Contain(c =>
            c.Contains("refused by the studio: GET http://127.0.0.1:1/never.png (that address belongs to the studio's own host services)", StringComparison.Ordinal));
        screen.Prototype.Console.Should().BeEmpty("the prototype makes no request and logs nothing");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
