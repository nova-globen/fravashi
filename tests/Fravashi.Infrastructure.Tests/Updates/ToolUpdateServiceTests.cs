using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Notifications;
using Fravashi.Application.Updates;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Infrastructure.Tests.AgentRuntimes;
using Fravashi.Infrastructure.Updates;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Updates;

/// <summary>
/// The Type A tool-update service (F1 / ADR 0006), reshaped by backlog 61 / ADR 0023: the registry check now
/// answers "has the vendor published past our PIN" (reported, never installed), the only offered action is a
/// repair back onto the pin, the cache lives under a new key, and nothing anywhere reaches for an unpinned
/// release.
/// </summary>
public class ToolUpdateServiceTests
{
    private static AgentRuntimeStatus Installed(AgentTool tool, string version) =>
        new(tool, Installed: true, version, AgentRuntimeAuthState.Unknown, null);

    private static string Pinned(AgentTool tool) => AgentRuntimeCatalog.For(tool).PinnedVersion;

    private static (ToolUpdateService Service, FakePackageRegistry Registry, FakeAgentRuntimeService Runtimes,
        FakeConfigurationStore Config, InMemoryNotificationHistory Notifications) Build()
    {
        var registry = new FakePackageRegistry();
        var runtimes = new FakeAgentRuntimeService();
        var config = new FakeConfigurationStore();
        var notifications = new InMemoryNotificationHistory();
        var service = new ToolUpdateService(registry, runtimes, config, notifications, NullLogger<ToolUpdateService>.Instance);

        // Default: every tool installed AT ITS PIN and nothing newer published; individual tests override.
        foreach (var d in AgentRuntimeCatalog.All)
        {
            runtimes.Statuses[d.Tool] = Installed(d.Tool, d.PinnedVersion);
            registry.Latest[d.NpmPackage] = d.PinnedVersion;
        }

        return (service, registry, runtimes, config, notifications);
    }

    [Fact]
    public async Task CheckNow_flags_a_newer_release_when_the_registry_is_ahead_of_the_pin()
    {
        var (service, registry, _, _, _) = Build();
        registry.Latest["@anthropic-ai/claude-code"] = "99.0.0";

        var results = await service.CheckNowAsync();

        var claude = results.Single(r => r.Tool == AgentTool.Claude);
        claude.NewerPublished.Should().BeTrue();
        claude.PinnedVersion.Should().Be(Pinned(AgentTool.Claude));
        claude.LatestVersion.Should().Be("99.0.0");
        results.Where(r => r.Tool != AgentTool.Claude).Should().OnlyContain(r => !r.NewerPublished);
    }

    [Fact]
    public async Task A_newer_published_release_is_reported_but_never_installed()
    {
        // The decision has two halves and this is the seam between them: reporting must not become
        // promoting. The row says a newer release exists, offers no repair, and the notification says so.
        var (service, registry, runtimes, _, notifications) = Build();
        registry.Latest["@anthropic-ai/claude-code"] = "99.0.0";

        var results = await service.CheckNowAsync();

        var claude = results.Single(r => r.Tool == AgentTool.Claude);
        claude.NewerPublished.Should().BeTrue();
        claude.OffPin.Should().BeFalse("the installed CLI is exactly the pinned one");
        claude.Note.Should().Contain("product change");
        runtimes.Updated.Should().BeEmpty("a check never installs anything");
        notifications.Recent.Should().Contain(n => n.Message.Contains("stays on its pinned version"));
        notifications.Recent.Should().NotContain(n => n.Message.Contains("An update is available"));
    }

    [Fact]
    public async Task An_installed_version_that_drifted_off_the_pin_is_offered_a_repair()
    {
        var (service, _, runtimes, _, _) = Build();
        runtimes.Statuses[AgentTool.Claude] = Installed(AgentTool.Claude, "9.9.9");

        var results = await service.CheckNowAsync();

        var claude = results.Single(r => r.Tool == AgentTool.Claude);
        claude.OffPin.Should().BeTrue();
        claude.Note.Should().Contain("9.9.9").And.Contain(Pinned(AgentTool.Claude));
    }

    [Fact]
    public async Task Update_logs_the_pinned_specifier_and_never_latest()
    {
        var (service, _, _, _, _) = Build();

        await service.UpdateAsync(AgentTool.Claude);

        var log = service.ProgressFor(AgentTool.Claude)!.Log;
        log.Should().Contain(l => l.Contains($"@anthropic-ai/claude-code@{Pinned(AgentTool.Claude)}"));
        log.Should().NotContain(l => l.Contains("@latest"));
    }

    [Fact]
    public void A_cache_written_in_the_old_shape_is_ignored_rather_than_half_read()
    {
        // The record gained PinnedVersion/NewerPublished/OffPin. A row written by the previous release under
        // the old key must not deserialize into a row claiming a null pin, so the key moved with the shape.
        var (_, registry, runtimes, config, notifications) = Build();
        config.Set(
            "updates:tools",
            """[{"tool":"Claude","displayName":"Claude Code","package":"@anthropic-ai/claude-code","installedVersion":"1.0.0","latestVersion":"1.2.0","updateAvailable":true,"note":null}]""");

        var service = new ToolUpdateService(registry, runtimes, config, notifications, NullLogger<ToolUpdateService>.Instance);

        service.Available.Should().BeEmpty("a cache in the pre-pin shape is not readable and must not be guessed at");
        service.LastCheckedUtc.Should().BeNull();
    }

    [Fact]
    public async Task CheckNow_caches_availability_in_the_settings_table()
    {
        var (service, registry, runtimes, config, notifications) = Build();
        registry.Latest["@anthropic-ai/claude-code"] = "99.0.0";

        await service.CheckNowAsync();

        config.Values.Should().ContainKey(ToolUpdateService.CacheKey);
        config.Values.Should().ContainKey(ToolUpdateService.CheckedAtKey);

        // A fresh service (same store) renders from cache without any check having run this process.
        var reloaded = new ToolUpdateService(registry, runtimes, config, notifications, NullLogger<ToolUpdateService>.Instance);
        reloaded.LastCheckedUtc.Should().NotBeNull();
        var claude = reloaded.Available.Single(i => i.Tool == AgentTool.Claude);
        claude.NewerPublished.Should().BeTrue();
        claude.PinnedVersion.Should().Be(Pinned(AgentTool.Claude));
    }

    [Fact]
    public async Task CheckNow_notifies_only_when_a_new_release_appears()
    {
        var (service, registry, _, _, notifications) = Build();
        registry.Latest["@anthropic-ai/claude-code"] = "99.0.0";

        await service.CheckNowAsync();
        notifications.Recent.Should().ContainSingle(n => n.Message.Contains("been published"));

        // A second identical check adds no further notification (nothing NEW was published).
        await service.CheckNowAsync();
        notifications.Recent.Count(n => n.Message.Contains("been published")).Should().Be(1);
    }

    [Fact]
    public async Task Not_installed_tool_is_not_updatable_and_says_so()
    {
        var (service, _, runtimes, _, _) = Build();
        runtimes.Statuses[AgentTool.Claude] = new AgentRuntimeStatus(
            AgentTool.Claude, Installed: false, null, AgentRuntimeAuthState.NotInstalled, null);

        var results = await service.CheckNowAsync();

        var absent = results.Single(r => r.Tool == AgentTool.Claude);
        absent.OffPin.Should().BeFalse("an absent CLI has not drifted, it is simply missing");
        absent.Note.Should().Contain("Not installed");
    }

    [Fact]
    public async Task Registry_unreachable_is_honest_never_a_silent_up_to_date()
    {
        var (service, registry, _, _, _) = Build();
        registry.Latest["@anthropic-ai/claude-code"] = null; // offline / 404

        var results = await service.CheckNowAsync();

        var claude = results.Single(r => r.Tool == AgentTool.Claude);
        claude.NewerPublished.Should().BeFalse();
        claude.LatestVersion.Should().BeNull();
        claude.Note.Should().Contain("registry");
    }

    [Fact]
    public async Task Update_runs_the_install_streams_progress_and_notifies_success()
    {
        var (service, _, runtimes, _, notifications) = Build();
        var pinned = Pinned(AgentTool.Claude);
        runtimes.Statuses[AgentTool.Claude] = Installed(AgentTool.Claude, "9.9.9");
        runtimes.UpdateResults[AgentTool.Claude] = new AgentRuntimeUpdateResult(
            AgentTool.Claude, AgentRuntimeUpdateOutcome.Succeeded, "9.9.9", pinned, null, DateTimeOffset.UtcNow);
        await service.CheckNowAsync();
        service.Available.Single(i => i.Tool == AgentTool.Claude).OffPin.Should().BeTrue();

        // The repair puts the installed CLI back on the pin, so re-checking must see it back at the pin.
        runtimes.Statuses[AgentTool.Claude] = Installed(AgentTool.Claude, pinned);
        await service.UpdateAsync(AgentTool.Claude);

        runtimes.Updated.Should().Contain(AgentTool.Claude);
        var progress = service.ProgressFor(AgentTool.Claude);
        progress.Should().NotBeNull();
        progress!.Phase.Should().Be(ToolUpdatePhase.Succeeded);
        progress.Log.Should().Contain(l => l.Contains("9.9.9") && l.Contains(pinned));
        notifications.Recent.Should().Contain(n => n.Severity == NotificationSeverity.Success && n.Message.Contains(pinned));

        // Post-install the tool is no longer off the pin, so nothing is offered.
        service.Available.Single(i => i.Tool == AgentTool.Claude).OffPin.Should().BeFalse();
    }

    [Fact]
    public async Task Update_failure_reports_the_real_error_and_does_not_throw()
    {
        var (service, _, runtimes, _, notifications) = Build();
        runtimes.UpdateResults[AgentTool.Claude] = new AgentRuntimeUpdateResult(
            AgentTool.Claude, AgentRuntimeUpdateOutcome.Failed, "2.0.0", "2.0.0", "npm exited with code 1.", DateTimeOffset.UtcNow);

        await service.UpdateAsync(AgentTool.Claude);

        var progress = service.ProgressFor(AgentTool.Claude);
        progress!.Phase.Should().Be(ToolUpdatePhase.Failed);
        progress.Log.Should().Contain(l => l.Contains("npm exited with code 1."));
        notifications.Recent.Should().Contain(n => n.Severity == NotificationSeverity.Error);
    }
}
