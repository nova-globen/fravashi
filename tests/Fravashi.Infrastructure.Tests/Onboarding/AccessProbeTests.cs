using System.ComponentModel;
using System.Net;
using Fravashi.Application.Onboarding;
using Fravashi.Infrastructure.Onboarding;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding;

/// <summary>
/// Unit tests for the onboarding probes. These exercise ONLY the branches that make no network or process
/// call to a live service - URL building, input validation, the local/not-supported paths, and the git read
/// via a fake <see cref="GitLsRemoteRunner"/>. No test here contacts a real GitHub/ADO/Jira/Confluence.
/// </summary>
public class AccessProbeTests
{
    private sealed class FakeGit(bool success) : GitLsRemoteRunner
    {
        public override Task<LsRemoteResult> LsRemoteAsync(string authenticatedUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LsRemoteResult(success, success ? 0 : 128, success ? string.Empty : "auth failed"));
    }

    [Theory]
    [InlineData("https://github.com/o/r.git", "tok", "https://x-access-token:tok@github.com/o/r.git")]
    [InlineData("https://user:old@github.com/o/r.git", "tok", "https://x-access-token:tok@github.com/o/r.git")]
    [InlineData("https://github.com/o/r.git", "", "https://github.com/o/r.git")]
    public void BuildAuthenticatedUrl_injects_the_token_and_strips_existing_credentials(string url, string token, string expected)
    {
        // Lives on the ls-remote runner since the source step retired (plan 2c); the wiki probe
        // still builds its round-trip URLs with it.
        GitLsRemoteRunner.BuildAuthenticatedUrl(url, token).Should().Be(expected);
    }

    [Fact]
    public async Task Board_probe_reports_not_supported_for_the_local_board()
    {
        var probe = new BoardAccessProbe(new HttpClient());

        var result = await probe.ProbeAsync(new BoardAccessRequest(OnboardingProviders.Local, null, null, null, null));

        result.Checks.Should().ContainSingle()
            .Which.Outcome.Should().Be(AccessProbeOutcome.NotSupported);
    }

    [Fact]
    public async Task Board_probe_reports_validation_failure_without_calling_out_when_inputs_are_missing()
    {
        var probe = new BoardAccessProbe(new HttpClient());

        var result = await probe.ProbeAsync(new BoardAccessRequest(OnboardingProviders.AzureDevOps, null, null, null, null));

        result.Checks.Should().ContainSingle()
            .Which.Mechanism.Should().Be("input validation");
    }

    [Fact]
    public void LsRemote_start_failure_maps_a_missing_git_binary_to_a_clear_message()
    {
        // A missing git binary surfaces from Process.Start as a Win32Exception ("No such file or directory").
        var message = GitLsRemoteRunner.DescribeStartFailure(new Win32Exception("No such file or directory"));

        message.Should().Contain("git is not available in this environment");
        message.Should().NotContain("No such file or directory");
    }

    [Fact]
    public void LsRemote_start_failure_preserves_the_message_for_other_exceptions()
    {
        var message = GitLsRemoteRunner.DescribeStartFailure(new InvalidOperationException("boom"));

        message.Should().Be("boom");
    }

    [Fact]
    public async Task Wiki_probe_reports_not_supported_for_the_local_wiki()
    {
        var probe = new WikiAccessProbe(new HttpClient(), new FakeGit(success: true));

        var result = await probe.ProbeAsync(new WikiAccessRequest(OnboardingProviders.Local, null, null, null));

        result.Checks.Should().ContainSingle()
            .Which.Outcome.Should().Be(AccessProbeOutcome.NotSupported);
    }
}
