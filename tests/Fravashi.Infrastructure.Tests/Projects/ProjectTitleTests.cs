using AwesomeAssertions;
using Fravashi.Application.Projects;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Tests.Persistence;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Projects;

/// <summary>Backlog 121: a page names its project only once there is more than one to confuse it with.</summary>
public sealed class ProjectTitleTests : IDisposable
{
    private readonly SqliteTestContext _context = new();

    public void Dispose() => _context.Dispose();

    [Fact]
    public void With_one_project_a_page_title_is_exactly_what_it_was()
    {
        var projects = new SqliteProjectStore(_context.Factory, TimeProvider.System);

        ProjectTitle.With("Work items", projects, DefaultProject.Id).Should().Be("Work items");
    }

    [Fact]
    public void With_a_second_project_every_page_says_whose_it_is()
    {
        var projects = new SqliteProjectStore(_context.Factory, TimeProvider.System);
        var second = projects.Create("Billing rewrite");

        ProjectTitle.With("Work items", projects, second.Id).Should().Be("Work items - Billing rewrite");
        ProjectTitle.With("Wiki", projects, DefaultProject.Id).Should().Be("Wiki - Default");
    }
}
