using AwesomeAssertions;
using Fravashi.Application.Git;
using Fravashi.Application.Releases;
using Fravashi.Domain.Git;
using Xunit;

namespace Fravashi.Application.Tests.Releases;

/// <summary>Work item #368: a release tag is created only in the release shape, never over an existing tag, and pushed alone.</summary>
public class ReleaseTaggerTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private readonly ReleaseGitFake _git = new();

    private ReleaseTagger Tagger(params string[] tags)
    {
        _git.Tags.AddRange(tags.Select(t => new GitTag(t, Commit, DateTimeOffset.UnixEpoch, string.Empty)));
        return new ReleaseTagger(_git, _git);
    }

    [Fact]
    public async Task Creating_names_the_release_title_as_the_message_and_pushes_nothing()
    {
        var outcome = await Tagger("v0.9.96").CreateAsync("v0.9.97", Commit);

        outcome.Done.Should().BeTrue();
        _git.Created.Should().Equal(("v0.9.97", Commit, "Release v0.9.97"));
        _git.Pushed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tag_name_that_already_exists_is_refused_before_git_is_asked_to_create_it()
    {
        var outcome = await Tagger("v0.9.97").CreateAsync("v0.9.97", Commit);

        outcome.Done.Should().BeFalse();
        outcome.Message.Should().Contain("already exists");
        _git.Created.Should().BeEmpty();
    }

    [Theory]
    [InlineData("v0.9")]
    [InlineData("v0.9.97:refs/heads/main")]
    [InlineData("--force")]
    public async Task A_name_outside_the_release_shape_is_refused(string name)
    {
        var outcome = await Tagger().CreateAsync(name, Commit);

        outcome.Done.Should().BeFalse();
        _git.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Pushing_sends_that_one_tag_and_refuses_one_that_does_not_exist()
    {
        var tagger = Tagger("v0.9.97");

        (await tagger.PushAsync("v0.9.97")).Done.Should().BeTrue();
        (await tagger.PushAsync("v0.9.98")).Done.Should().BeFalse();

        _git.Pushed.Should().Equal("v0.9.97");
    }

    [Fact]
    public async Task A_network_failure_says_trying_again_is_safe()
    {
        _git.PushOutcome = new GitSyncOutcome(false, "could not resolve host") { Failure = GitSyncFailure.Network };

        var outcome = await Tagger("v0.9.97").PushAsync("v0.9.97");

        outcome.Done.Should().BeFalse();
        outcome.Message.Should().Contain("Trying again is safe");
    }
}
