using AwesomeAssertions;
using Fravashi.Application.Git;
using Fravashi.Application.Network;
using Xunit;

namespace Fravashi.Application.Tests.Git;

/// <summary>
/// Backlog 231: git's real stderr (Fixtures, see its README for which were measured) read into slice A's network kinds
/// or a refusal. "Never left" is answered only where the text proves the phase.
/// </summary>
public sealed class GitRemoteStderrTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Git", "Fixtures", name + ".txt"));

    [Theory]
    [InlineData("https-name-not-resolved", NetworkFailureKind.NameNotResolved)]
    [InlineData("git-protocol-name-not-resolved", NetworkFailureKind.NameNotResolved)]
    [InlineData("source-ssh-name-not-resolved", NetworkFailureKind.NameNotResolved)]
    [InlineData("git-protocol-connect-refused", NetworkFailureKind.Refused)]
    [InlineData("source-ssh-connect-refused", NetworkFailureKind.Refused)]
    [InlineData("https-connect-refused", NetworkFailureKind.Unreachable)]
    [InlineData("https-no-route-ipv6", NetworkFailureKind.Unreachable)]
    [InlineData("source-ssh-no-route", NetworkFailureKind.Unreachable)]
    [InlineData("https-black-holed", NetworkFailureKind.TimedOutConnecting)]
    [InlineData("source-ssh-connect-timed-out", NetworkFailureKind.TimedOutConnecting)]
    [InlineData("http-reset-before-push", NetworkFailureKind.ResetBeforeSend)]
    [InlineData("http-empty-reply-before-push", NetworkFailureKind.ResetBeforeSend)]
    [InlineData("source-https-tls-handshake-broke", NetworkFailureKind.ResetBeforeSend)]
    public void A_failure_before_anything_was_sent_never_left(string fixture, NetworkFailureKind expected)
    {
        var verdict = GitRemoteStderr.Classify(Fixture(fixture));

        verdict.Network.Should().Be(expected);
        verdict.NeverLeft.Should().BeTrue();
        verdict.Refusal.Should().Be(GitRefusal.None);
        verdict.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("http-reset-after-pack", "the connection was reset")]
    [InlineData("http-empty-reply-after-pack", "the server closed the connection without answering")]
    [InlineData("http-too-slow-after-pack", "the transfer timed out")]
    [InlineData("source-ssh-reset-after-pack", "the connection was reset")]
    [InlineData("source-ssh-closed", "the connection broke during the transfer")]
    public void A_failure_after_the_pack_went_out_may_have_arrived(string fixture, string reason)
    {
        var verdict = GitRemoteStderr.Classify(Fixture(fixture));

        verdict.Network.Should().Be(NetworkFailureKind.LeftWithNoAnswer);
        verdict.NeverLeft.Should().BeFalse("git printed \"Everything up-to-date\" after some of these, which is not to be believed");
        verdict.Reason.Should().Be(reason);
    }

    [Theory]
    [InlineData("push-non-fast-forward", GitRefusal.Rejected)]
    [InlineData("push-hook-declined", GitRefusal.HookDeclined)]
    [InlineData("http-401-no-prompt", GitRefusal.Authentication)]
    [InlineData("http-401-with-credentials", GitRefusal.Authentication)]
    [InlineData("source-ssh-publickey", GitRefusal.Authentication)]
    [InlineData("http-403", GitRefusal.Permission)]
    [InlineData("source-https-permission", GitRefusal.Permission)]
    [InlineData("http-404", GitRefusal.NotFound)]
    [InlineData("local-not-a-repository", GitRefusal.NotFound)]
    [InlineData("source-https-certificate", GitRefusal.Certificate)]
    [InlineData("http-500", GitRefusal.ServerError)]
    public void An_answer_from_origin_is_a_refusal_not_the_network(string fixture, GitRefusal expected)
    {
        var verdict = GitRemoteStderr.Classify(Fixture(fixture));

        verdict.Refusal.Should().Be(expected);
        verdict.IsNetwork.Should().BeFalse();
    }

    [Fact]
    public void A_diverged_pull_and_an_empty_text_are_neither()
    {
        GitRemoteStderr.Classify(Fixture("pull-ff-only-diverged")).Should().Be(GitRemoteVerdict.Other);
        GitRemoteStderr.Classify("").Should().Be(GitRemoteVerdict.Other);
        GitRemoteStderr.Classify(null).Should().Be(GitRemoteVerdict.Other);
    }

    [Fact]
    public void A_refusal_wins_over_a_network_phrase_in_the_same_text()
    {
        // Origin answered: whatever else broke afterwards, it was reached and said no.
        var text = Fixture("push-non-fast-forward") + "\nfatal: the remote end hung up unexpectedly\n";

        GitRemoteStderr.Classify(text).Refusal.Should().Be(GitRefusal.Rejected);
    }

    [Fact]
    public void Every_fixture_is_read_by_a_case_here()
    {
        var named = new[]
        {
            "https-name-not-resolved", "git-protocol-name-not-resolved", "source-ssh-name-not-resolved",
            "git-protocol-connect-refused", "source-ssh-connect-refused", "https-connect-refused", "https-no-route-ipv6",
            "source-ssh-no-route", "https-black-holed", "source-ssh-connect-timed-out", "http-reset-before-push",
            "http-empty-reply-before-push", "source-https-tls-handshake-broke", "http-reset-after-pack",
            "http-empty-reply-after-pack", "http-too-slow-after-pack", "source-ssh-reset-after-pack", "source-ssh-closed",
            "push-non-fast-forward", "push-hook-declined", "http-401-no-prompt", "http-401-with-credentials",
            "source-ssh-publickey", "http-403", "source-https-permission", "http-404", "local-not-a-repository",
            "source-https-certificate", "http-500", "pull-ff-only-diverged",
        };
        var onDisk = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Git", "Fixtures"), "*.txt")
            .Select(Path.GetFileNameWithoutExtension);

        onDisk.Should().BeEquivalentTo(named);
    }
}
