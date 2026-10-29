using AwesomeAssertions;
using Fravashi.Application.Migration;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Migration;

/// <summary>
/// Backlog 49: the dogfood migration's import, over the real board and wiki, fed a repository's documents and its backlog.
/// The documents are a tree built here and the backlog is <c>Fixtures/synthetic-backlog.md</c>, both synthetic and in the
/// real shapes (the import of this repository's own backlog at the cut-over, 2026-09-30, is in its history). The small
/// cases hold the rules; the large one holds the claim that matters, which is that every number the backlog uses means
/// the same item in the tracker afterwards.
/// </summary>
public sealed class RepositoryImportTests : IDisposable
{
    private readonly SqliteTestContext _store = new();
    private readonly LocalTrackerResolver _trackers;
    private readonly SqliteWikiLibraries _wikis;
    private readonly ProjectId _project;
    private readonly RepositoryImport _import;

    public RepositoryImportTests()
    {
        _trackers = new LocalTrackerResolver(_store.Factory, TimeProvider.System);
        _wikis = new SqliteWikiLibraries(_store.Factory, TimeProvider.System);
        _project = new SqliteProjectStore(_store.Factory, TimeProvider.System).Create("Fravashi").Id;
        _import = new RepositoryImport(_trackers, _wikis);
    }

    public void Dispose() => _store.Dispose();

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull();
        return dir!.FullName;
    }

    /// <summary>
    /// A repository's markdown under docs/, keyed by repository-relative path the way the studio reads a workspace, with
    /// the backlog where the importer looks for it: folders of ADRs, plans, design notes and evidence, a folder README,
    /// and links between documents.
    /// </summary>
    private static Dictionary<string, string> RepositoryDocs() => new(StringComparer.Ordinal)
    {
        ["docs/vision.md"] = "# Vision\nWhere the product goes. See [the roadmap](roadmap.md).",
        ["docs/roadmap.md"] = "# Roadmap\nPhases A to E, after [the vision](vision.md).",
        ["docs/adr/0018-the-tracker-and-the-wiki-are-the-source-of-truth.md"] = "# ADR 0018: The tracker and the wiki are the source of truth\nNothing is tracked in files.",
        ["docs/adr/0027-the-verifier-runs-a-users-app-in-sibling-containers.md"] =
            "# ADR 0027: The verifier runs a user's app in sibling containers\nSee [the plan](../plans/app-runner.md).",
        ["docs/adr/0029-one-container-engine.md"] = "# ADR 0029: One container engine\nwslc on the dev host.",
        ["docs/plans/app-runner.md"] = "# The app runner plan\nBuilt on [ADR 0027](../adr/0027-the-verifier-runs-a-users-app-in-sibling-containers.md).",
        ["docs/plans/rename.md"] = "# The rename plan\nNames, then migrations.",
        ["docs/design/README.md"] = "# Design references\nSee [the threat model](threat-model.md).",
        ["docs/design/threat-model.md"] = "# Threat model\nBack to [the index](README.md).",
        ["docs/design/tokens.md"] = "# Design tokens\nSurfaces and type.",
        ["docs/evidences/2026-10-01-a-release/README.md"] = "# Evidence of a release",
        ["docs/evidences/2026-10-02-a-drill/notes.md"] = "# Notes from a drill",
        [RepositoryImport.BacklogPath] = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "tests", "Fravashi.Infrastructure.Tests", "Migration", "Fixtures", "synthetic-backlog.md")),
    };

    [Fact]
    public async Task A_repositorys_backlog_imports_with_every_number_meaning_the_same_item()
    {
        var files = RepositoryDocs();
        var entries = BacklogMarkdown.Parse(files[RepositoryImport.BacklogPath]);

        var report = await _import.ImportAsync(_project, files);

        report.Refusal.Should().BeNull(string.Join("\n", report.Problems));
        report.Problems.Should().BeEmpty();
        var tracker = _trackers.For(_project);
        foreach (var entry in entries)
        {
            var item = await tracker.GetByNumberAsync(entry.Number);
            item.Should().NotBeNull($"backlog item {entry.Number} must be tracker item #{entry.Number}");
            item!.Title.Should().Be(entry.Title);
            item.Status.Should().Be(RepositoryImport.StatusOf(entry.Status).Status, $"item {entry.Number}");
        }

        // Backlog 175: the fields the tracker carries as data are not repeated in the text, where they would go stale;
        // what has no field of its own is kept.
        foreach (var entry in entries)
        {
            var description = (await tracker.GetByNumberAsync(entry.Number))!.Description;
            description.Should().NotContain("**Status** ", $"item {entry.Number}'s status is a field");
            description.Should().NotContain("**Blocked by** ", $"item {entry.Number}'s blockers are links");
            description.Should().NotContain("**Blocks** ", $"item {entry.Number}'s blocking is a link");
        }

        var runner = (await tracker.GetByNumberAsync(87))!.Description;
        runner.Should().StartWith("**Type** Feature | **Size** XL | **Phase** E1").And.Contain("**ADRs** 0027");

        // Every number up to the last is taken, the removed ones by a placeholder that says so.
        var last = entries.Max(e => e.Number);
        for (var n = 1; n <= last; n++)
        {
            (await tracker.GetByNumberAsync(n)).Should().NotBeNull($"number {n}");
        }

        // The dependency graph came across: every "Blocked by" number is a link on the item.
        foreach (var entry in entries.Where(e => e.BlockedBy.Count > 0))
        {
            var item = await tracker.GetByNumberAsync(entry.Number);
            var links = await tracker.LinksAsync(item!.Id);
            links.Count(l => l.Type == WorkItemLinkType.BlockedBy || l.Type == WorkItemLinkType.Blocks)
                .Should().BeGreaterThanOrEqualTo(entry.BlockedBy.Count, $"item {entry.Number}");
        }

        // The documents came across, and the ADR the app runner is built on is one of them, by its title.
        var docs = await _wikis.For(_project).ListAsync(new WikiItemQuery { Limit = 500 });
        docs.Should().HaveCount(report.Documents);
        docs.Should().Contain(d => d.Type == WikiItemType.Adr && d.Title.Contains("0027", StringComparison.Ordinal)
                                   || d.Type == WikiItemType.Adr && d.Title.Contains("sibling containers", StringComparison.OrdinalIgnoreCase));

        // Every markdown file but the backlog is there, and the folders are a tree: every file that is not a
        // folder's README is ONE document, and each sits under the document named after its folder.
        var byId = docs.ToDictionary(d => d.Id);
        var readmes = files.Keys.Count(p => p.EndsWith("/README.md", StringComparison.Ordinal));
        var folders = files.Keys
            .Where(p => p != RepositoryImport.BacklogPath)
            .SelectMany(p =>
            {
                var parts = p.Split('/')[1..^1];
                return Enumerable.Range(1, parts.Length).Select(n => string.Join('/', parts[..n]));
            })
            .Distinct(StringComparer.Ordinal)
            .Count();
        docs.Should().HaveCount(files.Count - 1 - readmes + folders);
        var adr = docs.Single(d => d.Title.Contains("sibling containers", StringComparison.OrdinalIgnoreCase));
        byId[adr.ParentId!].Title.Should().Be("adr");
        byId[adr.ParentId!].ParentId.Should().BeNull();
        docs.Where(d => d.Title == "adr" || d.Title == "plans" || d.Title == "design").Should().HaveCount(3);
    }

    [Fact]
    public async Task A_project_that_already_holds_anything_is_refused_and_nothing_is_written()
    {
        await _wikis.For(_project).CreateAsync(new NewWikiItem { Type = WikiItemType.Draft, Title = "Already here" });

        var report = await _import.ImportAsync(_project, RepositoryDocs());

        report.Refusal.Should().Contain("empty project");
        (await _trackers.For(_project).GetByNumberAsync(1)).Should().BeNull();
    }

    [Fact]
    public async Task The_preview_says_what_would_happen_and_writes_nothing()
    {
        var report = await _import.PreviewAsync(_project, RepositoryDocs());

        report.Refusal.Should().BeNull();
        report.WorkItems.Should().BeGreaterThan(100);
        report.Documents.Should().BeGreaterThan(10);
        (await _trackers.For(_project).GetByNumberAsync(1)).Should().BeNull();
        (await _wikis.For(_project).ListAsync(new WikiItemQuery())).Should().BeEmpty();
    }

    [Fact]
    public void A_backlog_item_reads_its_fields_its_acceptance_and_its_blockers()
    {
        var entries = BacklogMarkdown.Parse("""
            # Phase E

            ### 87. Running somebody else's application, for real
            **Type** Feature | **Status** In Review | **Size** XL | **Phase** E1

            **Description.** The implementation.

            **Acceptance.** The verifier starts two projects,
            repeatedly.

            **Blocked by** 78, owner (answer the ADR, in writing), 12 | **Blocks** 56 | **ADRs** 0027 | **Design** -

            ---

            ### 88. Next
            **Type** Bug | **Status** Done | **Size** S
            """);

        entries.Should().HaveCount(2);
        var e = entries[0];
        e.Number.Should().Be(87);
        e.Type.Should().Be("Feature");
        e.Status.Should().Be("In Review");
        e.Acceptance.Should().Be("The verifier starts two projects, repeatedly.");
        e.BlockedBy.Should().Equal(78, 12);
        e.PersonBlockers.Should().ContainSingle().Which.Should().Be(("owner", "answer the ADR, in writing"));
        e.Body.Should().NotContain("### 88", "an item ends at the rule before the next one");
    }

    [Fact]
    public void A_description_drops_the_status_and_the_blocking_fields_and_keeps_every_other_character()
    {
        const string body = """
            **Type** Feature | **Status** In Review | **Size** XL | **Phase** E1

            **Description.** The implementation | with a pipe in prose.

            **Blocked by** 78, owner (answer the ADR, in writing) | **Blocks** 56 | **ADRs** 0027 | **Design** -

            **Built.** Owed: a release.
            """;

        BacklogMarkdown.WithoutTrackedFields(body.ReplaceLineEndings("\n")).Should().Be("""
            **Type** Feature | **Size** XL | **Phase** E1

            **Description.** The implementation | with a pipe in prose.

            **ADRs** 0027 | **Design** -

            **Built.** Owed: a release.
            """.ReplaceLineEndings("\n"));

        // A links line with nothing left goes, and the blank line after it with it.
        BacklogMarkdown.WithoutTrackedFields("**Type** Bug | **Status** Done\n\nText.\n\n**Blocked by** - | **Blocks** -\n\nMore.")
            .Should().Be("**Type** Bug\n\nText.\n\nMore.");
    }

    [Fact]
    public void A_link_to_an_imported_document_becomes_its_reference_and_any_other_link_is_left_alone()
    {
        var drafts = DocumentTree.Plan(new Dictionary<string, string>
        {
            ["docs/adr/0027-app-runner.md"] = "# ADR 0027\nSee [the plan](../plans/demo.md#top) and [code](../../src/x.cs).",
            ["docs/plans/demo.md"] = "# Demo plan\nBack to [the ADR](../adr/0027-app-runner.md).",
            ["README.md"] = "# not under docs",
        });

        var adr = drafts.Single(d => d.Path == "docs/adr/0027-app-runner.md");
        var plan = drafts.Single(d => d.Path == "docs/plans/demo.md");
        adr.Body.Should().Contain($"the plan (@{plan.Number})").And.Contain("[code](../../src/x.cs)");
        plan.Body.Should().Contain($"the ADR (@{adr.Number})");
        adr.Title.Should().Be("ADR 0027");
        drafts.Should().NotContain(d => d.Path == "README.md");
    }

    [Fact]
    public void The_documents_keep_their_folders_with_a_folders_readme_as_its_own_document()
    {
        // The owner's correction on 2026-09-23: the first import flattened docs/ and left most of it out.
        var drafts = DocumentTree.Plan(new Dictionary<string, string>
        {
            ["docs/vision.md"] = "# Vision",
            ["docs/backlog.md"] = "### 1. Not a document",
            ["docs/design/README.md"] = "# Design references\nSee [the model](threat-model.md).",
            ["docs/design/threat-model.md"] = "# Threat model\nBack to [the index](README.md) and [the folder](../design/).",
            ["docs/evidences/2026-09-23-v0.9.33/README.md"] = "# Evidence",
        });

        drafts.Select(d => (d.Path, d.Number, d.ParentPath)).Should().Equal(
            ("docs/vision.md", 1, (string?)null),
            ("docs/design", 2, null),
            ("docs/design/threat-model.md", 3, "docs/design"),
            ("docs/evidences", 4, null),
            ("docs/evidences/2026-09-23-v0.9.33", 5, "docs/evidences"));

        var design = drafts[1];
        design.Title.Should().Be("design");
        design.Type.Should().Be(WikiItemType.Reference);
        design.Body.Should().Contain("# Design references").And.Contain("the model (@3)");
        drafts[2].Body.Should().Contain("the index (@2)").And.Contain("the folder (@2)");
        drafts[3].Type.Should().Be(WikiItemType.Evidence);
        drafts[4].Body.Should().Contain("# Evidence");
    }
}
