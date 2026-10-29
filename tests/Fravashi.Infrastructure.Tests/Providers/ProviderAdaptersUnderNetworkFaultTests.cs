using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Application.Network;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Tracker.Atlassian;
using Fravashi.Infrastructure.Tracker.AzureDevOps;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Testing.Network;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Providers;

/// <summary>
/// Backlog 232 (plan @58): each of the four provider adapters, on #228's scripted network, classifies a failed call by
/// the ONE shared rule. A call that provably never left (a dead resolver, a refused connect, no route) is Unreachable and
/// goes back on the queue; a call whose request may have arrived (a reset after the body was written, the client's own
/// timeout) is Indeterminate and is looked up, never resent. Each result carries the classified failure, which is what
/// lets the outage be named.
/// </summary>
public sealed class ProviderAdaptersUnderNetworkFaultTests
{
    public static TheoryData<string> Adapters => ["jira", "ado-boards", "confluence", "ado-wiki"];

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task A_dead_resolver_is_unreachable_and_says_the_name_was_not_resolved(string adapter)
    {
        var (result, handler) = await CreateAsync(adapter, NetworkBehaviour.NameFails);

        result.Outcome.Should().Be(ProviderOutcome.Unreachable);
        result.Network!.Kind.Should().Be(NetworkFailureKind.NameNotResolved);
        result.Reason.Should().Contain("name not resolved").And.Contain("nothing was sent");
        handler.Arrived.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task A_refused_connect_is_unreachable(string adapter)
    {
        var (result, handler) = await CreateAsync(adapter, NetworkBehaviour.Refuses);

        result.Outcome.Should().Be(ProviderOutcome.Unreachable);
        result.Network!.Kind.Should().Be(NetworkFailureKind.Refused);
        handler.Arrived.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task No_route_is_unreachable(string adapter)
    {
        var (result, _) = await CreateAsync(adapter, NetworkBehaviour.NoRoute);

        result.Outcome.Should().Be(ProviderOutcome.Unreachable);
        result.Network!.NeverLeft.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task A_reset_after_the_body_was_written_is_indeterminate_never_unreachable(string adapter)
    {
        var (result, handler) = await CreateAsync(adapter, NetworkBehaviour.ResetAfterWrite);

        result.Outcome.Should().Be(ProviderOutcome.Indeterminate, "the request arrived; resending a creation would make two");
        result.Network!.Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer);
        result.Reason.Should().Contain("may have arrived");
        handler.Arrived.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task The_clients_own_timeout_is_indeterminate(string adapter)
    {
        var (result, _) = await CreateAsync(adapter, NetworkBehaviour.Hangs);

        result.Outcome.Should().Be(ProviderOutcome.Indeterminate);
        result.Network?.NeverLeft.Should().NotBe(true);
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task A_cancellation_the_caller_asked_for_is_the_callers_and_not_classified(string adapter)
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var handler = new ScriptedHttpHandler(new NetworkScript(new FakeNetworkClock(), new NetworkPhase(NetworkBehaviour.Healthy)));

        var act = () => CallAsync(adapter, handler, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>One adapter's creation over a network that does <paramref name="behaviour"/> with every call.</summary>
    private static async Task<(Result Result, ScriptedHttpHandler Handler)> CreateAsync(string adapter, NetworkBehaviour behaviour)
    {
        var handler = new ScriptedHttpHandler(new NetworkScript(new FakeNetworkClock(), new NetworkPhase(behaviour)));
        return (await CallAsync(adapter, handler, CancellationToken.None), handler);
    }

    private sealed record Result(ProviderOutcome Outcome, NetworkFailure? Network, string? Reason);

    // Each adapter's creation, which is the write that can be made twice. Its first request meets the network (for the
    // Azure DevOps adapters and Confluence that is a read they make first), and the failure is carried out unchanged.
    private static async Task<Result> CallAsync(
        string adapter, ScriptedHttpHandler handler, CancellationToken ct)
    {
        var http = new HttpClient(handler);
        switch (adapter)
        {
            case "jira":
            {
                var options = Substitute.For<IOptionsMonitor<AtlassianConfig>>();
                options.CurrentValue.Returns(new AtlassianConfig
                {
                    JiraKey = "token", JiraDomain = "acme.atlassian.net", JiraEmail = "me@acme.test", JiraProject = "DD",
                });
                var r = await new AtlassianJiraGateway(http, options).CreateAsync("""{"Title":"x"}""", "op-1", ct);
                return new Result(r.Outcome, r.Network, r.Reason);
            }

            case "ado-boards":
            {
                var connections = Substitute.For<IProviderConnectionStore>();
                connections.Get("board").Returns(new ProviderConnection(
                    "board", ProviderKind.Board, "https://dev.azure.com/acme/", "board:token", "Verified", DateTimeOffset.UtcNow));
                var settings = Substitute.For<IConfigurationStore>();
                settings.Get("Onboarding:Board:Provider").Returns(OnboardingProviders.AzureDevOps);
                settings.Get("Onboarding:Board:Project").Returns("Studio Project");
                var secrets = Substitute.For<ISecretStore>();
                secrets.Get("board:token").Returns("my-pat");
                var r = await new AzureDevOpsBoardsGateway(http, new AzureDevOpsBoardConnection(connections, settings, secrets))
                    .CreateAsync("""{"Title":"x"}""", "op-1", ct);
                return new Result(r.Outcome, r.Network, r.Reason);
            }

            case "confluence":
            {
                var options = Substitute.For<IOptionsMonitor<AtlassianConfig>>();
                options.CurrentValue.Returns(new AtlassianConfig
                {
                    JiraKey = "token", JiraDomain = "acme.atlassian.net", JiraEmail = "me@acme.test",
                });
                var r = await new AtlassianConfluencePageGateway(http, options).CreateAsync("TEAM", "Rollout plan", "<p>x</p>", null, ct);
                return new Result(r.Outcome, r.Network, r.Reason);
            }

            default:
            {
                var connections = Substitute.For<IProviderConnectionStore>();
                connections.Get("wiki").Returns(new ProviderConnection(
                    "wiki", ProviderKind.Wiki, "https://dev.azure.com/acme/Studio", "wiki:token", "Verified", DateTimeOffset.UtcNow));
                var settings = Substitute.For<IConfigurationStore>();
                settings.Get("Onboarding:Wiki:Provider").Returns(OnboardingProviders.AzureDevOpsWiki);
                var secrets = Substitute.For<ISecretStore>();
                secrets.Get("wiki:token").Returns("wiki-pat");
                var r = await new AzureDevOpsWikiPageGateway(http, connections, settings, secrets)
                    .CreateAsync("Studio.wiki", "Rollout plan", "x", null, ct);
                return new Result(r.Outcome, r.Network, r.Reason);
            }
        }
    }
}
