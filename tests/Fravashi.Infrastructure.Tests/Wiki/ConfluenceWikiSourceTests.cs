using AwesomeAssertions;
using Fravashi.Application.Wiki;
using Fravashi.Infrastructure.Wiki;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Wiki;

/// <summary>
/// Backlog 12, the third time it was met in the owner's install (2026-09-21, v0.9.25). The connection named
/// the owner's own space, a PERSONAL space. The access check wrote a page to it and said Verified; the sync lists
/// team spaces only, kept none, asked for no pages, and said "Synced 0 page(s)". The studio log showed exactly
/// one request, the space list. A space somebody NAMED is read whatever kind it is.
/// </summary>
public sealed class ConfluenceWikiSourceTests
{
    [Fact]
    public async Task The_space_the_connection_names_is_synced_even_when_it_is_a_personal_one()
    {
        var reader = new FakeReader(teamSpaces: [], pagesBySpace: new() { ["~owner"] = ["Notes"] });

        var pages = await new ConfluenceWikiSource(reader, "~owner").FetchAllAsync(null);

        pages.Select(p => p.Title).Should().Equal("Notes");
    }

    [Fact]
    public async Task The_team_spaces_are_still_synced_and_a_named_team_space_is_not_read_twice()
    {
        var reader = new FakeReader(
            teamSpaces: ["TEAM", "KB"],
            pagesBySpace: new() { ["TEAM"] = ["Plan"], ["KB"] = ["How to"], ["~owner"] = ["Notes"] });

        var pages = await new ConfluenceWikiSource(reader, "team").FetchAllAsync(null);

        pages.Select(p => p.Title).Should().BeEquivalentTo(["Plan", "How to"]);
        reader.ListedSpaces.Should().Equal("TEAM", "KB");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task With_no_space_named_only_the_team_spaces_are_read(string? named)
    {
        var reader = new FakeReader(teamSpaces: ["TEAM"], pagesBySpace: new() { ["TEAM"] = ["Plan"], ["~owner"] = ["Notes"] });

        var pages = await new ConfluenceWikiSource(reader, named).FetchAllAsync(null);

        pages.Select(p => p.Title).Should().Equal("Plan");
    }

    private sealed class FakeReader(string[] teamSpaces, Dictionary<string, string[]> pagesBySpace) : IConfluenceReader
    {
        public List<string> ListedSpaces { get; } = [];

        public bool IsConfigured => true;

        public Task<IReadOnlyList<ConfluenceSpaceSummary>> ListSpacesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConfluenceSpaceSummary>>(
                [.. teamSpaces.Select(k => new ConfluenceSpaceSummary("id-" + k, k, k))]);

        public Task<IReadOnlyList<ConfluencePageSummary>> ListPagesAsync(string spaceKey, CancellationToken cancellationToken = default)
        {
            ListedSpaces.Add(spaceKey);
            var titles = pagesBySpace.FirstOrDefault(p => string.Equals(p.Key, spaceKey, StringComparison.OrdinalIgnoreCase)).Value ?? [];
            return Task.FromResult<IReadOnlyList<ConfluencePageSummary>>(
                [.. titles.Select(t => new ConfluencePageSummary(spaceKey + "/" + t, t, spaceKey))]);
        }

        public Task<ConfluencePage?> GetPageAsync(string pageId, CancellationToken cancellationToken = default)
        {
            var parts = pageId.Split('/');
            return Task.FromResult<ConfluencePage?>(new ConfluencePage(pageId, parts[1], parts[0], "<p>body</p>"));
        }
    }
}
