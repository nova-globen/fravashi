using Bunit;
using Fravashi.Application.Persistence;
using Fravashi.Application.Migration;
using Fravashi.Application.Projects;
using Fravashi.Application.Repository;
using Fravashi.Domain.Repository;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Web.Components.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 121: choosing a folder's project, over the real stores and the real resolver, so what the section
/// says is what a page opened next would really show.
/// </summary>
public sealed class ProjectSettingsSectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fravashi-project-ui-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteProjectStore _projects;
    private readonly SqliteWorkspaceStore _workspaces;
    private readonly SqliteProjectEnvironment _variables;
    private readonly Fravashi.Infrastructure.Sessions.SqliteWorkspaceTrust _trust;
    private readonly HashSet<string> _running = [];

    public ProjectSettingsSectionTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _projects = new SqliteProjectStore(_factory, TimeProvider.System);
        _workspaces = new SqliteWorkspaceStore(_factory);
        _workspaces.Touch("ws-1", "billing-service", "C:/code/billing-service");
        // A folder put in the default project, as every folder was before item #373; a folder opened for the first
        // time is in none (A_folder_opened_for_the_first_time_is_not_set_up_and_can_be_put_in_a_project).
        _projects.BindWorkspace("ws-1", Fravashi.Domain.Projects.ProjectId.Default);
        _variables = new SqliteProjectEnvironment(_factory, new MemorySecrets(), TimeProvider.System);
        _trust = new Fravashi.Infrastructure.Sessions.SqliteWorkspaceTrust(_factory);
    }

    [Fact]
    public void The_folders_trust_level_can_be_chosen_before_any_conversation_exists()
    {
        // Found in the self-hosting trial (2026-09-27): the work strip is the only other place, and it exists only once
        // a conversation does, so a person could not decide before the first turn.
        using var ctx = Window("ws-1");
        var cut = ctx.Render<ProjectSettingsSection>();

        Assert.Equal("ask", cut.Find("[data-testid=project-trust]").GetAttribute("value"));
        cut.Find("[data-testid=project-trust]").Change("edits");

        Assert.Equal(Fravashi.Domain.AgentSessions.WorkspaceTrust.Edits, _trust.For("ws-1"));
        Assert.Contains("without asking", cut.Find("[data-testid=project-said]").TextContent);
        Assert.Equal(Fravashi.Domain.AgentSessions.WorkspaceTrust.Ask, _trust.For("ws-2"));
    }

    /// <summary>
    /// Backlog 312: opened on a worktree's own workspace, this picker shows and changes the REPOSITORY
    /// folder's level, never the worktree's own id, which AgentApprovalBroker.TrustFor does not read.
    /// </summary>
    [Fact]
    public void Opened_on_a_worktree_the_trust_picker_shows_and_changes_the_repositorys_own_level()
    {
        _workspaces.Touch("wt-1", "billing-service-item-1", "C:/code/billing-service.fravashi-worktrees/1");

        using var ctx = Window("wt-1");
        var cut = ctx.Render<ProjectSettingsSection>();

        Assert.Equal("ask", cut.Find("[data-testid=project-trust]").GetAttribute("value"));
        cut.Find("[data-testid=project-trust]").Change("edits");

        Assert.Equal(Fravashi.Domain.AgentSessions.WorkspaceTrust.Edits, _trust.For("ws-1"));
        // The worktree's own id is never where a level is stored.
        Assert.Equal(Fravashi.Domain.AgentSessions.WorkspaceTrust.Ask, _trust.For("wt-1"));
    }

    /// <summary>
    /// Backlog 312: a worktree whose repository folder has no workspace record has nowhere to read or write
    /// a level, so a choice made here is refused in words rather than silently accepted and forgotten.
    /// </summary>
    [Fact]
    public void A_worktree_whose_repository_has_no_record_refuses_a_chosen_level_in_words()
    {
        _workspaces.Touch("wt-2", "other-service-item-1", "C:/code/other-service.fravashi-worktrees/1");

        using var ctx = Window("wt-2");
        var cut = ctx.Render<ProjectSettingsSection>();

        cut.Find("[data-testid=project-trust]").Change("edits");

        Assert.Contains("repository is not registered", cut.Find("[data-testid=project-refusal]").TextContent);
        Assert.Equal("ask", cut.Find("[data-testid=project-trust]").GetAttribute("value"));
        Assert.Equal(Fravashi.Domain.AgentSessions.WorkspaceTrust.Ask, _trust.For("wt-2"));
        Assert.Equal(Fravashi.Domain.AgentSessions.WorkspaceTrust.Ask, _trust.For("ws-1"));
    }

    /// <summary>The secret store in memory: this test is about the section, not about protection at rest.</summary>
    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public void Set(string reference, string plaintext) => _values[reference] = plaintext;

        public string? Get(string reference) => _values.TryGetValue(reference, out var v) ? v : null;

        public void Remove(string reference) => _values.Remove(reference);
    }

    [Fact]
    public void A_variable_is_added_listed_and_removed_and_a_secret_is_never_shown_again()
    {
        // Backlog 154: what a person types here is what an agent in this folder's project gets.
        using var ctx = Window("ws-1");
        var cut = ctx.Render<ProjectSettingsSection>();

        cut.Find("[data-testid=project-var-name]").Input("API_BASE");
        cut.Find("[data-testid=project-var-input]").Input("https://api.example.test");
        cut.Find("[data-testid=project-var-add]").Click();

        cut.Find("[data-testid=project-var-name]").Input("SERVICE_TOKEN");
        cut.Find("[data-testid=project-var-input]").Input("tok-0123456789abcdef");
        cut.Find("[data-testid=project-var-secret]").Change(true);
        cut.Find("[data-testid=project-var-add]").Click();

        var rows = cut.FindAll("[data-testid=project-var-row]");
        Assert.Equal(2, rows.Count);
        Assert.Contains("https://api.example.test", cut.Markup);
        Assert.DoesNotContain("tok-0123456789abcdef", cut.Markup);
        Assert.Contains("secret, stored", cut.Markup);

        // And the store has what an agent would be handed.
        var resolved = _variables.Resolve(new Fravashi.Domain.Projects.ProjectId("default"));
        Assert.Equal("tok-0123456789abcdef", resolved["SERVICE_TOKEN"]);

        // A name the studio owns is refused in words, not stored.
        cut.Find("[data-testid=project-var-name]").Input("PATH");
        cut.Find("[data-testid=project-var-input]").Input("/evil");
        cut.Find("[data-testid=project-var-add]").Click();
        Assert.Contains("may not set it", cut.Find("[data-testid=project-refusal]").TextContent);
        Assert.Equal(2, cut.FindAll("[data-testid=project-var-row]").Count);

        cut.FindAll("[data-testid=project-var-remove]")[0].Click();
        Assert.Single(cut.FindAll("[data-testid=project-var-row]"));
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

    private BunitContext Window(string? workspaceId)
    {
        var ctx = new BunitContext();
        var selection = new WorkspaceSelection();
        selection.Capture(workspaceId);
        var workspace = new BridgeWorkspaceContext(new ConfigurationBuilder().Build(), selection);
        ctx.Services.AddSingleton<IProjectStore>(_projects);
        ctx.Services.AddSingleton<IWorkspaceStore>(_workspaces);
        ctx.Services.AddSingleton(workspace);
        ctx.Services.AddSingleton<ICurrentProject>(new WorkspaceCurrentProject(workspace, _workspaces));
        ctx.Services.AddSingleton(new WorkspaceProjectBinding(_projects, _workspaces, _running.Contains));
        ctx.Services.AddSingleton<IRepositoryReader>(_folder);
        ctx.Services.AddSingleton<IProjectProviders>(new SqliteProjectProviders(_factory));
        ctx.Services.AddSingleton<IProjectDelivery>(new SqliteProjectDelivery(_factory));
        ctx.Services.AddSingleton<IProjectEnvironment>(_variables);
        ctx.Services.AddSingleton<Fravashi.Application.Sessions.IWorkspaceTrust>(_trust);
        ctx.Services.AddSingleton(new RepositoryImport(
            new Fravashi.Infrastructure.WorkItems.LocalTrackerResolver(_factory, TimeProvider.System),
            new Fravashi.Infrastructure.Wiki.SqliteWikiLibraries(_factory, TimeProvider.System)));
        return ctx;
    }

    /// <summary>The open folder, as the import panel reads it: a tree under docs/ and each file's bytes.</summary>
    private sealed class FakeFolder : IRepositoryReader
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public bool IsConfigured => true;

        public string RootPath => "/workspaces/ws-1";

        public Task<RepositoryNode> GetTreeAsync(string relativePath = "", CancellationToken cancellationToken = default)
        {
            var prefix = relativePath.TrimEnd('/') + "/";
            var children = Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => k[prefix.Length..])
                .Select(rest => rest.Contains('/')
                    ? RepositoryNode.Directory(rest[..rest.IndexOf('/')], prefix + rest[..rest.IndexOf('/')], [])
                    : RepositoryNode.File(rest, prefix + rest))
                .DistinctBy(n => n.RelativePath)
                .ToList();
            return Task.FromResult(RepositoryNode.Directory(relativePath, relativePath, children));
        }

        public Task<RepositoryFile?> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<RepositoryFile?>(null);

        public Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Files.TryGetValue(relativePath, out var text) ? System.Text.Encoding.UTF8.GetBytes(text) : null);
    }

    private readonly FakeFolder _folder = new();

    [Fact]
    public async Task The_import_is_previewed_first_and_then_brings_the_backlog_and_documents_in_with_their_numbers()
    {
        // Backlog 49: the dogfood migration's surface, over the real board and wiki.
        var target = _projects.Create("Imported");
        _projects.BindWorkspace("ws-1", target.Id);
        _folder.Files["docs/backlog.md"] =
            "### 1. First\n**Type** Feature | **Status** Done\n\n### 3. Third\n**Type** Bug | **Status** Backlog\n\n**Blocked by** 1 | **Blocks** -\n";
        _folder.Files["docs/adr/0001-first.md"] = "# ADR 0001\nText.";
        using var ctx = Window("ws-1");
        var cut = ctx.Render<ProjectSettingsSection>();

        cut.Find("[data-testid=import-preview]").Click();
        cut.WaitForAssertion(() => Assert.Matches(
            @"Would import: 2 work items \(1 numbers kept by a placeholder\), 1 blocking links, 0 person blockers, 2 documents",
            System.Text.RegularExpressions.Regex.Replace(cut.Find("[data-testid=import-summary]").TextContent, @"\s+", " ")));

        cut.Find("[data-testid=import-run]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=import-done]")));
        var tracker = new Fravashi.Infrastructure.WorkItems.LocalTrackerResolver(_factory, TimeProvider.System).For(target.Id);
        Assert.Equal("Third", (await tracker.GetByNumberAsync(3))!.Title);
        Assert.Contains("removed", (await tracker.GetByNumberAsync(2))!.Title);
    }

    [Fact]
    public async Task With_documents_left_out_the_import_brings_the_work_items_and_leaves_the_wiki_empty()
    {
        // Backlog 49's cut-over, 2026-09-30: this repository moved its work into the tracker and kept its documents in
        // git, where its code and tests cite them by path.
        var target = _projects.Create("Items only");
        _projects.BindWorkspace("ws-1", target.Id);
        _folder.Files["docs/backlog.md"] = "### 1. First\n**Type** Feature | **Status** Ready\n\nSee [the ADR](adr/0001-first.md).\n";
        _folder.Files["docs/adr/0001-first.md"] = "# ADR 0001\nText.";
        using var ctx = Window("ws-1");
        var cut = ctx.Render<ProjectSettingsSection>();

        cut.Find("[data-testid=import-documents]").Change(false);
        cut.Find("[data-testid=import-preview]").Click();
        cut.WaitForAssertion(() => Assert.Contains(
            "1 work items (0 numbers kept by a placeholder), 0 blocking links, 0 person blockers, 0 documents",
            System.Text.RegularExpressions.Regex.Replace(cut.Find("[data-testid=import-summary]").TextContent, @"\s+", " ")));

        cut.Find("[data-testid=import-run]").Click();
        cut.WaitForAssertion(() => Assert.Contains("documents stay in the folder", cut.Find("[data-testid=import-done]").TextContent));
        var item = await new Fravashi.Infrastructure.WorkItems.LocalTrackerResolver(_factory, TimeProvider.System)
            .For(target.Id).GetByNumberAsync(1);
        Assert.Contains("[the ADR](adr/0001-first.md)", item!.Description);
        Assert.Empty(await new Fravashi.Infrastructure.Wiki.SqliteWikiLibraries(_factory, TimeProvider.System).For(target.Id)
            .ListAsync(new Fravashi.Application.Wiki.WikiItemQuery()));
    }

    [Fact]
    public async Task An_import_into_a_project_that_already_holds_work_is_refused_before_anything_is_written()
    {
        _folder.Files["docs/backlog.md"] = "### 1. First\n**Type** Feature | **Status** Done\n";
        await new Fravashi.Infrastructure.Wiki.SqliteWikiLibraries(_factory, TimeProvider.System).For(DefaultProject.Id)
            .CreateAsync(new Fravashi.Application.Wiki.NewWikiItem { Type = Fravashi.Domain.Wiki.WikiItemType.Draft, Title = "Mine" });
        using var ctx = Window("ws-1");
        var cut = ctx.Render<ProjectSettingsSection>();

        cut.Find("[data-testid=import-preview]").Click();

        cut.WaitForAssertion(() => Assert.Contains("empty project", cut.Find("[data-testid=import-refusal]").TextContent));
        Assert.True(cut.Find("[data-testid=import-run]").HasAttribute("disabled"));
    }

    [Fact]
    public void A_folder_in_the_default_project_says_so()
    {
        using var ctx = Window("ws-1");

        var cut = ctx.Render<ProjectSettingsSection>();

        Assert.Equal("billing-service", cut.Find("[data-testid=project-folder]").TextContent.Trim());
        Assert.Equal("Default", cut.Find("[data-testid=project-current]").TextContent.Trim());
    }

    [Fact]
    public void A_folder_opened_for_the_first_time_is_not_set_up_and_can_be_put_in_a_project()
    {
        // Item #373: no shared bucket. The section shows no project's settings, offers the setup, and keeps the move
        // controls as the manual way into a project.
        _workspaces.Touch("ws-new", "Stomme", "C:/code/Stomme");
        var team = _projects.Create("Team");
        using var ctx = Window("ws-new");
        var cut = ctx.Render<ProjectSettingsSection>();

        Assert.Equal("Not set up yet", cut.Find("[data-testid=project-current]").TextContent.Trim());
        Assert.Equal("/setup", cut.Find("[data-testid=folder-set-up]").GetAttribute("href"));
        Assert.Empty(cut.FindAll("[data-testid=project-board-provider]"));
        Assert.Empty(cut.FindAll("[data-testid=project-var-name]"));
        Assert.Empty(cut.FindAll("[data-testid=project-rename]"));
        Assert.Empty(cut.FindAll("[data-testid=import-panel]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=project-trust]"));
        Assert.True(cut.Find("[data-testid=project-move]").HasAttribute("disabled"), "nothing is picked yet");

        cut.Find("[data-testid=project-pick]").Change(team.Id.Value);
        cut.Find("[data-testid=project-move]").Click();

        Assert.Equal("Team", cut.Find("[data-testid=project-current]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=folder-not-set-up]"));
        Assert.Equal(team.Id, ctx.Services.GetRequiredService<ICurrentProject>().Id);
    }

    [Fact]
    public void A_new_project_is_made_and_the_folder_is_in_it_afterwards()
    {
        using var ctx = Window("ws-1");
        var cut = ctx.Render<ProjectSettingsSection>();

        cut.Find("[data-testid=project-new-name]").Input("Billing rewrite");
        cut.Find("[data-testid=project-create]").Click();

        Assert.Equal("Billing rewrite", cut.Find("[data-testid=project-current]").TextContent.Trim());
        Assert.Contains("Billing rewrite", cut.Find("[data-testid=project-said]").TextContent);

        // What a page opened next would resolve, through the same resolver the pages use.
        Assert.Equal("Billing rewrite", _projects.Get(ctx.Services.GetRequiredService<ICurrentProject>().Require())!.Name);
    }

    [Fact]
    public void The_folders_project_can_be_renamed_and_keeps_its_folder()
    {
        var first = _projects.Create("Fravashi");
        _projects.BindWorkspace("ws-1", first.Id);
        using var ctx = Window("ws-1");
        var cut = ctx.Render<ProjectSettingsSection>();

        cut.Find("[data-testid=project-rename-name]").Input("Fravashi (first import)");
        cut.Find("[data-testid=project-rename]").Click();

        Assert.Equal("Fravashi (first import)", cut.Find("[data-testid=project-current]").TextContent.Trim());
        Assert.Equal("Fravashi (first import)", _projects.Get(first.Id)!.Name);
        Assert.Equal(first.Id, ctx.Services.GetRequiredService<ICurrentProject>().Id);
    }

    [Fact]
    public void A_project_can_follow_no_board_and_no_wiki_and_back()
    {
        // Backlog 142: the owner's "disable the provider" before the cut-over.
        using var ctx = Window("ws-1");
        var cut = ctx.Render<ProjectSettingsSection>();
        var providers = new SqliteProjectProviders(_factory);

        cut.Find("[data-testid=project-board-provider]").Change("none");
        cut.Find("[data-testid=project-wiki-provider]").Change("none");

        Assert.Equal(new ProjectProviderChoice(ProviderChoice.None, ProviderChoice.None), providers.For(DefaultProject.Id));
        Assert.Contains("published nowhere", cut.Find("[data-testid=project-wiki-provider]").TextContent);
        Assert.Contains("follows no provider", cut.Find("[data-testid=project-said]").TextContent);

        cut.Find("[data-testid=project-board-provider]").Change("install");
        Assert.Equal(new ProjectProviderChoice(ProviderChoice.Install, ProviderChoice.None), providers.For(DefaultProject.Id));
    }

    [Fact]
    public void The_autopilot_delivery_choice_defaults_to_branch_and_pull_request_and_persists()
    {
        // Backlog 298 (ADR 0031 decision 3).
        using var ctx = Window("ws-1");
        var cut = ctx.Render<ProjectSettingsSection>();
        var delivery = new SqliteProjectDelivery(_factory);

        Assert.Equal("branch_and_pull_request", cut.Find("[data-testid=project-autopilot-delivery]").GetAttribute("value"));

        cut.Find("[data-testid=project-autopilot-delivery]").Change("commit_to_main");

        Assert.Equal(Fravashi.Domain.Autopilot.AutopilotDeliveryMode.CommitToMain, delivery.For(DefaultProject.Id));
        Assert.Contains("commits straight to main", cut.Find("[data-testid=project-said]").TextContent);
    }

    [Fact]
    public void A_folder_is_moved_to_a_project_picked_from_the_list()
    {
        var second = _projects.Create("Second");
        using var ctx = Window("ws-1");
        var cut = ctx.Render<ProjectSettingsSection>();

        cut.Find("[data-testid=project-pick]").Change(second.Id.Value);
        cut.Find("[data-testid=project-move]").Click();

        Assert.Equal("Second", cut.Find("[data-testid=project-current]").TextContent.Trim());
    }

    [Fact]
    public void A_folder_with_a_conversation_running_is_refused_in_words_and_stays_where_it_is()
    {
        var second = _projects.Create("Second");
        _running.Add("ws-1");
        using var ctx = Window("ws-1");
        var cut = ctx.Render<ProjectSettingsSection>();

        cut.Find("[data-testid=project-pick]").Change(second.Id.Value);
        cut.Find("[data-testid=project-move]").Click();

        Assert.Contains("conversation running", cut.Find("[data-testid=project-refusal]").TextContent);
        Assert.Equal("Default", cut.Find("[data-testid=project-current]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=project-said]"));
    }

    [Fact]
    public void A_window_with_no_folder_open_offers_nothing_to_move()
    {
        using var ctx = Window(null);

        var cut = ctx.Render<ProjectSettingsSection>();

        Assert.NotEmpty(cut.FindAll("[data-testid=project-no-folder]"));
        Assert.Empty(cut.FindAll("[data-testid=project-move]"));
    }
}
