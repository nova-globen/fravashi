using Bunit;
using Fravashi.Application.Search;
using Fravashi.Web.Components.Code;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

public class CodeSearchPanelTests
{
    private sealed class FakeSearchService : IRepositorySearchService
    {
        public SearchQuery? LastQuery { get; private set; }

        public List<(SearchQuery Query, string Replacement, IReadOnlyList<string> Paths, (int, int)? OnlyAt)> Replaces { get; } = [];

        public SearchResultSet NextResult { get; set; } = new(
            [new SearchFileResult("src/Program.cs", [new SearchMatch(3, 5, 5, "    alpha here")])],
            1, false, "test");

        public Task<SearchResultSet> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(NextResult);
        }

        public List<IReadOnlyList<ReplacedFile>> Restores { get; } = [];

        public Task<ReplaceResult> ReplaceAsync(
            SearchQuery query, string replacement, IReadOnlyList<string> relativePaths,
            (int Line, int Column)? onlyAt = null, CancellationToken cancellationToken = default)
        {
            Replaces.Add((query, replacement, relativePaths, onlyAt));
            return Task.FromResult(new ReplaceResult(relativePaths.Count, relativePaths.Count,
                Files: relativePaths.Select(p => new ReplacedFile(p, "content before the replace", null)).ToList()));
        }

        public Task<ReplaceResult> RestoreAsync(IReadOnlyList<ReplacedFile> files, CancellationToken cancellationToken = default)
        {
            Restores.Add(files);
            return Task.FromResult(new ReplaceResult(files.Count, files.Count));
        }
    }

    private const string IconManifest = """
        { "iconDefinitions": { "file": { "iconPath": "./../icons/file.svg" } },
          "fileExtensions": {}, "fileNames": {}, "folderNames": {}, "folderNamesExpanded": {},
          "file": "file", "folder": "folder", "folderExpanded": "folder-open" }
        """;

    private static (BunitContext Ctx, FakeSearchService Search) PanelContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var search = new FakeSearchService();
        ctx.Services.AddSingleton<IRepositorySearchService>(search);
        ctx.Services.AddSingleton(FileIconCatalog.FromJson(IconManifest));
        return (ctx, search);
    }

    [Fact]
    public void Typing_a_query_searches_after_the_debounce_and_renders_grouped_results()
    {
        var (ctx, search) = PanelContext();
        using var _ = ctx;
        var cut = ctx.Render<CodeSearchPanel>();

        // This test deliberately exercises the 300ms debounce path (type-to-search, no Enter): it is
        // the one place that proves the debounce fires, so it keeps the timer and a generous wait. The
        // other tests here press Enter to run immediately, because they assert on RESULTS, not on how
        // the search was triggered, and the timer is what made one of them flake under load (item 106).
        cut.Find("[data-testid=code-search-query]").Input("alpha");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-search-results]")), TimeSpan.FromSeconds(5));
        Assert.Equal("alpha", search.LastQuery!.Pattern);
        Assert.Contains("1 result in 1 file", cut.Find("[data-testid=code-search-summary]").TextContent);
        Assert.Contains(cut.FindAll(".code-search-file-row"), row => row.TextContent.Contains("Program.cs"));
        Assert.Equal("alpha", cut.Find(".code-search-match mark").TextContent);
    }

    [Fact]
    public void The_three_toggles_flow_into_the_query()
    {
        var (ctx, search) = PanelContext();
        using var _ = ctx;
        var cut = ctx.Render<CodeSearchPanel>();
        cut.Find("[data-testid=code-search-query]").Input("x");
        cut.Find("[data-testid=code-search-query]").KeyDown("Enter"); // run now, not on the 300ms debounce (flake, item 106)

        cut.Find("[data-testid=code-search-case]").Click();
        cut.Find("[data-testid=code-search-word]").Click();
        cut.Find("[data-testid=code-search-regex]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(search.LastQuery!.CaseSensitive);
            Assert.True(search.LastQuery!.WholeWord);
            Assert.True(search.LastQuery!.IsRegex);
        }, TimeSpan.FromSeconds(3));
        Assert.Equal("true", cut.Find("[data-testid=code-search-regex]").GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Include_and_exclude_fields_reach_the_query()
    {
        var (ctx, search) = PanelContext();
        using var _ = ctx;
        var cut = ctx.Render<CodeSearchPanel>();
        cut.Find("[data-testid=code-search-filters-toggle]").Click();
        cut.Find("[data-testid=code-search-include]").Input("src/**");
        cut.Find("[data-testid=code-search-exclude]").Input("*.md");
        cut.Find("[data-testid=code-search-query]").Input("alpha");
        cut.Find("[data-testid=code-search-query]").KeyDown("Enter"); // run now, not on the 300ms debounce (flake, item 106)

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("src/**", search.LastQuery!.IncludeGlobs);
            Assert.Equal("*.md", search.LastQuery!.ExcludeGlobs);
        }, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Clicking_a_match_raises_the_open_request()
    {
        var (ctx, _) = PanelContext();
        using var __ = ctx;
        CodeSearchPanel.OpenMatchRequest? opened = null;
        var cut = ctx.Render<CodeSearchPanel>(p => p
            .Add(c => c.OnOpenMatch, r => opened = r));
        cut.Find("[data-testid=code-search-query]").Input("alpha");
        cut.Find("[data-testid=code-search-query]").KeyDown("Enter"); // run now, not on the 300ms debounce (flake, item 106)
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".code-search-match")), TimeSpan.FromSeconds(3));

        cut.InvokeAsync(() => cut.Find(".code-search-match").Click());

        cut.WaitForAssertion(() => Assert.NotNull(opened), TimeSpan.FromSeconds(3));
        Assert.Equal("src/Program.cs", opened!.Path);
        Assert.Equal(3, opened.Line);
        Assert.Equal(5, opened.Column);
    }

    [Fact]
    public void Replace_all_confirms_first_then_routes_clean_files_to_the_service()
    {
        var (ctx, search) = PanelContext();
        using var _ = ctx;
        var cut = ctx.Render<CodeSearchPanel>();
        cut.Find("[data-testid=code-search-query]").Input("alpha");
        cut.Find("[data-testid=code-search-query]").KeyDown("Enter"); // run now, not on the 300ms debounce (flake, item 106)
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".code-search-match")), TimeSpan.FromSeconds(3));
        cut.InvokeAsync(() => cut.Find("[data-testid=code-search-replace]").Input("omega"));

        cut.InvokeAsync(() => cut.Find("[data-testid=code-search-replace-all]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-replace-confirm]")));
        Assert.Empty(search.Replaces); // nothing happens before the confirm

        cut.InvokeAsync(() => cut.Find("[data-testid=code-replace-confirm-go]").Click());

        cut.WaitForAssertion(() => Assert.Single(search.Replaces), TimeSpan.FromSeconds(3));
        Assert.Equal("omega", search.Replaces[0].Replacement);
        Assert.Equal(["src/Program.cs"], search.Replaces[0].Paths);
    }

    [Fact]
    public void Replace_all_routes_dirty_buffers_through_the_client_callback()
    {
        var (ctx, search) = PanelContext();
        using var _ = ctx;
        var bufferReplaces = new List<CodeSearchPanel.BufferReplaceRequest>();
        var cut = ctx.Render<CodeSearchPanel>(p => p
            .Add(c => c.IsBufferDirty, (Func<string, bool>)(path => path == "src/Program.cs"))
            .Add(c => c.ReplaceInBuffer, (Func<CodeSearchPanel.BufferReplaceRequest, Task<int>>)(r =>
            {
                bufferReplaces.Add(r);
                return Task.FromResult(1);
            })));
        cut.Find("[data-testid=code-search-query]").Input("alpha");
        cut.Find("[data-testid=code-search-query]").KeyDown("Enter"); // run now, not on the 300ms debounce (flake, item 106)
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".code-search-match")), TimeSpan.FromSeconds(3));
        cut.InvokeAsync(() => cut.Find("[data-testid=code-search-replace]").Input("omega"));

        cut.InvokeAsync(() => cut.Find("[data-testid=code-search-replace-all]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-replace-confirm]")));
        cut.InvokeAsync(() => cut.Find("[data-testid=code-replace-confirm-go]").Click());

        cut.WaitForAssertion(() => Assert.Single(bufferReplaces), TimeSpan.FromSeconds(3));
        Assert.Empty(search.Replaces); // the dirty file never goes to the disk path
        Assert.Equal("src/Program.cs", bufferReplaces[0].Path);
        Assert.Equal("omega", bufferReplaces[0].Replacement);
    }

    [Fact]
    public void Results_group_under_their_folder_by_default_the_tree_view()
    {
        var (ctx, _) = PanelContext();
        using var __ = ctx;
        var cut = ctx.Render<CodeSearchPanel>();

        cut.Find("[data-testid=code-search-query]").Input("alpha");
        cut.Find("[data-testid=code-search-query]").KeyDown("Enter"); // run now, not on the 300ms debounce (flake, item 106)

        // Wait for the FILE ROW, not only the folder: a folder node can render a pass before its rows, and on a
        // starved runner the immediate Find of the row came up empty (CI run 329).
        cut.WaitForAssertion(
            () => Assert.Contains("src", cut.Find(".code-search-dir .code-search-file-row .code-name").TextContent),
            TimeSpan.FromSeconds(3));
        // The tree already names the folder; the file row does not repeat the directory.
        Assert.Empty(cut.FindAll(".code-search-file-dir"));
    }

    [Fact]
    public void A_disk_replace_offers_a_one_level_undo_that_restores_the_pre_images()
    {
        var (ctx, search) = PanelContext();
        using var _ = ctx;
        var cut = ctx.Render<CodeSearchPanel>();
        cut.Find("[data-testid=code-search-query]").Input("alpha");
        cut.Find("[data-testid=code-search-query]").KeyDown("Enter"); // run now, not on the 300ms debounce (flake, item 106)
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".code-search-match")), TimeSpan.FromSeconds(3));
        cut.InvokeAsync(() => cut.Find("[data-testid=code-search-replace]").Input("omega"));
        cut.InvokeAsync(() => cut.Find("[data-testid=code-search-replace-all]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-replace-confirm]")));
        cut.InvokeAsync(() => cut.Find("[data-testid=code-replace-confirm-go]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-search-undo]")), TimeSpan.FromSeconds(3));

        cut.InvokeAsync(() => cut.Find("[data-testid=code-search-undo]").Click());

        cut.WaitForAssertion(() => Assert.Single(search.Restores), TimeSpan.FromSeconds(3));
        Assert.Equal("src/Program.cs", search.Restores[0][0].Path);
        Assert.Equal("content before the replace", search.Restores[0][0].PreviousContent);
        cut.WaitForAssertion(() =>
            Assert.Contains("Restored 1 file", cut.Find("[data-testid=code-search-undo-note]").TextContent));
    }

    [Fact]
    public void A_search_error_is_shown_honestly()
    {
        var (ctx, search) = PanelContext();
        using var _ = ctx;
        search.NextResult = SearchResultSet.Failed("test", "Invalid regular expression: boom");
        var cut = ctx.Render<CodeSearchPanel>();

        cut.Find("[data-testid=code-search-query]").Input("([bad");
        cut.Find("[data-testid=code-search-query]").KeyDown("Enter"); // run now, not on the 300ms debounce (flake, item 106)

        cut.WaitForAssertion(() =>
            Assert.Contains("Invalid regular expression", cut.Find("[data-testid=code-search-error]").TextContent),
            TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void A_truncated_result_says_so()
    {
        var (ctx, search) = PanelContext();
        using var _ = ctx;
        search.NextResult = new SearchResultSet(
            [new SearchFileResult("a.txt", [new SearchMatch(1, 1, 1, "x")])], 1, Truncated: true, "test");
        var cut = ctx.Render<CodeSearchPanel>();

        cut.Find("[data-testid=code-search-query]").Input("x");
        cut.Find("[data-testid=code-search-query]").KeyDown("Enter"); // run now, not on the 300ms debounce (flake, item 106)

        cut.WaitForAssertion(() =>
            Assert.Contains("stopped at the cap", cut.Find("[data-testid=code-search-summary]").TextContent),
            TimeSpan.FromSeconds(3));
    }
}
