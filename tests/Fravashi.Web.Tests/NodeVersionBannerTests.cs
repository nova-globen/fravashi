using Bunit;
using Fravashi.Infrastructure.AgentRuntimes;
using Fravashi.Web.Components.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 10: the band that ends the silent-wrong-Node behaviour. It reads a REAL folder standing in for
/// the workspace copy and a REAL nvs layout under a temp root, through the real advisor and coordinator;
/// only the download itself and <c>node --version</c> are stand-ins.
/// </summary>
public sealed class NodeVersionBannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fravashi-node-banner", Guid.NewGuid().ToString("N"));
    private readonly string _copy;
    private readonly string _nvsHome;
    private readonly FakeNodeProvisioner _provisioner = new();

    public NodeVersionBannerTests()
    {
        _copy = Path.Combine(_root, "copy");
        _nvsHome = Path.Combine(_root, "nvs");
        Directory.CreateDirectory(_copy);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private BunitContext Context(string? imageVersionOutput = "v22.14.0\n")
    {
        var ctx = new BunitContext();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["NVS_HOME"] = _nvsHome })
            .Build();
        ctx.Services.AddSingleton(new WorkspaceNodeAdvisor(
            new NodePathProvisioner(configuration), new FakeNodeCommandRunner(imageVersionOutput)));
        ctx.Services.AddSingleton(new NodeProvisionCoordinator(_provisioner));
        return ctx;
    }

    private IRenderedComponent<NodeVersionBanner> Banner(BunitContext ctx) =>
        ctx.Render<NodeVersionBanner>(p => p.Add(c => c.WorkingDirectory, _copy));

    private void Provision(string version) =>
        Directory.CreateDirectory(Path.Combine(_nvsHome, "node", version, "x64", "bin"));

    [Fact]
    public void A_workspace_that_requests_nothing_shows_no_banner()
    {
        using var ctx = Context();

        var cut = Banner(ctx);

        Assert.Empty(cut.FindAll("[data-testid=node-banner]"));
        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    [Fact]
    public void A_requested_version_that_is_provisioned_shows_no_banner()
    {
        File.WriteAllText(Path.Combine(_copy, ".nvmrc"), "22\n");
        Provision("22.12.0");
        using var ctx = Context();

        var cut = Banner(ctx);

        Assert.Empty(cut.FindAll("[data-testid=node-banner]"));
    }

    [Fact]
    public void A_requested_version_that_is_absent_names_both_versions_and_offers_the_install()
    {
        File.WriteAllText(Path.Combine(_copy, ".nvmrc"), "v20.11.1\n");
        using var ctx = Context();

        var cut = Banner(ctx);

        cut.WaitForAssertion(() =>
        {
            var requested = cut.Find("[data-testid=node-requested]").TextContent;
            Assert.Contains("Node 20.11.1", requested, StringComparison.Ordinal);
            Assert.Contains(".nvmrc", requested, StringComparison.Ordinal);
            Assert.Contains("Node 22.14.0", cut.Find("[data-testid=node-actual]").TextContent, StringComparison.Ordinal);
        });
        Assert.Equal("Install Node 20.11.1", cut.Find("[data-testid=node-install]").TextContent.Trim());
        // Warn, not danger: the session still works, on a Node the workspace did not ask for.
        Assert.DoesNotContain("session-node-failed", cut.Find("[data-testid=node-banner]").ClassList);
        // Nothing was downloaded unasked.
        Assert.Empty(_provisioner.Requested);
    }

    [Fact]
    public void No_runnable_node_is_said_in_those_words_rather_than_as_a_number()
    {
        File.WriteAllText(Path.Combine(_copy, ".node-version"), "18\n");
        using var ctx = Context(imageVersionOutput: null);

        var cut = Banner(ctx);

        cut.WaitForAssertion(() =>
            Assert.Contains("No runnable Node", cut.Find("[data-testid=node-actual]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clicking_install_shows_the_phase_and_the_bytes_as_they_arrive()
    {
        File.WriteAllText(Path.Combine(_copy, ".nvmrc"), "22\n");
        using var ctx = Context();
        var cut = Banner(ctx);
        cut.WaitForElement("[data-testid=node-install]");

        await cut.InvokeAsync(() => cut.Find("[data-testid=node-install]").Click());

        Assert.Equal(["22"], _provisioner.Requested);
        cut.WaitForAssertion(() =>
            Assert.Contains("finding the release", cut.Find("[data-testid=node-progress]").TextContent, StringComparison.Ordinal));

        _provisioner.Report(new NodeProvisionProgress(NodeProvisionPhase.Downloading, 5 * 1024 * 1024, 20 * 1024 * 1024, "22.12.0"));
        cut.WaitForAssertion(() =>
        {
            var text = cut.Find("[data-testid=node-progress]").TextContent;
            Assert.Contains("Installing Node 22.12.0", text, StringComparison.Ordinal);
            Assert.Contains("5 MB of 20 MB", text, StringComparison.Ordinal);
            Assert.Contains("width: 25%", cut.Find(".session-node-track .studio-progress-fill").GetAttribute("style") ?? string.Empty, StringComparison.Ordinal);
        });

        _provisioner.Report(new NodeProvisionProgress(NodeProvisionPhase.Unpacking, Version: "22.12.0"));
        cut.WaitForAssertion(() =>
            Assert.Contains("unpacking", cut.Find("[data-testid=node-progress]").TextContent, StringComparison.Ordinal));
        // One click, one install: the button is gone while it runs.
        Assert.Empty(cut.FindAll("[data-testid=node-install]"));
    }

    [Fact]
    public async Task A_failed_install_says_why_and_offers_a_retry()
    {
        File.WriteAllText(Path.Combine(_copy, ".nvmrc"), "22\n");
        using var ctx = Context();
        var cut = Banner(ctx);
        cut.WaitForElement("[data-testid=node-install]");

        await cut.InvokeAsync(() => cut.Find("[data-testid=node-install]").Click());
        _provisioner.Finish(NodeProvisionResult.Failure("Node 22 could not be installed: no route to host. Nothing was installed."));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("no route to host", cut.Find("[data-testid=node-error]").TextContent, StringComparison.Ordinal);
            Assert.Contains("session-node-failed", cut.Find("[data-testid=node-banner]").ClassList);
        });

        await cut.InvokeAsync(() => cut.Find("[data-testid=node-retry]").Click());

        Assert.Equal(["22", "22"], _provisioner.Requested);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=node-progress]")));
    }

    [Fact]
    public async Task A_finished_install_says_when_it_takes_effect_and_then_goes_away()
    {
        File.WriteAllText(Path.Combine(_copy, ".nvmrc"), "22\n");
        using var ctx = Context();
        var cut = Banner(ctx);
        cut.WaitForElement("[data-testid=node-install]");
        // The real provisioner's success IS the version being in the layout; the fake lays it down.
        _provisioner.OnInstall = _ => Provision("22.12.0");

        await cut.InvokeAsync(() => cut.Find("[data-testid=node-install]").Click());
        _provisioner.Finish(new NodeProvisionResult(true, "22.12.0", Path.Combine(_nvsHome, "node", "22.12.0", "x64", "bin"), null));

        cut.WaitForAssertion(() =>
        {
            var done = cut.Find("[data-testid=node-installed]").TextContent;
            Assert.Contains("Node 22.12.0 is installed", done, StringComparison.Ordinal);
            Assert.Contains("keeps the Node it started with", done, StringComparison.Ordinal);
        });

        await cut.InvokeAsync(() => cut.Find("[data-testid=node-dismiss]").Click());

        Assert.Empty(cut.FindAll("[data-testid=node-banner]"));
    }

    [Fact]
    public async Task A_dismissed_notice_stays_dismissed_on_the_other_surface_and_on_a_pane_opened_later()
    {
        // The coordinator keeps a finished install for the life of the process while panes are created and
        // thrown away as the user moves between the Sessions area and the Code page, so the dismissal has
        // to live with the install: as a field of one pane, the green notice came back on every new pane
        // until a restart.
        File.WriteAllText(Path.Combine(_copy, ".nvmrc"), "22\n");
        using var ctx = Context();
        var first = Banner(ctx);
        var second = Banner(ctx);
        first.WaitForElement("[data-testid=node-install]");
        _provisioner.OnInstall = _ => Provision("22.12.0");

        await first.InvokeAsync(() => first.Find("[data-testid=node-install]").Click());
        _provisioner.Finish(new NodeProvisionResult(true, "22.12.0", Path.Combine(_nvsHome, "node", "22.12.0", "x64", "bin"), null));
        first.WaitForElement("[data-testid=node-dismiss]");
        second.WaitForElement("[data-testid=node-installed]");

        await first.InvokeAsync(() => first.Find("[data-testid=node-dismiss]").Click());

        Assert.Empty(first.FindAll("[data-testid=node-banner]"));
        second.WaitForAssertion(() => Assert.Empty(second.FindAll("[data-testid=node-banner]")));
        var later = Banner(ctx);
        Assert.Empty(later.FindAll("[data-testid=node-banner]"));
        Assert.Equal(string.Empty, later.Markup.Trim());
    }

    [Fact]
    public async Task Two_surfaces_share_one_install()
    {
        // The Sessions area and the Code page's panel are two views of one workspace: a click on one
        // shows on the other, and a click on the other joins the running install rather than starting a
        // second download of the same tarball.
        File.WriteAllText(Path.Combine(_copy, ".nvmrc"), "22\n");
        using var ctx = Context();
        var first = Banner(ctx);
        var second = Banner(ctx);
        first.WaitForElement("[data-testid=node-install]");
        second.WaitForElement("[data-testid=node-install]");

        await first.InvokeAsync(() => first.Find("[data-testid=node-install]").Click());

        second.WaitForAssertion(() => Assert.NotEmpty(second.FindAll("[data-testid=node-progress]")));
        Assert.Single(_provisioner.Requested);
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(31_457_280, "30 MB")]
    public void Bytes_read_the_way_a_person_reads_them(long value, string expected) =>
        Assert.Equal(expected, NodeVersionBanner.Bytes(value));
}
