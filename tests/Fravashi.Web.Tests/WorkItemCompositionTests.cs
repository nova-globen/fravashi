using Fravashi.Application.Projects;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.WorkItems;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Increment B of the canonical artifact model plan: the persistence registration the studio's Program.cs calls
/// wires the tracker resolver and the project store, and the resolver hands out a working local board for the
/// seeded default project on a real, freshly migrated database.
/// </summary>
public sealed class WorkItemCompositionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fravashi-work-item-composition", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task The_persistence_registration_resolves_a_local_board_for_the_default_project()
    {
        var services = new ServiceCollection();
        services.AddFravashiPersistence(new DataDirectory(_directory));
        using var provider = services.BuildServiceProvider();

        var tracker = provider.GetRequiredService<ITrackerResolver>().For(ProjectId.Default);

        Assert.IsType<SqliteLocalBoard>(tracker);
        Assert.Equal(ProjectId.Default, tracker.Project);
        Assert.Equal("local", tracker.ProviderName);
        Assert.NotNull(provider.GetRequiredService<IProjectStore>().Get(ProjectId.Default));

        var created = await tracker.CreateAsync(
            new NewWorkItem { Level = Fravashi.Domain.WorkItems.WorkItemLevel.Task, Title = "Wired", Description = "Proves the composition end to end." },
            MutationContext.User);
        Assert.True(created.IsOk);
        Assert.Equal(1, created.Value.Number);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
