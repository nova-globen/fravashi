using Bunit;
using Fravashi.Domain.Git;
using Fravashi.Web.Components.Git;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #369: git status reports an untracked FOLDER as one entry whose path ends in a slash
/// (<c>?? docs/evidences/2026-10-25-v0.9.98-rc2/</c>). The tree view filed it inside a folder node of its own name
/// with a child row showing only "U", because the file name of a path ending in a slash is empty. It is now one row
/// naming the folder, with its trailing slash, staged as a whole by its own path, in both views.
/// </summary>
public class GitWorkingTreeFolderEntryTests
{
    private const string Folder = "docs/evidences/2026-10-25-v0.9.98-rc2/";

    private static GitWorkingStatus Status() => new(
        "main", null, 0, 0, [],
        [
            new GitWorkingEntry("docs/evidences/notes.md", GitEntryState.Modified, Staged: false),
            new GitWorkingEntry(Folder, GitEntryState.Untracked, Staged: false),
        ]);

    private static (BunitContext Ctx, IRenderedComponent<GitWorkingTree> Cut, List<IReadOnlyList<string>> Staged) Render()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var staged = new List<IReadOnlyList<string>>();
        var cut = ctx.Render<GitWorkingTree>(p => p
            .Add(c => c.Status, Status())
            .Add(c => c.OnStage, paths => staged.Add(paths)));
        return (ctx, cut, staged);
    }

    private static AngleSharp.Dom.IElement FolderRow(IRenderedComponent<GitWorkingTree> cut) =>
        Assert.Single(cut.FindAll("[data-testid=git-change-row]"), r => r.GetAttribute("title") == Folder);

    [Fact]
    public void In_the_tree_an_untracked_folder_is_one_row_naming_it_with_a_trailing_slash()
    {
        var (ctx, cut, _) = Render();
        using var _1 = ctx;

        var rows = cut.FindAll("[data-testid=git-change-row]");
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.QuerySelector(".code-name")?.TextContent)));

        var row = FolderRow(cut);
        Assert.Equal("2026-10-25-v0.9.98-rc2/", row.QuerySelector(".code-name")!.TextContent);
        Assert.Equal("true", row.GetAttribute("data-folder"));

        // It sits beside its sibling file under docs/evidences, not inside a folder node of its own name.
        var dirLabels = cut.FindAll(".git-change-dir-row .code-name").Select(e => e.TextContent).ToList();
        Assert.Equal(["docs/evidences"], dirLabels);
    }

    [Fact]
    public async Task The_folder_row_stages_the_folder_as_a_whole_by_its_own_path()
    {
        var (ctx, cut, staged) = Render();
        using var _1 = ctx;

        await cut.InvokeAsync(() => FolderRow(cut).QuerySelector("[data-testid=git-row-stage]")!.Click());

        Assert.Equal([Folder], Assert.Single(staged));
    }

    [Fact]
    public async Task In_the_list_the_folder_row_shows_its_whole_path_and_stages_it()
    {
        var (ctx, cut, staged) = Render();
        using var _1 = ctx;

        await cut.InvokeAsync(() => cut.Find("[data-testid=git-changes-view-toggle]").Click());

        var row = FolderRow(cut);
        Assert.Equal(Folder, row.QuerySelector(".code-name")!.TextContent);
        await cut.InvokeAsync(() => row.QuerySelector("[data-testid=git-row-stage]")!.Click());
        Assert.Equal([Folder], Assert.Single(staged));
    }

    [Theory]
    [InlineData("docs/evidences/run/", true, "run/", "docs/evidences")]
    [InlineData("run/", true, "run/", "")]
    [InlineData("docs/evidences/notes.md", false, "notes.md", "docs/evidences")]
    [InlineData("README.md", false, "README.md", "")]
    public void A_working_entry_is_named_and_placed_by_its_own_kind(string path, bool folder, string name, string directory)
    {
        Assert.Equal(folder, GitPaths.IsFolderEntry(path));
        Assert.Equal(name, GitPaths.EntryNameOf(path));
        Assert.Equal(directory, GitPaths.EntryDirectoryOf(path));
    }
}
