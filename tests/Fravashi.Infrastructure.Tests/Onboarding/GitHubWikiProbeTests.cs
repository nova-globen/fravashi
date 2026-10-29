using Fravashi.Application.Onboarding;
using Fravashi.Infrastructure.Onboarding;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding;

/// <summary>
/// Unit tests for the GitHub wiki probe's honesty mapping: how an <c>ls-remote</c> outcome and a
/// <see cref="GitWikiRoundTripRunner"/> result become Read/Write/Delete checks. The git process is faked here
/// (a canned <c>ls-remote</c> result and a canned round-trip result) so <b>no test contacts a live GitHub or
/// shells out to git</b>. The real git round-trip is exercised offline against a local bare repo in
/// <see cref="GitWikiRoundTripRunnerTests"/>.
/// </summary>
public class GitHubWikiProbeTests
{
    private const string RepoUrl = "https://github.com/nova-globen/agent";

    private sealed class FakeGit(GitLsRemoteRunner.LsRemoteResult result) : GitLsRemoteRunner
    {
        public string? LastUrl { get; private set; }

        public override Task<LsRemoteResult> LsRemoteAsync(string authenticatedUrl, CancellationToken cancellationToken = default)
        {
            LastUrl = authenticatedUrl;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeRoundTrip(GitWikiRoundTripRunner.GitWikiRoundTripResult result) : GitWikiRoundTripRunner
    {
        public bool WasCalled { get; private set; }

        public override Task<GitWikiRoundTripResult> RunAsync(string wikiGitUrl, string? token, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(result);
        }
    }

    private static GitLsRemoteRunner.LsRemoteResult LsOk() => new(true, 0, string.Empty);
    private static GitLsRemoteRunner.LsRemoteResult LsFail(string error) => new(false, 128, error);

    private static WikiAccessProbe Probe(FakeGit git, FakeRoundTrip? roundTrip = null) =>
        new(new HttpClient(), git, roundTrip ?? new FakeRoundTrip(default!));

    private static WikiAccessRequest Request(string? baseUrl = RepoUrl, string? token = "pat") =>
        new(OnboardingProviders.GitHubWiki, baseUrl, null, token);

    [Fact]
    public async Task Not_found_read_explains_the_first_page_requirement_and_does_not_show_raw_fatal_text()
    {
        var git = new FakeGit(LsFail("remote: Repository not found.\nfatal: repository 'https://github.com/nova-globen/agent.wiki.git/' not found"));

        var result = await Probe(git).ProbeAsync(Request());

        var read = result.Checks.Single(c => c.Capability == "Read");
        read.Outcome.Should().Be(AccessProbeOutcome.Failed);
        read.Detail.Should().Contain("first page").And.Contain("Wiki tab");
        read.Detail.Should().NotContain("fatal:");
        var write = result.Checks.Single(c => c.Capability == "Write");
        write.Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        write.Detail.Should().Contain("does not exist yet");
        result.Checks.Single(c => c.Capability == "Delete").Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
    }

    [Fact]
    public async Task Bad_credential_read_is_distinguished_from_not_found()
    {
        var git = new FakeGit(LsFail("remote: Invalid username or token. Password authentication is not supported for Git operations.\nfatal: Authentication failed for 'https://github.com/nova-globen/agent.wiki.git/'"));

        var result = await Probe(git).ProbeAsync(Request());

        var read = result.Checks.Single(c => c.Capability == "Read");
        read.Outcome.Should().Be(AccessProbeOutcome.Failed);
        read.Detail.Should().Contain("credential was not accepted");
        read.Detail.Should().NotContain("first page");
    }

    [Fact]
    public async Task The_probe_normalizes_a_wiki_web_url_before_ls_remote()
    {
        // The owner's first mangled input: the probe must ls-remote agent.wiki.git, never agent/wiki.wiki.git.
        var git = new FakeGit(LsOk());

        await Probe(git, new FakeRoundTrip(RoundTrip(cloned: false))).ProbeAsync(
            Request(baseUrl: "https://github.com/nova-globen/agent/wiki"));

        git.LastUrl.Should().Contain("agent.wiki.git").And.NotContain("wiki.wiki.git");
    }

    [Fact]
    public async Task No_token_leaves_write_and_delete_not_verifiable_after_a_passing_read()
    {
        var roundTrip = new FakeRoundTrip(RoundTrip(cloned: true, write: true, delete: true));
        var result = await Probe(new FakeGit(LsOk()), roundTrip).ProbeAsync(Request(token: null));

        result.Checks.Single(c => c.Capability == "Read").Outcome.Should().Be(AccessProbeOutcome.Passed);
        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        result.Checks.Single(c => c.Capability == "Delete").Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        roundTrip.WasCalled.Should().BeFalse("no token means no push round-trip");
    }

    [Fact]
    public async Task Full_round_trip_reports_read_write_delete_verified_and_notes_history_is_append_only()
    {
        var result = await Probe(new FakeGit(LsOk()), new FakeRoundTrip(RoundTrip(cloned: true, write: true, delete: true)))
            .ProbeAsync(Request());

        result.Checks.Single(c => c.Capability == "Read").Outcome.Should().Be(AccessProbeOutcome.Passed);
        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.Passed);
        var delete = result.Checks.Single(c => c.Capability == "Delete");
        delete.Outcome.Should().Be(AccessProbeOutcome.Passed);
        delete.Detail.Should().Contain("append-only");
    }

    [Fact]
    public async Task Write_push_rejected_reports_write_failed_and_delete_not_attempted()
    {
        var result = await Probe(new FakeGit(LsOk()),
                new FakeRoundTrip(RoundTrip(cloned: true, write: false, writeDetail: "remote: protected branch hook declined")))
            .ProbeAsync(Request());

        var write = result.Checks.Single(c => c.Capability == "Write");
        write.Outcome.Should().Be(AccessProbeOutcome.Failed);
        write.Detail.Should().Contain("protected branch hook declined");
        result.Checks.Single(c => c.Capability == "Delete").Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
    }

    [Fact]
    public async Task Delete_push_failure_reports_the_leftover_probe_page_with_its_wiki_url()
    {
        var result = await Probe(new FakeGit(LsOk()),
                new FakeRoundTrip(RoundTrip(cloned: true, write: true, delete: false,
                    deleteDetail: "remote: rejected", fileName: "Fravashi-access-probe-20260716T000000Z.md")))
            .ProbeAsync(Request());

        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.Passed);
        var delete = result.Checks.Single(c => c.Capability == "Delete");
        delete.Outcome.Should().Be(AccessProbeOutcome.Failed);
        delete.Detail.Should().Contain("left behind")
            .And.Contain("https://github.com/nova-globen/agent/wiki/Fravashi-access-probe-20260716T000000Z");
    }

    [Fact]
    public async Task Clone_failure_reports_write_failed_and_delete_not_attempted()
    {
        var result = await Probe(new FakeGit(LsOk()),
                new FakeRoundTrip(RoundTrip(cloned: false, cloneDetail: "fatal: could not read from remote")))
            .ProbeAsync(Request());

        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.Failed);
        result.Checks.Single(c => c.Capability == "Write").Detail.Should().Contain("could not be cloned");
        result.Checks.Single(c => c.Capability == "Delete").Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
    }

    private static GitWikiRoundTripRunner.GitWikiRoundTripResult RoundTrip(
        bool cloned,
        bool write = false,
        bool delete = false,
        string? cloneDetail = null,
        string? writeDetail = null,
        string? deleteDetail = null,
        string fileName = "Fravashi-access-probe-test.md") =>
        new(cloned, cloneDetail, write, writeDetail, delete, deleteDetail, fileName, TokenFoundOnDisk: false);
}
