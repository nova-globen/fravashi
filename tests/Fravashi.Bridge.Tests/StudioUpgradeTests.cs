using System.Net;
using System.Net.Http.Json;
using Fravashi.Bridge;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Bridge.Tests;

/// <summary>
/// Backlog 157: the host-side operation that replaces the studio with the release the update channel offers. The
/// launcher's check and apply are faked; what is tested is that a person's approved version is the only one installed,
/// that one upgrade runs at a time, that the answer comes back before the work (the asker is what gets replaced), and
/// that the HTTP surface is these three operations behind a token of their own.
/// </summary>
public sealed class StudioUpgradeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fravashi-upgrade-tests", Guid.NewGuid().ToString("N"));

    public StudioUpgradeTests() => Directory.CreateDirectory(_directory);

    private static Func<CancellationToken, Task<StudioUpgradeOffer>> Offers(string? offered, string installed = "0.9.44") =>
        _ => Task.FromResult(new StudioUpgradeOffer(installed, offered, false, offered is null ? $"Fravashi {installed} is the newest release." : null));

    [Fact]
    public void The_token_is_minted_once_and_is_not_the_bridges_or_the_app_runners()
    {
        var first = StudioUpgradeToken.GetOrCreate(_directory);

        StudioUpgradeToken.GetOrCreate(_directory).Should().Be(first);
        first.Should().NotBe(BridgeAccessToken.GetOrCreate(_directory));
        StudioUpgradeToken.FileName.Should().NotBe(BridgeAccessToken.FileName);
    }

    [Fact]
    public async Task Only_the_version_the_channel_offers_now_is_started()
    {
        var applied = new List<string>();
        var runner = new StudioUpgradeRunner(Offers("0.9.45"), (v, _) =>
        {
            applied.Add(v);
            return Task.FromResult(new StudioUpgradeOutcome(true, "Updated."));
        });

        (await runner.TryStartAsync("0.9.46", default)).Should().Be("The update channel offers 0.9.45, not 0.9.46.");
        (await runner.TryStartAsync("", default)).Should().NotBeNull();
        applied.Should().BeEmpty();

        (await runner.TryStartAsync("v0.9.45", default)).Should().BeNull("a leading v names the same version");
        await runner.Running;

        applied.Should().Equal("0.9.45");
        runner.Status.State.Should().Be(StudioUpgradeState.Succeeded);
        runner.Status.Version.Should().Be("0.9.45");
    }

    [Fact]
    public async Task Nothing_offered_is_refused_in_the_channels_own_words()
    {
        var runner = new StudioUpgradeRunner(Offers(null), (_, _) => throw new InvalidOperationException("never"));

        (await runner.TryStartAsync("0.9.45", default)).Should().Be("Fravashi 0.9.44 is the newest release.");
        runner.Status.Should().Be(StudioUpgradeStatus.Idle);
    }

    [Fact]
    public async Task One_upgrade_at_a_time_and_the_answer_comes_before_the_work()
    {
        var release = new TaskCompletionSource<StudioUpgradeOutcome>();
        var runner = new StudioUpgradeRunner(Offers("0.9.45"), (_, _) => release.Task);

        (await runner.TryStartAsync("0.9.45", default)).Should().BeNull();
        runner.Status.State.Should().Be(StudioUpgradeState.Running, "the asker is what gets replaced, so it cannot wait");
        (await runner.TryStartAsync("0.9.45", default)).Should().Be("An upgrade to 0.9.45 is already running.");

        release.SetResult(new StudioUpgradeOutcome(false, "The update download failed; your current version is unchanged."));
        await runner.Running;

        runner.Status.State.Should().Be(StudioUpgradeState.Failed);
        runner.Status.Said.Should().Contain("current version is unchanged");
    }

    [Fact]
    public async Task An_apply_that_throws_is_a_failure_said_in_words()
    {
        var runner = new StudioUpgradeRunner(Offers("0.9.45"), (_, _) => throw new IOException("disk full"));

        (await runner.TryStartAsync("0.9.45", default)).Should().BeNull();
        await runner.Running;

        runner.Status.State.Should().Be(StudioUpgradeState.Failed);
        runner.Status.Said.Should().Contain("disk full");
    }

    [Fact]
    public async Task The_api_is_three_operations_behind_its_own_token()
    {
        var runner = new StudioUpgradeRunner(Offers("0.9.45"), (_, _) => Task.FromResult(new StudioUpgradeOutcome(true, "Updated.")));
        await using var app = StudioUpgradeApplication.Create("upgrade-token", ["http://127.0.0.1:0"], runner);
        await app.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        using var bridgeHeader = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        bridgeHeader.DefaultRequestHeaders.Add("X-Fravashi-Bridge", "upgrade-token");
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        client.DefaultRequestHeaders.Add(StudioUpgradeToken.Header, "upgrade-token");

        (await anonymous.GetAsync("/upgrade/check")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await bridgeHeader.GetAsync("/upgrade/check")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the bridge's header is not this one's");

        var offer = await client.GetFromJsonAsync<StudioUpgradeOffer>("/upgrade/check");
        offer!.Offered.Should().Be("0.9.45");

        var wrong = await client.PostAsJsonAsync("/upgrade/apply", new { version = "0.9.46" });
        wrong.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await wrong.Content.ReadAsStringAsync()).Should().Contain("offers 0.9.45");

        (await client.PostAsJsonAsync("/upgrade/apply", new { version = "0.9.45" })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await runner.Running;
        (await client.GetStringAsync("/upgrade/status")).Should().Contain("\"state\":\"Succeeded\"").And.Contain("\"version\":\"0.9.45\"");

        (await client.PostAsJsonAsync("/upgrade/image", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound, "there is no operation beyond the three");
        await app.StopAsync();
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
