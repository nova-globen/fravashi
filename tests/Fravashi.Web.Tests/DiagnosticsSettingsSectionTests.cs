using Bunit;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Application.Telemetry;
using Fravashi.Domain.Telemetry;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 74: the Settings &gt; Advanced &gt; Diagnostics section. Two things are held. The switch
/// must default OFF and say what it records, because an opt-in that is on by default is not one; and
/// the bundle button must show the PATH it wrote, because a diagnosis the user cannot find is not one
/// they can send.
/// </summary>
public class DiagnosticsSettingsSectionTests
{
    private static BunitContext Context(
        out FakeConfigurationStore config, out RecordingBundleWriter bundles, IProductEventStore? events = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        config = new FakeConfigurationStore();
        bundles = new RecordingBundleWriter();
        ctx.Services.AddSingleton<IConfigurationStore>(config);
        ctx.Services.AddSingleton<IProductEventStore>(events ?? new StubEventStore(0));
        ctx.Services.AddSingleton<IDiagnosticBundleWriter>(bundles);
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        return ctx;
    }

    [Fact]
    public void The_switch_is_off_on_a_fresh_install()
    {
        using var ctx = Context(out _, out _);

        var cut = ctx.Render<DiagnosticsSettingsSection>();

        // Recording product events is opt-in: nothing is recorded until the owner says so.
        Assert.False(cut.Find("[data-testid=telemetry-enabled]").HasAttribute("checked"));
    }

    [Fact]
    public void The_section_says_what_is_recorded_and_what_never_is()
    {
        using var ctx = Context(out _, out _);

        var cut = ctx.Render<DiagnosticsSettingsSection>();
        var copy = cut.Find("[data-testid=telemetry-what-is-recorded]").TextContent;

        // The promise the payload schema actually keeps, in the words the user reads.
        Assert.Contains("Prompts", copy, StringComparison.Ordinal);
        Assert.Contains("never recorded", copy, StringComparison.Ordinal);
        Assert.Contains("hash", copy, StringComparison.Ordinal);
        // And the section is explicit that this stays on the machine.
        Assert.Contains("no remote destination", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bundle_copy_says_what_redaction_catches_and_what_it_cannot()
    {
        using var ctx = Context(out _, out _);

        var cut = ctx.Render<DiagnosticsSettingsSection>();
        var copy = cut.Find("[data-testid=diagnostics-redaction-copy]").TextContent;

        // Backlog 119: the copy matches what the redactor does after the widening, its limits included.
        Assert.Contains("replaced whole even when a terminal wrapped it across lines", copy, StringComparison.Ordinal);
        Assert.Contains("indented continuation lines", copy, StringComparison.Ordinal);
        Assert.Contains("prefix itself was split", copy, StringComparison.Ordinal);
        Assert.Contains("best-effort, not a guarantee", copy, StringComparison.Ordinal);
    }

    [Fact]
    public void Ticking_the_box_writes_the_opt_in_and_unticking_it_writes_it_back_off()
    {
        using var ctx = Context(out var config, out _);
        var cut = ctx.Render<DiagnosticsSettingsSection>();

        cut.InvokeAsync(() => cut.Find("[data-testid=telemetry-enabled]").Change(true));
        cut.WaitForAssertion(() =>
            Assert.True(TelemetrySettings.IsEnabled(config.Get(TelemetrySettings.EnabledKey))));

        cut.InvokeAsync(() => cut.Find("[data-testid=telemetry-enabled]").Change(false));
        cut.WaitForAssertion(() =>
            Assert.False(TelemetrySettings.IsEnabled(config.Get(TelemetrySettings.EnabledKey))));
    }

    [Fact]
    public void An_install_that_already_opted_in_renders_the_box_ticked_and_the_count()
    {
        using var ctx = Context(out var config, out _, new StubEventStore(17));
        config.Set(TelemetrySettings.EnabledKey, "true");

        var cut = ctx.Render<DiagnosticsSettingsSection>();

        Assert.True(cut.Find("[data-testid=telemetry-enabled]").HasAttribute("checked"));
        Assert.Contains("17", cut.Find("[data-testid=telemetry-count]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Writing_a_bundle_shows_the_path_it_wrote()
    {
        using var ctx = Context(out _, out var bundles);
        var cut = ctx.Render<DiagnosticsSettingsSection>();

        cut.InvokeAsync(() => cut.Find("[data-testid=diagnostics-export]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(
                "/data/diagnostics/fravashi-diagnostics-20260913-070509.zip",
                cut.Find("[data-testid=diagnostics-path]").TextContent);
            Assert.Contains(
                "summary.txt",
                cut.Find("[data-testid=diagnostics-result]").TextContent,
                StringComparison.Ordinal);
        });
        Assert.Equal(1, bundles.Writes);
    }

    [Fact]
    public void A_bundle_that_could_not_be_written_says_so_instead_of_reporting_a_path()
    {
        using var ctx = Context(out _, out var bundles);
        bundles.Failure = new IOException("the disk is full");
        var cut = ctx.Render<DiagnosticsSettingsSection>();

        cut.InvokeAsync(() => cut.Find("[data-testid=diagnostics-export]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(
                "the disk is full",
                cut.Find("[data-testid=diagnostics-error]").TextContent,
                StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=diagnostics-result]"));
        });
    }

    [Fact]
    public void The_bundle_can_be_written_without_ever_opting_in_to_recording()
    {
        // The two are deliberately independent: a user reporting a problem should not have to switch on
        // recording first, and switching recording on should not be the price of asking for help.
        using var ctx = Context(out var config, out var bundles);
        var cut = ctx.Render<DiagnosticsSettingsSection>();

        Assert.False(TelemetrySettings.IsEnabled(config.Get(TelemetrySettings.EnabledKey)));
        cut.InvokeAsync(() => cut.Find("[data-testid=diagnostics-export]").Click());

        cut.WaitForAssertion(() => Assert.Equal(1, bundles.Writes));
    }

    // ---- doubles ---------------------------------------------------------------------------------

    private sealed class RecordingBundleWriter : IDiagnosticBundleWriter
    {
        public int Writes { get; private set; }

        public Exception? Failure { get; set; }

        public DiagnosticBundle Write()
        {
            Writes++;
            if (Failure is { } failure)
            {
                throw failure;
            }

            return new DiagnosticBundle(
                "/data/diagnostics/fravashi-diagnostics-20260913-070509.zip",
                2048,
                ["summary.txt", "posture.txt", "database.txt", "events.csv"]);
        }
    }

    private sealed class StubEventStore(long count) : IProductEventStore
    {
        public void Append(ProductEvent productEvent)
        {
        }

        public IReadOnlyList<ProductEvent> Recent(int max = 2000) => [];

        public IReadOnlyList<ProductEvent> Since(ProductEventKind kind, DateTimeOffset fromUtc, int max = 20000) => [];

        public long Count() => count;
    }
}
