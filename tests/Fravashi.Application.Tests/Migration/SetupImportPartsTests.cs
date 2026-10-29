using AwesomeAssertions;
using Fravashi.Application.Migration;
using Fravashi.Domain.Wiki;
using Xunit;

namespace Fravashi.Application.Tests.Migration;

/// <summary>
/// Item #373, the setup's import: the checklist parser, the selection (the scan's defaults with a person's changes), and
/// the scan's suggestions, its cap, its sizes and its ignore rules over an in-memory folder.
/// </summary>
public sealed class SetupImportPartsTests
{
    [Fact]
    public void A_checklist_gives_one_entry_per_box_in_order_with_ticked_boxes_done()
    {
        var entries = ChecklistMarkdown.Parse("# Todo\n\n- [ ] Write the page\n  * [x] Draft it\n1. [ ] Ship it\n\n```\n- [ ] not a box\n```\n- [ ]   \n");

        entries.Select(e => (e.Text, e.Done, e.Line)).Should().Equal(
            ("Write the page", false, 3), ("Draft it", true, 4), ("Ship it", false, 5));
    }

    [Theory]
    [InlineData("TODO.md", "Some prose.\n\n- [ ] one\n\nMore prose.\nAnd more.\n", true)]
    [InlineData("docs/notes.md", "- [ ] one\n- [x] two\n- [ ] three\nA line.\n", true)]
    [InlineData("docs/notes.md", "- [ ] one\nA line.\nAnother.\nAnd another.\n", false)]
    [InlineData("TASKS.md", "No boxes at all.\n", false)]
    public void A_file_is_a_checklist_by_its_name_or_when_boxes_are_most_of_it(string path, string text, bool expected) =>
        ChecklistMarkdown.IsChecklist(path, text).Should().Be(expected);

    [Fact]
    public void The_selection_is_the_scans_defaults_with_a_persons_changes_and_never_a_file_too_large()
    {
        ScanCandidate[] candidates =
        [
            new("README.md", 10, ImportTarget.Document(WikiItemType.Reference), true),
            new("CHANGELOG.md", 10, ImportTarget.Document(WikiItemType.Reference), false),
            new("docs/backlog.md", 10, ImportTarget.WorkItems, true, WorkItemCount: 3),
            new("docs/huge.md", 2_000_000, ImportTarget.Document(WikiItemType.Reference), false, TooLarge: true),
        ];
        var overrides = new Dictionary<string, ImportOverride>
        {
            ["README.md"] = new(false, "reference"),
            ["CHANGELOG.md"] = new(true, "plan"),
            ["docs/huge.md"] = new(true, "reference"),
            ["docs/backlog.md"] = new(true, "no-such-target"),
        };

        var selection = ImportSelection.From(candidates, overrides);

        selection.Files.Select(f => (f.Path, f.Target.Name)).Should().BeEquivalentTo(
            [("CHANGELOG.md", "plan"), ("docs/backlog.md", ImportTarget.WorkItemsName)]);
        selection.DocumentTypes.Should().ContainKey("CHANGELOG.md").WhoseValue.Should().Be(WikiItemType.Plan);
        ImportSelection.From(candidates).Files.Select(f => f.Path).Should().BeEquivalentTo(["README.md", "docs/backlog.md"]);
    }

    [Fact]
    public void A_target_round_trips_through_its_stored_name()
    {
        ImportTarget.Parse(ImportTarget.WorkItems.Name).Should().Be(ImportTarget.WorkItems);
        foreach (var type in ImportTarget.DocumentTypes)
        {
            ImportTarget.Parse(ImportTarget.Document(type).Name).Should().Be(ImportTarget.Document(type));
        }

        ImportTarget.Parse("design").Should().BeNull("a design has a body of its own shape, so a markdown file is never one");
        ImportTarget.Document(WikiItemType.Adr).Label.Should().Be("ADR");
    }

    [Theory]
    [InlineData("docs/adr/0001-x.md", WikiItemType.Adr, true)]
    [InlineData("docs/plans/p.markdown", WikiItemType.Plan, true)]
    [InlineData("docs/backlog.md", WikiItemType.Reference, true)]
    [InlineData("README.md", WikiItemType.Reference, true)]
    [InlineData("readme.md", WikiItemType.Reference, true)]
    [InlineData("CONTRIBUTING.md", WikiItemType.Governance, true)]
    [InlineData("CODE_OF_CONDUCT.md", WikiItemType.Governance, true)]
    [InlineData("CHANGELOG.md", WikiItemType.Reference, false)]
    [InlineData("packages/api/README.md", WikiItemType.Reference, false)]
    [InlineData("NOTES.md", WikiItemType.Reference, false)]
    public void A_file_is_suggested_by_its_path(string path, WikiItemType type, bool include)
    {
        var (target, included) = RepositoryScan.ByPath(path);

        target.DocumentType.Should().Be(type);
        included.Should().Be(include);
    }

    [Fact]
    public async Task The_scan_lists_tracked_markdown_with_sizes_reads_only_the_lists_and_counts_their_items()
    {
        var folder = new MemoryFolder(tracked: true)
            .With("README.md", "# Shop\n")
            .With("docs/backlog.md", "### 1. One\n**Status** Ready\n\n### 3. Three\n**Status** Done\n")
            .With("TODO.md", "- [ ] a\n- [x] b\n")
            .With("docs/guide.md", "# Guide\n")
            .With("docs/big.md", new string('x', 1_100_000))
            .With("src/app.cs", "class A {}");

        var result = await new RepositoryScan(folder).RunAsync();

        result.FromGit.Should().BeTrue();
        result.Candidates.Select(c => c.Path).Should().Equal("README.md", "TODO.md", "docs/backlog.md", "docs/big.md", "docs/guide.md");
        result.Candidates.Single(c => c.Path == "README.md").Size.Should().Be(7);
        var backlog = result.Candidates.Single(c => c.Path == "docs/backlog.md");
        backlog.Suggested.Should().Be(ImportTarget.WorkItems);
        backlog.Note.Should().Be("2 items, numbers kept");
        result.Candidates.Single(c => c.Path == "TODO.md").Note.Should().Be("2 items from its checklist, 1 done");
        var big = result.Candidates.Single(c => c.Path == "docs/big.md");
        (big.TooLarge, big.Include, big.Note).Should().Be((true, false, "Too large to import (over 1 MB)"));
        result.DocumentsChecked.Should().Be(2);
        result.WorkItemsChecked.Should().Be(4);

        // Nothing but the two lists was read: no title, no document text, before a person looks.
        folder.Reads.Should().BeEquivalentTo(["head:docs/backlog.md", "all:docs/backlog.md", "head:TODO.md", "all:TODO.md"]);
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repository_is_walked_past_what_is_ignored()
    {
        var folder = new MemoryFolder(tracked: false)
            .With("README.md", "# x\n")
            .With("node_modules/pkg/README.md", "# dep\n")
            .With("build/out.md", "# built\n")
            .With("docs/a.md", "# a\n");

        var result = await new RepositoryScan(folder, (path, isDirectory) => isDirectory && path == "build").RunAsync();

        result.FromGit.Should().BeFalse();
        result.Candidates.Select(c => c.Path).Should().Equal("README.md", "docs/a.md");
    }

    [Fact]
    public async Task The_scan_stops_listing_at_its_cap_and_says_so()
    {
        var folder = new MemoryFolder(tracked: true);
        for (var i = 0; i < 12; i++)
        {
            folder.With($"docs/n{i:00}.md", "# n\n");
        }

        var progress = new List<ScanProgress>();
        var result = await new RepositoryScan(folder).RunAsync(new SynchronousProgress(progress), cap: 5);

        result.Capped.Should().BeTrue();
        result.Candidates.Should().HaveCount(5);
        progress.Should().NotBeEmpty();
    }

    private sealed class SynchronousProgress(List<ScanProgress> seen) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => seen.Add(value);
    }

    /// <summary>A folder in memory: files with text, optionally "tracked by git", recording every read.</summary>
    private sealed class MemoryFolder(bool tracked) : IRepositoryFiles
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public List<string> Reads { get; } = [];

        public MemoryFolder With(string path, string text)
        {
            _files[path] = text;
            return this;
        }

        public Task<IReadOnlyList<string>?> TrackedMarkdownAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>(tracked
                ? [.. _files.Keys.Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))]
                : null);

        public Task<IReadOnlyList<RepositoryEntry>> ListAsync(string folder, CancellationToken ct)
        {
            var prefix = folder.Length == 0 ? string.Empty : folder + "/";
            var entries = new Dictionary<string, RepositoryEntry>(StringComparer.Ordinal);
            foreach (var (path, text) in _files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var rest = path[prefix.Length..];
                var slash = rest.IndexOf('/');
                entries[slash < 0 ? path : prefix + rest[..slash]] = slash < 0
                    ? new RepositoryEntry(path, false, System.Text.Encoding.UTF8.GetByteCount(text))
                    : new RepositoryEntry(prefix + rest[..slash], true, 0);
            }

            return Task.FromResult<IReadOnlyList<RepositoryEntry>>([.. entries.Values]);
        }

        public Task<string?> ReadHeadAsync(string path, int maxBytes, CancellationToken ct)
        {
            Reads.Add("head:" + path);
            return Task.FromResult(_files.TryGetValue(path, out var text) ? text[..Math.Min(text.Length, maxBytes)] : null);
        }

        public Task<string?> ReadAllAsync(string path, CancellationToken ct)
        {
            Reads.Add("all:" + path);
            return Task.FromResult(_files.GetValueOrDefault(path));
        }
    }
}
