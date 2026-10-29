using Bunit;
using Fravashi.Application.Git;
using Fravashi.Application.Releases;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Git;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Releases;
using Fravashi.Infrastructure.WorkItems;
using Fravashi.Web.Components.WorkItems;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #368: the New release dialog over the real board, release store and wiki, with the repository's git
/// substituted. Every Done item no release has taken is offered ticked; the notes are drafted from the ticked ones;
/// saving against an existing tag stamps and archives exactly the ticked items and writes the Release document; and a
/// new tag is created and pushed only after its own confirmation each, never over a tag that already exists.
/// </summary>
public sealed class NewReleaseDialogTests : IDisposable
{
    private const string Head = "0123456789abcdef0123456789abcdef01234567";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-tests", Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;
    private readonly LocalTrackerResolver _resolver;
    private readonly ITracker _board;
    private readonly Fravashi.Infrastructure.Wiki.SqliteWikiLibrary _wiki;
    private readonly SqliteReleaseStores _releases;
    private readonly WorkItemChanges _changes = new();
    private readonly ReleaseGitFake _git = new();

    public NewReleaseDialogTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _resolver = new LocalTrackerResolver(_factory, TimeProvider.System);
        _board = _resolver.For(ProjectId.Default);
        _wiki = new Fravashi.Infrastructure.Wiki.SqliteWikiLibrary(_factory, TimeProvider.System, ProjectId.Default);
        _releases = new SqliteReleaseStores(_factory, TimeProvider.System);

        _git.Commits.Add(new GitCommit(Head, Head[..7], "The newest commit", "Dev", DateTimeOffset.UnixEpoch));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp folder.
        }
    }

    private BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ITrackerResolver>(_resolver);
        ctx.Services.AddSingleton<IReleaseStores>(_releases);
        ctx.Services.AddSingleton(new ReleaseRecorder(_releases, _resolver, new OneWiki(_wiki), _changes));
        ctx.Services.AddSingleton<Fravashi.Application.Projects.ICurrentProject>(Fravashi.Application.Projects.SingleProject.Instance);
        ctx.Services.AddSingleton<IGitService>(_git);
        ctx.Services.AddSingleton(new ReleaseTagger(_git, _git));
        return ctx;
    }

    private async Task<WorkItem> DoneAsync(string title, WorkItemLevel level = WorkItemLevel.Task)
    {
        var created = await _board.CreateAsync(
            new NewWorkItem
            {
                Level = level,
                Title = title,
                Description = "The problem and the outcome.",
                Status = WorkItemStatus.Done,
                Resolution = WorkItemResolution.Completed,
            },
            MutationContext.User);
        Assert.Equal(TrackerOutcome.Ok, created.Outcome);
        return created.Value;
    }

    private IRenderedComponent<NewReleaseDialog> Render(BunitContext ctx, Action<ReleaseSaved>? saved = null) =>
        ctx.Render<NewReleaseDialog>(p => p.Add(x => x.Saved, EventCallback.Factory.Create<ReleaseSaved>(this, s => saved?.Invoke(s))));

    [Fact]
    public async Task Every_unreleased_done_item_is_offered_ticked_and_the_newest_unrecorded_tag_is_suggested()
    {
        var feature = await DoneAsync("A release option on the board", WorkItemLevel.Feature);
        var fix = await DoneAsync("fix: the toggle sticks");
        await _board.CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Not done", Description = "d", Status = WorkItemStatus.Ready },
            MutationContext.User);
        _git.Tags.Add(new GitTag("v0.9.96", Head, DateTimeOffset.UnixEpoch, string.Empty));
        _git.Tags.Add(new GitTag("v0.9.97", Head, DateTimeOffset.UnixEpoch, string.Empty));

        using var ctx = Context();
        var cut = Render(ctx);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("v0.9.97", cut.Find("[data-testid=release-version]").GetAttribute("value"));
            Assert.True(cut.Find($"[data-testid=release-item-{feature.Number}]").HasAttribute("checked"));
            Assert.True(cut.Find($"[data-testid=release-item-{fix.Number}]").HasAttribute("checked"));
            Assert.Equal(2, cut.FindAll("[data-testid=release-items] li").Count);
            Assert.True(cut.Find("[data-testid=release-tag-existing]").HasAttribute("checked"));
            var notes = cut.Find("[data-testid=release-notes]").GetAttribute("value")!;
            Assert.Contains("## Features", notes);
            Assert.Contains($"- A release option on the board (#{feature.Number})", notes);
            Assert.Contains($"- The toggle sticks (#{fix.Number})", notes);
        });
    }

    [Fact]
    public async Task Saving_from_an_existing_tag_stamps_the_ticked_items_and_leaves_the_unticked_one()
    {
        var shipped = await DoneAsync("A release option on the board", WorkItemLevel.Feature);
        var waits = await DoneAsync("Waits for the next one");
        _git.Tags.Add(new GitTag("v0.9.97", Head, DateTimeOffset.UnixEpoch, string.Empty));
        ReleaseSaved? said = null;

        using var ctx = Context();
        var cut = Render(ctx, s => said = s);
        cut.WaitForAssertion(() => cut.Find($"[data-testid=release-item-{waits.Number}]"));

        await cut.InvokeAsync(() => cut.Find($"[data-testid=release-item-{waits.Number}]")
            .Change(new ChangeEventArgs { Value = false }));
        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("Waits for the next one", cut.Find("[data-testid=release-notes]").GetAttribute("value")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=release-save]").Click());

        cut.WaitForAssertion(() => Assert.NotNull(said));
        Assert.Equal("v0.9.97", (await _board.GetAsync(shipped.Id))!.ReleasedIn);
        Assert.Null((await _board.GetAsync(waits.Id))!.ReleasedIn);
        var release = await _releases.For(ProjectId.Default).GetAsync("v0.9.97");
        Assert.NotNull(release);
        Assert.False(release!.TagCreated);
        Assert.Equal(Head, release.Commit);
        var document = await _wiki.GetByNumberAsync(release.WikiNumber!.Value);
        Assert.Equal(WikiItemType.Release, document!.Type);
        Assert.Equal("Release v0.9.97", document.Title);
        Assert.Empty(_git.Created);
        Assert.Empty(_git.Pushed);
    }

    [Fact]
    public async Task Creating_a_tag_asks_first_then_pushing_asks_again_and_only_then_can_the_release_be_saved()
    {
        await DoneAsync("A release option on the board", WorkItemLevel.Feature);
        _git.Tags.Add(new GitTag("v0.9.97", Head, DateTimeOffset.UnixEpoch, string.Empty));
        await _releases.For(ProjectId.Default).CreateAsync(new NewRelease
        {
            Version = "v0.9.97",
            Tag = "v0.9.97",
            Notes = "n",
            Items = [(await DoneAsync("Already shipped")).Id],
        });

        using var ctx = Context();
        var cut = Render(ctx);

        // v0.9.97 is recorded, so the suggestion is the next patch on a new tag.
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("v0.9.98", cut.Find("[data-testid=release-version]").GetAttribute("value"));
            Assert.True(cut.Find("[data-testid=release-tag-create]").HasAttribute("checked"));
            Assert.True(cut.Find("[data-testid=release-save]").HasAttribute("disabled"));
        });

        await cut.InvokeAsync(() => cut.Find("[data-testid=release-create-tag]").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid=release-create-confirm]"));
        Assert.Empty(_git.Created);

        await cut.InvokeAsync(() => cut.Find("[data-testid=release-create-yes]").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid=release-push-tag]"));
        Assert.Equal([("v0.9.98", Head, "Release v0.9.98")], _git.Created);
        Assert.Empty(_git.Pushed);
        Assert.True(cut.Find("[data-testid=release-save]").HasAttribute("disabled"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=release-push-tag]").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid=release-push-confirm]"));
        Assert.Empty(_git.Pushed);

        await cut.InvokeAsync(() => cut.Find("[data-testid=release-push-yes]").Click());
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=release-save]").HasAttribute("disabled")));
        Assert.Equal(["v0.9.98"], _git.Pushed);
    }

    [Fact]
    public async Task A_tag_name_that_already_exists_cannot_be_created()
    {
        await DoneAsync("A release option on the board", WorkItemLevel.Feature);
        _git.Tags.Add(new GitTag("v0.9.97", Head, DateTimeOffset.UnixEpoch, string.Empty));
        await _releases.For(ProjectId.Default).CreateAsync(new NewRelease
        {
            Version = "v0.9.96",
            Tag = "v0.9.96",
            Notes = "n",
            Items = [(await DoneAsync("Older")).Id],
        });

        using var ctx = Context();
        var cut = Render(ctx);
        cut.WaitForAssertion(() => cut.Find("[data-testid=release-tag-create]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=release-tag-create]").Change(new ChangeEventArgs { Value = "on" }));
        await cut.InvokeAsync(() => cut.Find("[data-testid=release-version]").Input("v0.9.97"));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("already exists", cut.Find("[data-testid=release-tag-exists]").TextContent);
            Assert.True(cut.Find("[data-testid=release-create-tag]").HasAttribute("disabled"));
        });
        Assert.Empty(_git.Created);
    }

    [Fact]
    public async Task The_releases_page_shows_a_release_with_its_notes_and_every_item_it_carried()
    {
        var shipped = await DoneAsync("A release option on the board", WorkItemLevel.Feature);
        await _releases.For(ProjectId.Default).CreateAsync(new NewRelease
        {
            Version = "v0.9.97",
            Tag = "v0.9.97",
            Commit = Head,
            Notes = "# Release v0.9.97" + Environment.NewLine + Environment.NewLine + "What shipped.",
            Items = [shipped.Id],
        });

        using var ctx = Context();
        ctx.Services.AddSingleton<IWorkItemChanges>(_changes);
        ctx.Services.AddSingleton<Fravashi.Application.Projects.IProjectStore>(new OneProjectStore());
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.Wiki.WikiReferenceResolver(_resolver, new OneWiki(_wiki)));
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.Rendering.WikiMarkdownRenderer());
        ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("http://localhost/releases?version=v0.9.97");

        var cut = ctx.Render<Fravashi.Web.Components.Pages.ReleasesPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-testid=releases-item]"));
            Assert.Contains("What shipped.", cut.Find("[data-testid=release-detail-notes]").TextContent);
            var item = cut.Find("[data-testid=release-detail-item]");
            Assert.Contains("A release option on the board", item.TextContent);
            Assert.Equal($"/workitems/{shipped.Number}", item.GetAttribute("href"));
        });
    }
}
