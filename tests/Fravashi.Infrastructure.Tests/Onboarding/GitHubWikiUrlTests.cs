using Fravashi.Infrastructure.Onboarding;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding;

/// <summary>
/// Exhaustive tests for the GitHub wiki URL normalizer. The live defect: the old probe blindly appended
/// <c>.wiki.git</c>, so a wiki web URL (<c>…/agent/wiki</c>) became <c>agent/wiki.wiki.git</c> and a wiki git
/// URL (<c>…/agent.wiki.git</c>) became <c>agent.wiki.wiki.git</c> - both 404. Every accepted input must
/// reduce to the one canonical <c>…/agent.wiki.git</c>.
/// </summary>
public class GitHubWikiUrlTests
{
    [Theory]
    // Plain repo URL.
    [InlineData("https://github.com/nova-globen/agent")]
    [InlineData("https://github.com/nova-globen/agent/")]
    // Repo git URL.
    [InlineData("https://github.com/nova-globen/agent.git")]
    [InlineData("https://github.com/nova-globen/agent.git/")]
    // Wiki web URL - the owner's first mangled input.
    [InlineData("https://github.com/nova-globen/agent/wiki")]
    [InlineData("https://github.com/nova-globen/agent/wiki/")]
    [InlineData("https://github.com/nova-globen/agent/wiki/Home")]
    [InlineData("https://github.com/nova-globen/agent/wiki/Some-Page")]
    // Wiki git URL - the owner's second mangled input.
    [InlineData("https://github.com/nova-globen/agent.wiki.git")]
    [InlineData("https://github.com/nova-globen/agent.wiki.git/")]
    // Surrounding whitespace and mixed-case suffixes.
    [InlineData("  https://github.com/nova-globen/agent  ")]
    [InlineData("https://github.com/nova-globen/agent.GIT")]
    [InlineData("https://github.com/nova-globen/agent.WIKI.GIT")]
    public void All_accepted_forms_reduce_to_the_same_wiki_git_url(string input)
    {
        var target = GitHubWikiUrl.Normalize(input);

        target.WikiGitUrl.Should().Be("https://github.com/nova-globen/agent.wiki.git");
        target.WikiWebUrl.Should().Be("https://github.com/nova-globen/agent/wiki");
        target.RepoWebUrl.Should().Be("https://github.com/nova-globen/agent");
    }

    [Fact]
    public void Owner_mangled_input_one_no_longer_becomes_agent_slash_wiki_dot_wiki_dot_git()
    {
        // Was probed as "agent/wiki.wiki.git" → "Not Found".
        GitHubWikiUrl.Normalize("https://github.com/nova-globen/agent/wiki").WikiGitUrl
            .Should().Be("https://github.com/nova-globen/agent.wiki.git")
            .And.NotContain("wiki.wiki.git");
    }

    [Fact]
    public void Owner_mangled_input_two_no_longer_becomes_agent_dot_wiki_dot_wiki_dot_git()
    {
        // Was probed as "agent.wiki.wiki.git" → "Repository not found".
        GitHubWikiUrl.Normalize("https://github.com/nova-globen/agent.wiki.git").WikiGitUrl
            .Should().Be("https://github.com/nova-globen/agent.wiki.git")
            .And.NotContain("wiki.wiki.git");
    }

    [Fact]
    public void A_repo_literally_named_wiki_is_not_mistaken_for_the_wiki_suffix()
    {
        var target = GitHubWikiUrl.Normalize("https://github.com/acme/wiki");

        target.WikiGitUrl.Should().Be("https://github.com/acme/wiki.wiki.git");
        target.RepoWebUrl.Should().Be("https://github.com/acme/wiki");
    }

    [Fact]
    public void Enterprise_hosts_are_supported_host_agnostically()
    {
        var target = GitHubWikiUrl.Normalize("https://git.example.com/team/repo/wiki");

        target.WikiGitUrl.Should().Be("https://git.example.com/team/repo.wiki.git");
        target.WikiWebUrl.Should().Be("https://git.example.com/team/repo/wiki");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://github.com/onlyowner")]
    [InlineData("https://github.com/")]
    [InlineData("not a url at all")]
    public void Inputs_without_an_owner_and_repo_are_rejected(string input)
    {
        var act = () => GitHubWikiUrl.Normalize(input);

        act.Should().Throw<FormatException>();
    }
}
