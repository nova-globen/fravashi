using Fravashi.Domain.PullRequests;
using Fravashi.Infrastructure.PullRequests;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests;

public class RemoteInfoTests
{
    [Theory]
    [InlineData("https://github.com/acme/widgets.git", "acme", "widgets")]
    [InlineData("git@github.com:acme/widgets.git", "acme", "widgets")]
    [InlineData("https://github.com/acme/widgets", "acme", "widgets")]
    public void Parses_github(string url, string owner, string repo)
    {
        var info = RemoteInfo.Parse(url);
        info.Provider.Should().Be(PullRequestProvider.GitHub);
        info.Owner.Should().Be(owner);
        info.Repo.Should().Be(repo);
        info.Host.Should().Be("github.com");
    }

    [Theory]
    [InlineData("https://dev.azure.com/acme/Platform/_git/widgets", "acme", "Platform", "widgets", "dev.azure.com")]
    [InlineData("git@ssh.dev.azure.com:v3/acme/Platform/widgets", "acme", "Platform", "widgets", "ssh.dev.azure.com")]
    [InlineData("https://acme.visualstudio.com/Platform/_git/widgets", "acme", "Platform", "widgets", "acme.visualstudio.com")]
    // The legacy Services SSH remote (backlog 76): the org lives in the host behind a "vs-ssh."
    // prefix AND again in the path after v3. Read as the web form it parsed to
    // Owner=vs-ssh.acme, Project=v3, Repo=acme, which passed the Services guard and then 404d.
    [InlineData("git@vs-ssh.acme.visualstudio.com:v3/acme/Platform/widgets", "acme", "Platform", "widgets", "vs-ssh.acme.visualstudio.com")]
    [InlineData("git@vs-ssh.acme.visualstudio.com:v3/acme/Platform/widgets.git", "acme", "Platform", "widgets", "vs-ssh.acme.visualstudio.com")]
    [InlineData("ssh://vs-ssh.acme.visualstudio.com/v3/acme/Platform/widgets", "acme", "Platform", "widgets", "vs-ssh.acme.visualstudio.com")]
    // The spelling Microsoft documents: the bare vs-ssh host with the org as the SSH user.
    [InlineData("acme@vs-ssh.visualstudio.com:v3/acme/Platform/widgets", "acme", "Platform", "widgets", "vs-ssh.visualstudio.com")]
    public void Parses_azure_devops(string url, string org, string project, string repo, string host)
    {
        var info = RemoteInfo.Parse(url);
        info.Provider.Should().Be(PullRequestProvider.AzureDevOps);
        info.Owner.Should().Be(org);
        info.Project.Should().Be(project);
        info.Repo.Should().Be(repo);
        info.Host.Should().Be(host);
    }

    [Theory]
    [InlineData("https://forge.example.test/nova-globen/fravashi.git", "forge.example.test", "nova-globen", "fravashi")]
    [InlineData("git@gitlab.com:acme/widgets.git", "gitlab.com", "acme", "widgets")]
    public void Unknown_host_keeps_the_coordinates_with_no_provider(string url, string host, string owner, string repo)
    {
        // Plan 2c: an unrecognized host is NOT an error; the git_hosts registry decides its
        // provider later. The parse still surfaces host + owner/repo.
        var info = RemoteInfo.Parse(url);
        info.Provider.Should().Be(PullRequestProvider.None);
        info.Host.Should().Be(host);
        info.Owner.Should().Be(owner);
        info.Repo.Should().Be(repo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/only-one-segment")]
    public void Unparseable_is_none_with_no_host(string? url)
    {
        var info = RemoteInfo.Parse(url);
        info.Provider.Should().Be(PullRequestProvider.None);
        info.Host.Should().BeEmpty();
    }
}
