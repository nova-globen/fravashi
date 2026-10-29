using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Domain.PullRequests;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.PullRequests;
using AwesomeAssertions;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Git;

/// <summary>
/// Plan 2c: one origin remote becomes (provider, API credential). Explicit git_hosts entry first,
/// then built-in host detection, then the transitional legacy single-source fallback.
/// </summary>
public sealed class GitHostAccessTests
{
    private readonly IGitHostStore _hosts = Substitute.For<IGitHostStore>();
    private readonly ISecretStore _secrets = Substitute.For<ISecretStore>();
    private readonly IProviderConnectionStore _connections = Substitute.For<IProviderConnectionStore>();
    private readonly IConfigurationStore _settings = Substitute.For<IConfigurationStore>();

    private GitHostAccess Access => new(_hosts, _secrets, _connections, _settings);

    [Fact]
    public void A_git_hosts_entry_wins_and_supplies_provider_and_token()
    {
        _hosts.Get("forge.example.test").Returns(
            new GitHostRecord("forge.example.test", OnboardingProviders.Forgejo, "githost:forge.example.test:token", DateTimeOffset.UtcNow));
        _secrets.Get("githost:forge.example.test:token").Returns("forgejo-pat");
        // The legacy flag says nothing; the entry alone must carry the resolution.
        _settings.Get("Onboarding:Source:Provider").Returns((string?)null);

        var resolution = Access.Resolve(RemoteInfo.Parse("https://forge.example.test/nova-globen/fravashi.git"));

        resolution.Provider.Should().Be(PullRequestProvider.Forgejo);
        resolution.Credential!.AccessToken.Should().Be("forgejo-pat");
        resolution.Host.Should().Be("forge.example.test");
    }

    [Fact]
    public void An_entry_beats_the_legacy_flag_even_for_builtin_hosts()
    {
        _hosts.Get("github.example.com").Returns(
            new GitHostRecord("github.example.com", OnboardingProviders.GitHub, "githost:github.example.com:token", DateTimeOffset.UtcNow));
        _secrets.Get("githost:github.example.com:token").Returns("ghes-pat");

        var resolution = Access.Resolve(RemoteInfo.Parse("https://github.example.com/acme/widgets.git"));

        resolution.Provider.Should().Be(PullRequestProvider.GitHub);
        resolution.Credential!.AccessToken.Should().Be("ghes-pat");
    }

    [Fact]
    public void Builtin_detection_falls_back_to_the_legacy_source_credential()
    {
        _hosts.Get(Arg.Any<string>()).Returns((GitHostRecord?)null);
        _connections.GetByKind(ProviderKind.Source).Returns(
            [new ProviderConnection("source", ProviderKind.Source, "https://github.com/acme/widgets", "source:git", "ok", DateTimeOffset.UtcNow)]);
        _secrets.Get("source:git").Returns("legacy-pat");

        var resolution = Access.Resolve(RemoteInfo.Parse("https://github.com/acme/widgets.git"));

        resolution.Provider.Should().Be(PullRequestProvider.GitHub);
        resolution.Credential!.AccessToken.Should().Be("legacy-pat");
    }

    [Fact]
    public void Unknown_host_with_the_legacy_forgejo_flag_resolves_forgejo()
    {
        _hosts.Get(Arg.Any<string>()).Returns((GitHostRecord?)null);
        _settings.Get("Onboarding:Source:Provider").Returns(OnboardingProviders.Forgejo);
        _connections.GetByKind(ProviderKind.Source).Returns(
            [new ProviderConnection("source", ProviderKind.Source, "https://forge.example.test/x/y", "source:git", "ok", DateTimeOffset.UtcNow)]);
        _secrets.Get("source:git").Returns("legacy-forgejo-pat");

        var resolution = Access.Resolve(RemoteInfo.Parse("https://forge.example.test/nova-globen/fravashi.git"));

        resolution.Provider.Should().Be(PullRequestProvider.Forgejo);
        resolution.Credential!.AccessToken.Should().Be("legacy-forgejo-pat");
    }

    [Fact]
    public void Unknown_host_without_entry_or_flag_is_none()
    {
        _hosts.Get(Arg.Any<string>()).Returns((GitHostRecord?)null);
        _settings.Get("Onboarding:Source:Provider").Returns((string?)null);

        var resolution = Access.Resolve(RemoteInfo.Parse("https://forge.example.test/nova-globen/fravashi.git"));

        resolution.Provider.Should().Be(PullRequestProvider.None);
        resolution.Credential.Should().BeNull();
        resolution.Host.Should().Be("forge.example.test");
    }

    [Fact]
    public void A_missing_secret_resolves_the_provider_with_a_null_credential()
    {
        _hosts.Get("forge.example.test").Returns(
            new GitHostRecord("forge.example.test", OnboardingProviders.Forgejo, "githost:forge.example.test:token", DateTimeOffset.UtcNow));
        _secrets.Get("githost:forge.example.test:token").Returns((string?)null);

        var resolution = Access.Resolve(RemoteInfo.Parse("https://forge.example.test/a/b.git"));

        resolution.Provider.Should().Be(PullRequestProvider.Forgejo);
        resolution.Credential.Should().BeNull();
    }

    [Fact]
    public void An_unparsed_remote_resolves_none()
    {
        var resolution = Access.Resolve(RemoteInfo.None);

        resolution.Provider.Should().Be(PullRequestProvider.None);
        resolution.Credential.Should().BeNull();
        _hosts.DidNotReceive().Get(Arg.Any<string>());
    }
}
