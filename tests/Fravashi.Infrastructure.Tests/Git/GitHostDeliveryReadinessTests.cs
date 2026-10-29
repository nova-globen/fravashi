using Fravashi.Application.Git;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Git;
using AwesomeAssertions;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Git;

/// <summary>
/// Backlog 298 (ADR 0031 decision 3): whether branch + pull request delivery has a token for the
/// workspace's origin, over the same resolution the PR panel uses.
/// </summary>
public sealed class GitHostDeliveryReadinessTests
{
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly IGitHostStore _hosts = Substitute.For<IGitHostStore>();
    private readonly ISecretStore _secrets = Substitute.For<ISecretStore>();
    private readonly IProviderConnectionStore _connections = Substitute.For<IProviderConnectionStore>();
    private readonly IConfigurationStore _settings = Substitute.For<IConfigurationStore>();

    private GitHostDeliveryReadiness Readiness => new(
        _git, new GitHostAccess(_hosts, _secrets, _connections, _settings));

    [Fact]
    public async Task A_resolved_host_with_a_stored_token_has_one()
    {
        _git.GetRemoteUrlAsync().Returns(Task.FromResult<string?>("https://forge.example.test/nova-globen/fravashi.git"));
        _hosts.Get("forge.example.test").Returns(
            new GitHostRecord("forge.example.test", OnboardingProviders.Forgejo, "githost:forge.example.test:token", DateTimeOffset.UtcNow));
        _secrets.Get("githost:forge.example.test:token").Returns("a-token");

        var result = await Readiness.CheckAsync();

        result.HasToken.Should().BeTrue();
        result.Host.Should().Be("forge.example.test");
    }

    [Fact]
    public async Task A_resolved_host_with_no_stored_token_has_none()
    {
        _git.GetRemoteUrlAsync().Returns(Task.FromResult<string?>("https://forge.example.test/nova-globen/fravashi.git"));
        _hosts.Get(Arg.Any<string>()).Returns((GitHostRecord?)null);
        _settings.Get(Arg.Any<string>()).Returns((string?)null);

        var result = await Readiness.CheckAsync();

        result.HasToken.Should().BeFalse();
        result.Host.Should().Be("forge.example.test");
    }

    [Fact]
    public async Task An_origin_that_does_not_resolve_has_no_host_and_no_token()
    {
        _git.GetRemoteUrlAsync().Returns(Task.FromResult<string?>(null));

        var result = await Readiness.CheckAsync();

        result.HasToken.Should().BeFalse();
        result.Host.Should().BeNull();
    }
}
