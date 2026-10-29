using System.Diagnostics;
using AwesomeAssertions;
using Fravashi.Application.Migration;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Application.Sessions;
using Fravashi.Application.Setup;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Repository;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Migration;

/// <summary>
/// Item #373, the first-open setup over the real stores: the scan of a real git repository on disk (git's own listing,
/// so .gitignore and untracked files are honoured), the selection-aware import of a fixture repository into an empty
/// project (documents, items, numbers and rewritten links), and FolderSetup's one apply (its order, its refusal before
/// any project is made, and that pressing it twice does nothing twice).
/// </summary>
public sealed class FolderSetupImportTests : IDisposable
{
    private const string Folder = "ws-shop";

    private readonly SqliteTestContext _store = new();
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "fravashi-setup-repo-" + Guid.NewGuid().ToString("N"));
    private readonly LocalTrackerResolver _trackers;
    private readonly SqliteWikiLibraries _wikis;
    private readonly SqliteProjectStore _projects;
    private readonly SqliteWorkspaceStore _workspaces;
    private readonly SqliteConfigurationStore _settings;
    private readonly SqliteProjectProviders _providers;
    private readonly SqliteWorkspaceTrust _trust;
    private readonly SetupDraftStore _drafts;
    private readonly HashSet<string> _running = [];
    private readonly FolderSetup _setup;

    public FolderSetupImportTests()
    {
        Directory.CreateDirectory(_repo);
        _trackers = new LocalTrackerResolver(_store.Factory, TimeProvider.System);
        _wikis = new SqliteWikiLibraries(_store.Factory, TimeProvider.System);
        _projects = new SqliteProjectStore(_store.Factory, TimeProvider.System);
        _workspaces = new SqliteWorkspaceStore(_store.Factory);
        _workspaces.Touch(Folder, "Shop", _repo);
        _settings = new SqliteConfigurationStore(_store.Factory);
        _providers = new SqliteProjectProviders(_store.Factory);
        _trust = new SqliteWorkspaceTrust(_store.Factory);
        _drafts = new SetupDraftStore(_settings);
        _setup = new FolderSetup(
            _projects, _workspaces, new WorkspaceProjectBinding(_projects, _workspaces, id => _running.Contains(id)),
            _providers, _trust, _settings, _drafts, new RepositoryImport(_trackers, _wikis));
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_repo, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        process.ExitCode.Should().Be(0, "git {0} should succeed: {1}", string.Join(' ', args), process.StandardError.ReadToEnd());
    }

    private RepositoryScan Scan(bool withGit = true) => new(new LocalRepositoryFiles(
        _repo,
        withGit ? new LocalGitCommandRunner(Options.Create(new RepositoryReaderOptions { Root = _repo })) : null));

    /// <summary>A small repository the way people write them: a backlog OUTSIDE docs/, a checklist, linked documents.</summary>
    private void Fixture()
    {
        Write("README.md", "# Shop\n\nSee [the guide](docs/guide.md) and [the decision](docs/adr/0001-postgres.md).\n");
        Write("planning/BACKLOG.md",
            "# Backlog\n\n### 1. Take payments\n**Type** Feature | **Status** Ready\n\nPer [the plan](../docs/plans/payments.md).\n\n"
            + "### 2. Old thing\n**Status** Done\n\nDone.\n\n### 4. Ship it\n**Status** Backlog | **Blocked by** 1\n\nAfter payments.\n");
        Write("TODO.md", "- [ ] Write the usage page\n- [x] Add a changelog\n");
        Write("docs/guide.md", "# The guide\n\nThe plan is [here](plans/payments.md).\n");
        Write("docs/plans/payments.md", "# Payments plan\n\nBack to [the guide](../guide.md).\n");
        Write("docs/adr/0001-postgres.md", "# ADR 0001: Use PostgreSQL\n");
        Write("packages/api/README.md", "# The API package\n");
    }

    [Fact]
    public async Task The_scan_of_a_repository_lists_what_git_tracks_and_nothing_it_ignores()
    {
        Fixture();
        Write(".gitignore", "generated/\n");
        Write("generated/out.md", "# built\n");
        Git("init");
        Git("add", "-A");
        Write("untracked.md", "# not added yet\n");

        var result = await Scan().RunAsync();

        result.FromGit.Should().BeTrue();
        result.Candidates.Select(c => c.Path).Should().BeEquivalentTo(
            "README.md", "TODO.md", "planning/BACKLOG.md", "docs/guide.md", "docs/plans/payments.md",
            "docs/adr/0001-postgres.md", "packages/api/README.md");
        result.Candidates.Single(c => c.Path == "README.md").Size.Should().Be(new FileInfo(Path.Combine(_repo, "README.md")).Length);
        var backlog = result.Candidates.Single(c => c.Path == "planning/BACKLOG.md");
        (backlog.Suggested.IsWorkItems, backlog.Include, backlog.WorkItemCount).Should().Be((true, true, 3));
        result.Candidates.Single(c => c.Path == "packages/api/README.md").Include.Should().BeFalse();
        (await Scan().TitleAsync("docs/plans/payments.md")).Should().Be("Payments plan");
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repository_is_walked_and_the_cap_holds()
    {
        for (var i = 0; i < 7; i++)
        {
            Write($"notes/n{i}.md", "# n\n");
        }

        Write("node_modules/x/README.md", "# dep\n");

        var result = await Scan(withGit: false).RunAsync(cap: 4);

        result.FromGit.Should().BeFalse();
        result.Capped.Should().BeTrue();
        result.Candidates.Should().HaveCount(4).And.OnlyContain(c => c.Path.StartsWith("notes/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_chosen_selection_imports_into_an_empty_project_with_numbers_kept_and_links_rewritten()
    {
        Fixture();
        var scan = Scan(withGit: false);
        var result = await scan.RunAsync();
        var selection = ImportSelection.From(result.Candidates, new Dictionary<string, ImportOverride>
        {
            // A person turns the root README into an ADR's neighbour type, to prove a type can be overridden.
            ["README.md"] = new(true, "architecture"),
        });
        var project = _projects.Create("Shop").Id;

        var report = await new RepositoryImport(_trackers, _wikis).ImportAsync(project, selection, await scan.ReadAsync(selection));

        report.Refusal.Should().BeNull(string.Join("\n", report.Problems));
        report.Problems.Should().BeEmpty();
        report.WorkItems.Should().Be(5, "three backlog items and two checklist boxes");
        report.Placeholders.Should().Be(1, "number 3 was removed from the backlog");

        var tracker = _trackers.For(project);
        (await tracker.GetByNumberAsync(1))!.Title.Should().Be("Take payments");
        (await tracker.GetByNumberAsync(3))!.Status.Should().Be(WorkItemStatus.Cancelled);
        (await tracker.GetByNumberAsync(4))!.Title.Should().Be("Ship it");
        var usage = (await tracker.GetByNumberAsync(5))!;
        (usage.Title, usage.Status).Should().Be(("Write the usage page", WorkItemStatus.Backlog));
        (await tracker.GetByNumberAsync(6))!.Status.Should().Be(WorkItemStatus.Done);
        var shipLinks = await tracker.LinksAsync((await tracker.GetByNumberAsync(4))!.Id);
        shipLinks.Should().NotBeEmpty("item 4 is blocked by item 1");

        var wiki = _wikis.For(project);
        var docs = await wiki.ListAsync(new WikiItemQuery { Limit = 50 });
        docs.Select(d => d.Title).Should().Contain(["The guide", "Payments plan", "ADR 0001: Use PostgreSQL", "Shop", "plans", "adr"]);
        docs.Single(d => d.Title == "Shop").Type.Should().Be(WikiItemType.Architecture);
        var plan = docs.Single(d => d.Title == "Payments plan");
        var guide = docs.Single(d => d.Title == "The guide");
        guide.Body.Should().Contain($"(@{plan.Number})");
        plan.Body.Should().Contain($"(@{guide.Number})");
        docs.Single(d => d.Title == "Shop").Body.Should().Contain($"(@{guide.Number})");
        (await tracker.GetByNumberAsync(1))!.Description.Should().Contain($"(@{plan.Number})", "a backlog outside docs/ still has its links rewritten");
        docs.Should().NotContain(d => d.Title == "The API package", "a package README is not chosen by default");
    }

    [Fact]
    public async Task The_import_refuses_a_project_that_already_holds_work()
    {
        Fixture();
        var scan = Scan(withGit: false);
        var selection = ImportSelection.From((await scan.RunAsync()).Candidates);
        var project = _projects.Create("Busy").Id;
        (await _wikis.For(project).CreateAsync(new NewWikiItem { Type = WikiItemType.Reference, Title = "Already here" })).IsValid.Should().BeTrue();

        var report = await new RepositoryImport(_trackers, _wikis).ImportAsync(project, selection, await scan.ReadAsync(selection));

        report.Refusal.Should().Contain("already holds work items or documents");
        report.WorkItems.Should().Be(0);
    }

    private SetupImportSource Source(RepositoryScan scan, ScanResult result) =>
        new(ImportSelection.From(result.Candidates), async (selection, ct) => await scan.ReadAsync(selection, ct));

    [Fact]
    public async Task Set_up_makes_the_project_binds_the_folder_records_every_choice_and_imports()
    {
        Fixture();
        var scan = Scan(withGit: false);
        var result = await scan.RunAsync();
        var draft = new SetupDraft
        {
            NewName = "Shop", Board = ProviderChoice.None, Wiki = ProviderChoice.None,
            Model = AgentDefaultsSetting.ModelIds[0], Effort = "high", TrustEdits = true,
        };

        var applied = await _setup.ApplyAsync(Folder, draft, Source(scan, result));

        applied.Refusal.Should().BeNull();
        var outcome = applied.Outcome!;
        var project = new ProjectId(outcome.ProjectId);
        WorkspaceProjects.For(_workspaces, Folder).Should().Be(project);
        _providers.For(project).Should().Be(new ProjectProviderChoice(ProviderChoice.None, ProviderChoice.None));
        ProjectAgentDefaults.Read(_settings, project).Should().Be(new AgentDefault(AgentDefaultsSetting.ModelIds[0], AgentEffort.High));
        _trust.For(Folder).Should().Be(WorkspaceTrust.Edits);
        (outcome.Import.WorkItems, outcome.Import.Files, outcome.Import.HasProblem).Should().Be((5, 4, false));
        _drafts.Get(Folder)!.Outcome.Should().BeEquivalentTo(outcome, "Done reads the outcome after a refresh");
    }

    [Fact]
    public async Task Pressing_set_up_twice_makes_one_project_and_imports_once()
    {
        Fixture();
        var scan = Scan(withGit: false);
        var result = await scan.RunAsync();
        var draft = new SetupDraft { NewName = "Shop" };

        var first = await _setup.ApplyAsync(Folder, draft, Source(scan, result));
        var second = await _setup.ApplyAsync(Folder, _drafts.Get(Folder)!, Source(scan, result));

        second.Outcome.Should().BeEquivalentTo(first.Outcome);
        _projects.List().Count(p => p.Name == "Shop").Should().Be(1);

        // A press that died after the project was made (the outcome was never written) binds to THAT project.
        var interrupted = new SetupDraft { NewName = "Shop", CreatedProjectId = first.Outcome!.ProjectId };
        var third = await _setup.ApplyAsync(Folder, interrupted, Source(scan, result));
        third.Outcome!.ProjectId.Should().Be(first.Outcome.ProjectId);
        _projects.List().Count(p => p.Name == "Shop").Should().Be(1);
        third.Outcome.Import.Refusal.Should().Contain("already holds", "the import ran once and is refused the second time, in words");
        (await _trackers.For(new ProjectId(first.Outcome.ProjectId)).GetByNumberAsync(7)).Should().BeNull("nothing was imported twice");
    }

    [Fact]
    public async Task A_folder_with_a_conversation_running_is_refused_before_any_project_is_made()
    {
        _running.Add(Folder);
        var before = _projects.List().Count;

        var applied = await _setup.ApplyAsync(Folder, new SetupDraft { NewName = "Shop" });

        applied.Outcome.Should().BeNull();
        applied.Refusal.Should().Contain("conversation running");
        _projects.List().Should().HaveCount(before);
        WorkspaceProjects.For(_workspaces, Folder).Should().BeNull();
    }

    [Theory]
    [InlineData("   ", "Give the project a name.")]
    [InlineData("A name that is far too long for any project to carry around in a stepper, a title bar, or a list", "Keep the name under 80 characters.")]
    public async Task A_bad_name_is_refused_in_words_on_the_name_field(string name, string words)
    {
        var applied = await _setup.ApplyAsync(Folder, new SetupDraft { NewName = name });

        (applied.Refusal, applied.Field).Should().Be((words, "name"));
    }

    [Fact]
    public async Task Start_empty_makes_a_project_named_after_the_folder_or_the_next_free_name()
    {
        _projects.Create("Shop");

        var applied = await _setup.StartEmptyAsync(Folder);

        var outcome = applied.Outcome!;
        outcome.ProjectName.Should().Be("Shop 2");
        (outcome.Board, outcome.Wiki).Should().Be((ProviderChoice.None, ProviderChoice.None));
        outcome.Import.Skipped.Should().BeTrue();
        (outcome.Model, outcome.Effort, outcome.TrustEdits).Should().Be(((string?)null, (string?)null, false));
        _trust.For(Folder).Should().Be(WorkspaceTrust.Ask);
    }

    [Fact]
    public async Task Joining_a_project_keeps_its_own_providers_and_does_not_import()
    {
        var shared = _projects.Create("Shared");
        _providers.Set(shared.Id, new ProjectProviderChoice(ProviderChoice.Install, ProviderChoice.None));

        var applied = await _setup.ApplyAsync(Folder, new SetupDraft
        {
            ProjectMode = SetupProjectMode.Join, JoinProjectId = shared.Id.Value, Board = ProviderChoice.None, Wiki = ProviderChoice.Install,
        });

        applied.Outcome!.ProjectIsNew.Should().BeFalse();
        _providers.For(shared.Id).Should().Be(new ProjectProviderChoice(ProviderChoice.Install, ProviderChoice.None));
        WorkspaceProjects.For(_workspaces, Folder).Should().Be(shared.Id);
        ProjectAgentDefaults.Read(_settings, shared.Id).Should().Be(AgentDefault.None, "nothing was chosen, so the joined project's own defaults are untouched");
    }

    [Fact]
    public void A_projects_own_default_is_laid_over_the_installs_part_by_part()
    {
        var project = _projects.Create("P").Id;
        AgentDefaultsSetting.Write(_settings, AgentDefaults.None with { NewConversation = new AgentDefault(AgentDefaultsSetting.ModelIds[0], AgentEffort.Low) });
        ProjectAgentDefaults.Write(_settings, project, new AgentDefault(null, AgentEffort.Max));

        ProjectAgentDefaults.NewConversation(_settings, project).Should().Be(new AgentDefault(AgentDefaultsSetting.ModelIds[0], AgentEffort.Max));
        ProjectAgentDefaults.NewConversation(_settings, null).Should().Be(new AgentDefault(AgentDefaultsSetting.ModelIds[0], AgentEffort.Low));
    }
}
