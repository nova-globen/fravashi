using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Updates;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions.Tools;

/// <summary>
/// Backlog 157: an agent schedules the product's own upgrade for the end of its turn. What is tested here is the tool's
/// half: it names the exact version the channel offers or schedules nothing, one conversation at a time, and it says
/// why whenever it does nothing. The turn boundary and the resume are LiveSessionRegistryTests'.
/// </summary>
public sealed class StudioUpgradeToolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fravashi-upgrade-tool-" + Guid.NewGuid().ToString("N"));
    private readonly StudioUpgradeSchedule _schedule;

    public StudioUpgradeToolTests()
    {
        Directory.CreateDirectory(_directory);
        _schedule = new StudioUpgradeSchedule(_directory);
    }

    private static Task<StudioToolResult> Call(StudioUpgradeTool tool, AgentSessionId session, string arguments) =>
        tool.InvokeAsync(new StudioToolCall(session, tool.Name, JsonDocument.Parse(arguments).RootElement, "k", ProjectId.Default));

    [Fact]
    public async Task It_schedules_exactly_the_offered_version_for_the_end_of_the_turn()
    {
        var session = AgentSessionId.New();
        var tool = new StudioUpgradeTool(new OfferingUpgrades("0.9.45"), _schedule);

        var wrong = await Call(tool, session, """{"version":"0.9.46"}""");
        wrong.IsError.Should().BeTrue();
        wrong.Text.Should().Contain("offers 0.9.45, not 0.9.46");
        _schedule.Take(session).Should().BeNull("nothing is scheduled for a version nobody offers");

        var right = await Call(tool, session, """{"version":"v0.9.45"}""");
        right.IsError.Should().BeFalse();
        right.Text.Should().Contain("from 0.9.44 to 0.9.45");
        _schedule.Take(session).Should().Be("0.9.45");
    }

    [Fact]
    public async Task One_conversation_at_a_time()
    {
        var tool = new StudioUpgradeTool(new OfferingUpgrades("0.9.45"), _schedule);
        (await Call(tool, AgentSessionId.New(), """{"version":"0.9.45"}""")).IsError.Should().BeFalse();

        var second = await Call(tool, AgentSessionId.New(), """{"version":"0.9.45"}""");

        second.IsError.Should().BeTrue();
        second.Text.Should().Contain("Another conversation already scheduled");
    }

    [Fact]
    public async Task Without_the_operation_or_an_offer_it_says_why_and_schedules_nothing()
    {
        var session = AgentSessionId.New();

        var none = await Call(new StudioUpgradeTool(new OfferingUpgrades(null), _schedule), session, """{"version":"0.9.45"}""");
        none.Text.Should().Be("Fravashi 0.9.44 is the newest release the update channel offers.");

        var unavailable = await Call(new StudioUpgradeTool(new OfferingUpgrades("0.9.45") { Available = false }, _schedule), session, """{"version":"0.9.45"}""");
        unavailable.Text.Should().Contain("cannot upgrade itself");

        var noVersion = await Call(new StudioUpgradeTool(new OfferingUpgrades("0.9.45"), _schedule), session, """{"version":7}""");
        noVersion.IsError.Should().BeTrue();

        _schedule.Take(session).Should().BeNull();
    }

    [Fact]
    public void A_resume_marker_survives_on_disk_and_is_read_once()
    {
        var session = AgentSessionId.New();
        _schedule.MarkResume(session, "0.9.45");

        new StudioUpgradeSchedule(_directory).TakeResume(session).Should().Be("0.9.45", "a new process reads what the old one wrote");
        new StudioUpgradeSchedule(_directory).TakeResume(session).Should().BeNull();
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>An upgrade operation whose channel offers <paramref name="offered"/> over an installed 0.9.44.</summary>
    internal sealed class OfferingUpgrades(string? offered) : IStudioUpgrades
    {
        public bool Available { get; init; } = true;

        public List<string> Applied { get; } = [];

        public string? Refusal { get; set; }

        public Task<UpgradeOffer?> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<UpgradeOffer?>(new UpgradeOffer(
                "0.9.44", offered, false, offered is null ? "Fravashi 0.9.44 is the newest release the update channel offers." : null));

        public Task<string?> ApplyAsync(string version, CancellationToken cancellationToken = default)
        {
            Applied.Add(version);
            return Task.FromResult(Refusal);
        }

        public Task<UpgradeProgress?> StatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<UpgradeProgress?>(null);
    }
}
