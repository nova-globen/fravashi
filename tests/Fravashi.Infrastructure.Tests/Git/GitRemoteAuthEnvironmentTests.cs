using System.Text;
using AwesomeAssertions;
using Fravashi.Application.Git;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Repository;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Git;

/// <summary>
/// Plan 2c: fetch/pull/push build their credential env from the ORIGIN host's stored entry, in
/// the exact header form each provider's git-over-HTTPS accepts.
/// </summary>
public sealed class GitRemoteAuthEnvironmentTests
{
    private sealed class CapturingRunner(string originUrl) : IGitCommandRunner
    {
        public IReadOnlyDictionary<string, string>? SyncEnv { get; private set; }

        public bool IsAvailable() => true;

        public Task<GitCommandResult> RunAsync(
            IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
        {
            if (args.Count >= 2 && args[0] == "remote" && args[1] == "get-url")
            {
                return Task.FromResult(new GitCommandResult(0, originUrl + "\n", string.Empty));
            }

            if (args[0] is "fetch" or "pull" or "push")
            {
                SyncEnv = environment;
                return Task.FromResult(new GitCommandResult(0, string.Empty, string.Empty));
            }

            // rev-parse and friends: pretend a branch with an upstream.
            return Task.FromResult(new GitCommandResult(0, "main\n", string.Empty));
        }
    }

    private static GitRemoteService Service(string originUrl, GitHostRecord? entry, string token, out CapturingRunner runner)
    {
        var hosts = Substitute.For<IGitHostStore>();
        hosts.Get(Arg.Any<string>()).Returns(entry);
        var secrets = Substitute.For<ISecretStore>();
        secrets.Get(Arg.Any<string>()).Returns(CredentialEnvelope.Serialize(StoredCredential.ForPat(token)));
        var connections = Substitute.For<IProviderConnectionStore>();
        connections.GetByKind(Arg.Any<ProviderKind>()).Returns([]);
        var settings = Substitute.For<IConfigurationStore>();
        settings.Get(Arg.Any<string>()).Returns((string?)null);

        runner = new CapturingRunner(originUrl);
        return new GitRemoteService(
            runner, new RepositoryWriteGate(), new GitHostAccess(hosts, secrets, connections, settings));
    }

    [Fact]
    public async Task A_github_host_entry_rides_basic_with_the_x_access_token_user()
    {
        var service = Service(
            "https://github.example.com/acme/widgets.git",
            new GitHostRecord("github.example.com", "GitHub", "githost:github.example.com:token", DateTimeOffset.UnixEpoch),
            "ghes-pat",
            out var runner);

        (await service.FetchAsync()).Succeeded.Should().BeTrue();

        var expected = Convert.ToBase64String(Encoding.ASCII.GetBytes("x-access-token:ghes-pat"));
        runner.SyncEnv!["GIT_CONFIG_KEY_0"].Should().Be("http.extraHeader");
        runner.SyncEnv["GIT_CONFIG_VALUE_0"].Should().Be($"AUTHORIZATION: basic {expected}");
    }

    [Fact]
    public async Task An_ado_host_entry_rides_the_empty_user_basic_pat_form()
    {
        var service = Service(
            "https://dev.azure.com/acme/Platform/_git/widgets",
            new GitHostRecord("dev.azure.com", "AzureDevOps", "githost:dev.azure.com:token", DateTimeOffset.UnixEpoch),
            "ado-pat",
            out var runner);

        (await service.PushAsync()).Succeeded.Should().BeTrue();

        var expected = Convert.ToBase64String(Encoding.ASCII.GetBytes(":ado-pat"));
        runner.SyncEnv!["GIT_CONFIG_VALUE_0"].Should().Be($"AUTHORIZATION: basic {expected}");
    }

    [Fact]
    public async Task No_entry_and_no_legacy_credential_sends_no_header()
    {
        var service = Service(
            "https://forge.example.test/nova-globen/fravashi.git",
            entry: null,
            "unused",
            out var runner);

        (await service.FetchAsync()).Succeeded.Should().BeTrue();

        runner.SyncEnv!.Should().NotContainKey("GIT_CONFIG_VALUE_0");
        runner.SyncEnv["GIT_TERMINAL_PROMPT"].Should().Be("0");
    }
}
