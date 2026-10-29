using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Backlog 15. The tray had no Start item and no idea whether the product was running, and the studio
/// window had no idea a stopped container was why a navigation failed, so stopping from the tray and
/// choosing Open landed on the browser engine's raw connection-refused page. These cover the three
/// pieces the shell asks the launcher for: what state the product is in, what the tray menu must say
/// about it, and what the window's own recovery page must say instead of the engine's.
/// </summary>
public sealed class ProductStateTests : IDisposable
{
    private readonly string _directory;
    private readonly LauncherConfigStore _configStore;

    public ProductStateTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "fravashi-product-state-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _configStore = new LauncherConfigStore(Path.Combine(_directory, "launcher.json"));
    }

    private LauncherApp Build(FakeContainerRuntime runtime) => new(
        runtime,
        _configStore,
        launcherDirectory: _directory,
        log: _ => { },
        openBrowser: _ => { },
        isPortFree: _ => true,
        tray: new FakeTrayControl(),
        defaultDataDir: () => Path.Combine(_directory, "data"));

    private void SeedConfig(string name = "fravashi") => _configStore.Save(new LauncherConfig
    {
        Port = 9090,
        DataDir = Path.Combine(_directory, "data"),
        ContainerName = name,
    });

    [Fact]
    public void A_running_container_is_Running()
    {
        SeedConfig();

        Build(new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true })
            .ProductState().Should().Be(ProductRunState.Running);
    }

    [Fact]
    public void A_container_that_was_stopped_from_the_tray_is_Stopped()
    {
        SeedConfig();

        Build(new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = false })
            .ProductState().Should().Be(ProductRunState.Stopped);
    }

    [Fact]
    public void A_configured_install_with_no_container_is_NotCreated()
    {
        SeedConfig();

        Build(new FakeContainerRuntime { Available = true, ContainerPresent = false })
            .ProductState().Should().Be(ProductRunState.NotCreated);
    }

    [Fact]
    public void No_launcher_json_is_NeverStarted()
    {
        Build(new FakeContainerRuntime { Available = true }).ProductState().Should().Be(ProductRunState.NeverStarted);
    }

    /// <summary>
    /// An engine that does not answer must never be reported as "stopped": we cannot see the container
    /// at all, and the tray would then offer a Stop that does nothing.
    /// </summary>
    [Fact]
    public void A_runtime_that_does_not_answer_is_its_own_state_not_Stopped()
    {
        SeedConfig();

        Build(new FakeContainerRuntime { Available = false, ContainerPresent = true, ContainerIsRunning = true })
            .ProductState().Should().Be(ProductRunState.RuntimeUnavailable);
    }

    /// <summary>The container-name override has to reach the state read, or it reports another container's.</summary>
    [Fact]
    public void The_container_name_override_is_the_container_asked_about()
    {
        SeedConfig(name: "fravashi");
        var runtime = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true };
        var app = new LauncherApp(
            runtime, _configStore, _directory,
            log: _ => { }, openBrowser: _ => { }, isPortFree: _ => true, tray: new FakeTrayControl(),
            containerNameOverride: "studio-selftest",
            defaultDataDir: () => Path.Combine(_directory, "data"));

        app.ProductState().Should().Be(ProductRunState.Running);

        runtime.RunningQueries.Should().Contain("studio-selftest");
    }

    [Fact]
    public void A_running_product_offers_Stop_and_not_Start()
    {
        var menu = TrayProductMenu.For(ProductRunState.Running);

        menu.CanStop.Should().BeTrue();
        menu.CanStart.Should().BeFalse();
        menu.StateLabel.Should().Contain("running");
    }

    /// <summary>
    /// The whole defect in one assertion: with the product stopped the tray must offer a way to start
    /// it. Before this change the menu had no Start item at all, so "Open Fravashi" was the only way
    /// out and it opened a window on a dead port.
    /// </summary>
    [Theory]
    [InlineData(ProductRunState.Stopped)]
    [InlineData(ProductRunState.NotCreated)]
    [InlineData(ProductRunState.NeverStarted)]
    [InlineData(ProductRunState.RuntimeUnavailable)]
    public void Anything_that_is_not_running_offers_Start_and_not_Stop(ProductRunState state)
    {
        var menu = TrayProductMenu.For(state);

        menu.CanStart.Should().BeTrue();
        menu.CanStop.Should().BeFalse();
    }

    /// <summary>Win32 caps the notification-area tooltip at 63 characters, and it is the state label.</summary>
    [Theory]
    [InlineData(ProductRunState.Running)]
    [InlineData(ProductRunState.Stopped)]
    [InlineData(ProductRunState.NotCreated)]
    [InlineData(ProductRunState.NeverStarted)]
    [InlineData(ProductRunState.RuntimeUnavailable)]
    public void Every_state_has_a_label_that_fits_a_tray_tooltip(ProductRunState state)
    {
        var label = TrayProductMenu.For(state).StateLabel;

        label.Should().NotBeNullOrWhiteSpace();
        label.Length.Should().BeLessThanOrEqualTo(63);
    }

    /// <summary>
    /// A stopped product is the common case behind a failed navigation, and the page has to say so and
    /// offer to start it, rather than the engine's "check your network".
    /// </summary>
    [Fact]
    public void A_stopped_product_gets_a_start_it_message()
    {
        var message = StudioUnavailableMessage.For(ProductRunState.Stopped);

        message.Headline.Should().Contain("stopped");
        message.ActionLabel.Should().Contain("Start");
    }

    [Fact]
    public void A_running_product_that_did_not_answer_offers_a_retry_rather_than_a_start()
    {
        var message = StudioUnavailableMessage.For(ProductRunState.Running);

        message.ActionLabel.Should().Be("Try again");
        message.Detail.Should().Contain("starting up");
    }

    [Theory]
    [InlineData(ProductRunState.Running)]
    [InlineData(ProductRunState.Stopped)]
    [InlineData(ProductRunState.NotCreated)]
    [InlineData(ProductRunState.NeverStarted)]
    [InlineData(ProductRunState.RuntimeUnavailable)]
    public void Every_state_has_a_headline_a_detail_and_a_button(ProductRunState state)
    {
        var message = StudioUnavailableMessage.For(state);

        message.Headline.Should().NotBeNullOrWhiteSpace();
        message.Detail.Should().NotBeNullOrWhiteSpace();
        message.ActionLabel.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The window cancels a navigation on purpose every time it re-imposes its workspace query, and
    /// WebView2 reports that as a failure. Treating it as one would paper the studio over with the
    /// recovery page during ordinary use.
    /// </summary>
    [Theory]
    [InlineData("OperationCanceled")]
    [InlineData("ValidAuthenticationCredentialsRequired")]
    [InlineData("ValidProxyAuthenticationRequired")]
    public void A_benign_navigation_failure_is_left_to_the_engine(string status)
        => StudioNavigationFailure.DeservesOurPage(status).Should().BeFalse();

    [Theory]
    [InlineData("ConnectionRefused")]
    [InlineData("CannotConnect")]
    [InlineData("ServerUnreachable")]
    [InlineData("HostNameNotResolved")]
    [InlineData("Timeout")]
    [InlineData("Unknown")]
    public void A_real_navigation_failure_gets_our_own_page(string status)
        => StudioNavigationFailure.DeservesOurPage(status).Should().BeTrue();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
