using AwesomeAssertions;
using Fravashi.Application.Projects;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Tracker;
using Fravashi.Infrastructure.Tracker.Atlassian;
using Fravashi.Infrastructure.Wiki;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Tracker;

/// <summary>
/// Backlog 142: a project chooses the providers it follows, and NONE means none, whatever the install is connected
/// to. Held where the answer is decided (the gateways, the publisher), not only where it is shown.
/// </summary>
public sealed class ProjectProviderChoiceTests : IDisposable
{
    private readonly SqliteTestContext _context = new();
    private readonly SqliteProjectProviders _choices;
    private readonly ProjectId _project = new("dogfood");

    public ProjectProviderChoiceTests() => _choices = new SqliteProjectProviders(_context.Factory);

    public void Dispose() => _context.Dispose();

    private ConfiguredProviderGateways Gateways()
    {
        var options = Substitute.For<IOptionsMonitor<AtlassianConfig>>();
        options.CurrentValue.Returns(new AtlassianConfig
        {
            JiraKey = "token", JiraDomain = "acme.atlassian.net", JiraEmail = "me@acme.test", JiraProject = "DD",
        });
        return new ConfiguredProviderGateways(options, new AtlassianJiraGateway(new HttpClient(), options), _choices);
    }

    [Fact]
    public void A_project_that_never_chose_follows_the_install_and_one_that_chose_none_follows_nothing()
    {
        Gateways().For(_project).Should().NotBeNull();

        _choices.Set(_project, new ProjectProviderChoice(ProviderChoice.None, ProviderChoice.Install));

        Gateways().For(_project).Should().BeNull();
        Gateways().For(ProjectId.Default).Should().NotBeNull("another project's choice is its own");
        _choices.For(_project).Should().Be(new ProjectProviderChoice(ProviderChoice.None, ProviderChoice.Install));
    }

    [Fact]
    public async Task A_project_that_chose_no_wiki_is_refused_by_the_publisher_and_nothing_is_sent()
    {
        var wiki = new SqliteWikiLibrary(_context.Factory, TimeProvider.System, _project);
        var document = (await wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Plan, Title = "Kept here" })).Value;
        var site = Substitute.For<IWikiPageGateway>();
        site.IsConfigured.Returns(true);
        site.Provider.Returns("Confluence");
        var target = Substitute.For<IWikiPublishTarget>();
        target.Space.Returns("TEAM");
        var publisher = new WikiPublisher(
            wiki, new SqliteWikiPublicationStore(_context.Factory), site, target, TimeProvider.System, choices: _choices);
        publisher.CanPublishFor(_project).Should().BeTrue();

        _choices.Set(_project, new ProjectProviderChoice(ProviderChoice.Install, ProviderChoice.None));
        var report = await publisher.PublishAsync(document.Id);

        publisher.CanPublishFor(_project).Should().BeFalse();
        report.Refusal.Should().Contain("publishes to no wiki");
        await site.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default!);
    }
}
