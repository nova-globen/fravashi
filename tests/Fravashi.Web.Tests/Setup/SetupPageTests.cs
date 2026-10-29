using Bunit;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Application.Setup;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Web.Components.Pages;
using Fravashi.Web.Components.Setup;
using Fravashi.Web.Components.Shared;
using Fravashi.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests.Setup;

/// <summary>
/// Item #373, slices 2 and 3 (<c>docs/plans/first-open-setup.md</c>, with the host's decisions): the recommended setup and
/// every step, over the real stores. Each step: its defaults and exact copy, Skip doing what its caption says, Back
/// keeping the answers, its errors in words with role alert, and the draft saved after a change and read back on a fresh
/// render (resume). The recommended setup's one click applies exactly what it showed.
/// </summary>
public sealed class SetupPageTests : IDisposable
{
    private readonly SetupWindow _window = new(formerProjectId: null);

    public void Dispose() => _window.Dispose();

    private static TimeSpan Wait => TimeSpan.FromSeconds(30);

    private void Fixture()
    {
        _window.Write("README.md", "# Stomme\n");
        _window.Write("docs/guide.md", "# The guide\n");
        _window.Write("docs/adr/0001-x.md", "# ADR 0001\n");
        _window.Write("docs/backlog.md", "### 1. One\n**Status** Ready\n\n### 2. Two\n**Status** Done\n");
        _window.Write("packages/a/README.md", "# A\n");
    }

    private string Uri(BunitContext ctx) => ctx.Services.GetRequiredService<NavigationManager>().Uri;

    private static IRenderedComponent<SetupPage> Step(BunitContext ctx, string step) => Settled(ctx, ctx.Render<SetupPage>(p => p.Add(c => c.Step, step)));

    /// <summary>
    /// The page once what it reads off the render path (git, and the scan when the screen shows one) has answered, so a
    /// test never clicks a button the next repaint replaces.
    /// </summary>
    private static IRenderedComponent<SetupPage> Settled(BunitContext ctx, IRenderedComponent<SetupPage> cut)
    {
        var scan = ctx.Services.GetRequiredService<SetupScan>();
        try
        {
            cut.WaitForState(() => cut.Instance.Source.State != SetupSourceState.Reading && !scan.IsRunning, Wait);
        }
        catch (Bunit.Extensions.WaitForHelpers.WaitForFailedException)
        {
            throw new InvalidOperationException($"source {cut.Instance.Source.State}, scan running {scan.IsRunning}, result {scan.Result is not null}, error {scan.Error}");
        }
        cut.Render();
        return cut;
    }

    private SetupDraft Draft => _window.Drafts.Get(SetupWindow.FolderId)!;

    /// <summary>A draft that has seen every question, on <paramref name="step"/>.</summary>
    private void OnStep(string step, Action<SetupDraft>? change = null)
    {
        var draft = new SetupDraft { NewName = "Stomme", StepByStep = true, Step = step };
        foreach (var s in SetupSteps.Questions)
        {
            draft.See(s);
        }

        change?.Invoke(draft);
        _window.Drafts.Save(SetupWindow.FolderId, draft);
    }

    /// <summary>Waits until every title on screen has been read, so no repaint replaces a row between finding and clicking.</summary>
    private static void TitlesRead(IRenderedComponent<SetupPage> cut) =>
        cut.WaitForState(() => !cut.Markup.Contains("setup-title-pending", StringComparison.Ordinal), Wait);

    /// <summary>A folder whose look-through never finishes, so the "looking" state can be seen.</summary>
    private sealed class NeverAnswering : Fravashi.Application.Migration.IRepositoryScans
    {
        public Task<Fravashi.Application.Migration.RepositoryScan?> ForWindowAsync(CancellationToken ct = default) =>
            Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => (Fravashi.Application.Migration.RepositoryScan?)null, TaskScheduler.Default);
    }

    // ---- The recommended setup ----

    [Fact]
    public void The_recommended_setup_says_what_was_found_and_what_will_happen()
    {
        Fixture();
        _window.Git = new SetupFakeGit();
        using var ctx = _window.Context();
        var cut = Settled(ctx, ctx.Render<SetupPage>());

        Assert.Equal("Set up Stomme", cut.Find(".studio-page-bar-title").TextContent.Trim());
        Assert.Equal("Set up Stomme", cut.Find("#setup-heading").TextContent.Trim());
        Assert.Equal("A new project, Stomme", cut.Find("[data-testid=setup-plan-project]").TextContent.Trim());
        Assert.Equal("Board and wiki here in Fravashi", cut.Find("[data-testid=setup-plan-providers]").TextContent.Trim());
        Assert.Equal("Branch main, origin on GitHub", cut.Find("[data-testid=setup-plan-source]").TextContent.Trim());
        Assert.StartsWith("Found 3 documents and 2 work items to import", cut.Find("[data-testid=setup-plan-import]").TextContent.Trim());
        Assert.Equal("Agents use the install's defaults and ask before every change", cut.Find("[data-testid=setup-plan-agents]").TextContent.Trim());
        Assert.Equal("Set up Stomme", cut.Find("[data-testid=setup-apply]").TextContent.Trim());
        Assert.Equal("Choose step by step", cut.Find("[data-testid=setup-step-by-step]").TextContent.Trim());
        Assert.Equal("polite", cut.Find("[data-testid=setup-scan-status]").GetAttribute("aria-live"));
        Assert.Empty(cut.FindAll("[data-testid=setup-default-note]"));
    }

    [Fact]
    public void While_the_folder_is_looked_through_the_import_line_says_so()
    {
        _window.Scans = new NeverAnswering();
        using var ctx = _window.Context();
        var cut = ctx.Render<SetupPage>();

        Assert.StartsWith("Looking through Stomme ...", cut.Find("[data-testid=setup-plan-import]").TextContent.Trim());
    }

    [Fact]
    public void One_click_applies_exactly_the_default_selection_and_lands_on_Done()
    {
        Fixture();
        using var ctx = _window.Context();
        var cut = Settled(ctx, ctx.Render<SetupPage>());

        cut.Find("[data-testid=setup-apply]").Click();
        cut.WaitForState(() => Uri(ctx).EndsWith("/setup/done", StringComparison.Ordinal), Wait);

        var outcome = Draft.Outcome!;
        var project = new ProjectId(outcome.ProjectId);
        Assert.Equal(project, WorkspaceProjects.For(_window.Workspaces, SetupWindow.FolderId));
        Assert.Equal(("Stomme", true), (outcome.ProjectName, outcome.ProjectIsNew));
        Assert.Equal((2, 3), (outcome.Import.WorkItems, outcome.Import.Files));
        Assert.Equal(new ProjectProviderChoice(ProviderChoice.None, ProviderChoice.None), _window.Providers.For(project));
        Assert.Equal(WorkspaceTrust.Ask, _window.Trust.For(SetupWindow.FolderId));
    }

    [Fact]
    public void A_folder_from_the_shared_project_says_so_and_offers_to_join_it()
    {
        using var window = new SetupWindow(formerProjectId: "default");
        using var ctx = window.Context();
        var cut = Settled(ctx, ctx.Render<SetupPage>());

        Assert.Contains("Earlier versions of Fravashi put every new folder in one shared project, Default.", cut.Find("[data-testid=setup-default-note]").TextContent);
        cut.Find("[data-testid=setup-join-default]").Click();

        var draft = window.Drafts.Get(SetupWindow.FolderId)!;
        Assert.Equal((SetupProjectMode.Join, "default", true), (draft.ProjectMode, draft.JoinProjectId, draft.StepByStep));
        Assert.EndsWith("/setup/project", Uri(ctx), StringComparison.Ordinal);
    }

    [Fact]
    public void Choose_step_by_step_opens_the_steps_on_Project_with_the_same_draft()
    {
        using var ctx = _window.Context();
        var cut = Settled(ctx, ctx.Render<SetupPage>());

        cut.Find("[data-testid=setup-step-by-step]").Click();

        Assert.EndsWith("/setup/project", Uri(ctx), StringComparison.Ordinal);
        Assert.Equal(("Stomme", true), (Draft.NewName, Draft.StepByStep));
    }

    [Fact]
    public void Start_empty_makes_a_bare_project_and_lands_on_Done()
    {
        using var ctx = _window.Context();
        var cut = Settled(ctx, ctx.Render<SetupPage>());

        cut.Find("[data-testid=setup-start-empty]").Click();
        cut.WaitForState(() => Uri(ctx).EndsWith("/setup/done", StringComparison.Ordinal), Wait);

        Assert.True(Draft.Outcome!.Import.Skipped);
        Assert.NotNull(WorkspaceProjects.For(_window.Workspaces, SetupWindow.FolderId));
    }

    [Fact]
    public void Not_now_goes_back_where_the_person_came_from_and_is_remembered()
    {
        using var ctx = _window.Context("/setup?from=%2Fworkitems");
        var cut = Settled(ctx, ctx.Render<SetupPage>());

        cut.Find("[data-testid=setup-not-now]").Click();

        Assert.EndsWith("/workitems", Uri(ctx), StringComparison.Ordinal);
        Assert.True(Draft.NotNow);
        Assert.Null(WorkspaceProjects.For(_window.Workspaces, SetupWindow.FolderId));
    }

    [Fact]
    public void A_setup_left_part_way_resumes_on_the_step_reached()
    {
        OnStep(SetupSteps.Board);
        using var ctx = _window.Context();
        ctx.Render<SetupPage>();

        Assert.EndsWith("/setup/board", Uri(ctx), StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_ahead_of_what_was_seen_is_not_reachable()
    {
        _window.Drafts.Save(SetupWindow.FolderId, new SetupDraft { NewName = "Stomme", StepByStep = true });
        using var ctx = _window.Context("/setup/agents");
        ctx.Render<SetupPage>(p => p.Add(c => c.Step, SetupSteps.Agents));

        Assert.EndsWith("/setup/project", Uri(ctx), StringComparison.Ordinal);
    }

    // ---- Project ----

    [Fact]
    public void Project_has_the_folders_name_no_Skip_and_no_Back_and_the_heading_is_the_question()
    {
        using var ctx = _window.Context("/setup/project");
        var cut = Step(ctx, SetupSteps.Project);

        Assert.Equal("Which project is Stomme part of?", cut.Find("#setup-step-heading").TextContent.Trim());
        Assert.Equal("-1", cut.Find("#setup-step-heading").GetAttribute("tabindex"));
        Assert.Equal("Stomme", cut.Find("[data-testid=setup-project-name]").GetAttribute("value"));
        Assert.True(cut.Find("[data-testid=setup-mode-new]").HasAttribute("checked"));
        Assert.Empty(cut.FindAll("[data-testid=setup-skip]"));
        Assert.Empty(cut.FindAll("[data-testid=setup-back]"));
        Assert.Equal("step", cut.Find("[data-testid=setup-step-project] [aria-current]").GetAttribute("aria-current"));
        Assert.Equal("Setup steps", cut.Find("nav.setup-stepper").GetAttribute("aria-label"));
    }

    [Fact]
    public void A_name_already_taken_defaults_to_the_next_free_one_and_says_why()
    {
        _window.Projects.Create("Stomme");
        using var ctx = _window.Context("/setup/project");
        var cut = Step(ctx, SetupSteps.Project);

        Assert.Equal("Stomme 2", cut.Find("[data-testid=setup-project-name]").GetAttribute("value"));
        Assert.Equal(
            "A project called Stomme already exists. Join it below instead, or keep a new one with this name.",
            cut.Find("[data-testid=setup-name-collision]").TextContent.Trim());
    }

    [Fact]
    public void An_empty_name_is_refused_in_words_on_the_field_and_the_step_stays()
    {
        using var ctx = _window.Context("/setup/project");
        var cut = Step(ctx, SetupSteps.Project);

        cut.Find("[data-testid=setup-project-name]").Input("   ");
        cut.Find("[data-testid=setup-next]").Click();

        var error = cut.Find("[data-testid=setup-name-error]");
        Assert.Equal("Give the project a name.", error.TextContent.Trim());
        Assert.Equal("alert", error.GetAttribute("role"));
        Assert.Equal("setup-name-error", cut.Find("[data-testid=setup-project-name]").GetAttribute("aria-describedby"));
        Assert.Equal("true", cut.Find("[data-testid=setup-project-name]").GetAttribute("aria-invalid"));
        Assert.EndsWith("/setup/project", Uri(ctx), StringComparison.Ordinal);
    }

    [Fact]
    public void Joining_lists_the_projects_with_what_they_hold_and_the_shared_one_by_its_story()
    {
        var shop = _window.Projects.Create("Shop");
        using var ctx = _window.Context("/setup/project");
        var cut = Step(ctx, SetupSteps.Project);

        cut.Find("[data-testid=setup-mode-join]").Change(true);

        var rows = cut.FindAll("[data-testid=setup-join-row]");
        Assert.Contains(rows, r => r.TextContent.Contains("Default (the shared project earlier versions used)", StringComparison.Ordinal));
        var row = rows.Single(r => r.GetAttribute("data-project") == shop.Id.Value);
        Assert.Contains("0 work items, 0 documents, 0 folders", row.TextContent, StringComparison.Ordinal);
        row.QuerySelector("input")!.Change(true);
        Assert.Equal((SetupProjectMode.Join, shop.Id.Value), (Draft.ProjectMode, Draft.JoinProjectId));
    }

    [Fact]
    public void Next_saves_the_name_and_Back_from_Source_finds_it_there()
    {
        using var ctx = _window.Context("/setup/project");
        var cut = Step(ctx, SetupSteps.Project);
        cut.Find("[data-testid=setup-project-name]").Input("Core platform");
        cut.Find("[data-testid=setup-next]").Click();
        Assert.EndsWith("/setup/source", Uri(ctx), StringComparison.Ordinal);

        using var back = _window.Context("/setup/source");
        var source = Step(back, SetupSteps.Source);
        source.Find("[data-testid=setup-back]").Click();
        Assert.EndsWith("/setup/project", Uri(back), StringComparison.Ordinal);

        using var again = _window.Context("/setup/project");
        Assert.Equal("Core platform", Step(again, SetupSteps.Project).Find("[data-testid=setup-project-name]").GetAttribute("value"));
    }

    // ---- Source ----

    [Fact]
    public void Source_shows_the_branch_and_remote_and_how_to_turn_on_pull_requests()
    {
        OnStep(SetupSteps.Source);
        _window.Git = new SetupFakeGit();
        using var ctx = _window.Context("/setup/source");
        var cut = Step(ctx, SetupSteps.Source);
        cut.WaitForState(() => cut.FindAll("[data-testid=setup-source-facts]").Count > 0, Wait);

        Assert.Equal("Where does Stomme's code live?", cut.Find("#setup-step-heading").TextContent.Trim());
        Assert.Equal("main", cut.Find("[data-testid=setup-source-branch]").TextContent.Trim());
        Assert.Contains("https://github.com/acme/shop.git", cut.Find("[data-testid=setup-source-remote]").TextContent, StringComparison.Ordinal);
        Assert.Contains("(GitHub)", cut.Find("[data-testid=setup-source-remote]").TextContent, StringComparison.Ordinal);
        Assert.Contains("add a token for github.com in", cut.Find("[data-testid=setup-source-pr-token]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=setup-skip]"));
    }

    [Fact]
    public void A_folder_that_is_not_a_repository_is_told_so_calmly()
    {
        OnStep(SetupSteps.Source);
        _window.Git = new SetupFakeGit { IsRepository = false };
        using var ctx = _window.Context("/setup/source");
        var cut = Step(ctx, SetupSteps.Source);
        cut.WaitForState(() => cut.FindAll("[data-testid=setup-source-not-git]").Count > 0, Wait);

        Assert.Equal(
            "Stomme is not a Git repository. The board and the wiki work without one, and Git features turn on when it becomes one.",
            System.Text.RegularExpressions.Regex.Replace(cut.Find("[data-testid=setup-source-not-git]").TextContent, @"\s+", " ").Trim());
    }

    [Fact]
    public void Git_that_cannot_be_read_is_an_alert_with_Retry_and_the_step_can_still_be_left()
    {
        OnStep(SetupSteps.Source);
        using var ctx = _window.Context("/setup/source");
        var cut = Step(ctx, SetupSteps.Source);
        cut.WaitForState(() => cut.FindAll("[data-testid=setup-source-failed]").Count > 0, Wait);

        Assert.Equal("alert", cut.Find("[data-testid=setup-source-failed]").GetAttribute("role"));
        Assert.Contains("Git could not be read in this folder: Git cannot be reached from this window. You can go on", cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[data-testid=setup-source-retry]"));
        cut.Find("[data-testid=setup-next]").Click();
        Assert.EndsWith("/setup/board", Uri(ctx), StringComparison.Ordinal);
    }

    // ---- Board and Wiki ----

    [Fact]
    public void Board_defaults_to_here_and_offers_to_connect_when_the_install_has_no_board()
    {
        OnStep(SetupSteps.Board);
        using var ctx = _window.Context("/setup/board");
        var cut = Step(ctx, SetupSteps.Board);

        Assert.Equal("Where should Stomme's work items live?", cut.Find("#setup-step-heading").TextContent.Trim());
        Assert.True(cut.Find("[data-testid=setup-provider-local-input]").HasAttribute("checked"));
        Assert.Contains("Here in Fravashi", cut.Find("[data-testid=setup-provider-local]").TextContent, StringComparison.Ordinal);
        Assert.Equal("/settings/board", cut.Find("[data-testid=setup-provider-connect] a").GetAttribute("href"));
        Assert.Equal("Skipping keeps a local board.", cut.Find(".setup-skip-caption").TextContent.Trim());
    }

    [Fact]
    public void An_installs_connection_is_offered_second_and_never_preselected_and_Skip_keeps_a_local_board()
    {
        // The v0.9.100 check: the install's only connection was made for ANOTHER project, and a new folder defaulted into
        // it. A new project's board and wiki are here; the connection is offered, named by what it points at.
        _window.Settings.Set("Onboarding:Board:Provider", "Jira");
        _window.Settings.Set("Onboarding:Board:Project", "CORE");
        new Fravashi.Infrastructure.Persistence.SqliteProviderConnectionStore(_window.Factory).Upsert(
            new ProviderConnection("board", ProviderKind.Board, "https://acme.atlassian.net", "ref", "Verified", DateTimeOffset.UtcNow));
        using (var first = _window.Context())
        {
            var recommended = Settled(first, first.Render<SetupPage>());
            Assert.Equal("Board and wiki here in Fravashi", recommended.Find("[data-testid=setup-plan-providers]").TextContent.Trim());
            recommended.Find("[data-testid=setup-step-by-step]").Click();
        }

        Assert.Equal(ProviderChoice.None, Draft.Board);
        var draft = Draft;
        foreach (var s in SetupSteps.Questions)
        {
            draft.See(s);
        }

        _window.Drafts.Save(SetupWindow.FolderId, draft);
        using var ctx = _window.Context("/setup/board");
        var cut = Step(ctx, SetupSteps.Board);
        var choices = cut.FindAll("[data-testid^=setup-provider-][data-testid$=-input]");
        Assert.Equal(["setup-provider-local-input", "setup-provider-install-input"], choices.Select(c => c.GetAttribute("data-testid")!).ToArray());
        Assert.True(cut.Find("[data-testid=setup-provider-local-input]").HasAttribute("checked"));
        Assert.False(cut.Find("[data-testid=setup-provider-install-input]").HasAttribute("checked"));
        Assert.Contains("Follow Jira project CORE", cut.Find("[data-testid=setup-provider-install]").TextContent, StringComparison.Ordinal);
        Assert.Equal(
            "The install's connection, at acme.atlassian.net. Choose it only if Stomme's work items belong on that board, beside the work already there. Nothing is sent until you say so.",
            cut.Find("[data-testid=setup-provider-install-sub]").TextContent.Trim());

        // Chosen on purpose, it is what the recommended line says.
        cut.Find("[data-testid=setup-provider-install-input]").Change(true);
        Assert.Equal(ProviderChoice.Install, Draft.Board);
        Assert.Equal("Board follows Jira project CORE; wiki here in Fravashi",
            new SetupConnections(new SetupConnection("Jira", "acme.atlassian.net", "Jira project CORE"), null).Describe(Draft.Board, Draft.Wiki));

        cut.Find("[data-testid=setup-skip]").Click();

        Assert.Equal(ProviderChoice.None, Draft.Board);
        Assert.True(Draft.IsSkipped(SetupSteps.Board));
        Assert.EndsWith("/setup/wiki", Uri(ctx), StringComparison.Ordinal);
    }

    [Fact]
    public void An_installs_wiki_connection_is_named_by_the_project_it_was_made_for()
    {
        _window.Settings.Set("Onboarding:Wiki:Provider", Fravashi.Application.Onboarding.OnboardingProviders.AzureDevOpsWiki);
        _window.Settings.Set("Onboarding:Draft:Wiki:SpaceKey", "~owner");
        new Fravashi.Infrastructure.Persistence.SqliteProviderConnectionStore(_window.Factory).Upsert(
            new ProviderConnection("wiki", ProviderKind.Wiki, "https://dev.azure.com/nova-globen-ab/Fravashi", "ref", "Verified", DateTimeOffset.UtcNow));
        OnStep(SetupSteps.Wiki);
        using var ctx = _window.Context("/setup/wiki");
        var cut = Step(ctx, SetupSteps.Wiki);

        Assert.True(cut.Find("[data-testid=setup-provider-local-input]").HasAttribute("checked"));
        Assert.False(cut.Find("[data-testid=setup-provider-install-input]").HasAttribute("checked"));
        Assert.Contains("Publish to the Azure DevOps wiki of Fravashi", cut.Find("[data-testid=setup-provider-install]").TextContent, StringComparison.Ordinal);
        Assert.StartsWith("The install's connection, at dev.azure.com/nova-globen-ab/Fravashi. Choose it only if Stomme's documents belong there",
            cut.Find("[data-testid=setup-provider-install-sub]").TextContent.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_skipped_step_says_Skipped_in_the_stepper_in_words()
    {
        OnStep(SetupSteps.Wiki, d => d.MarkSkipped(SetupSteps.Board, true));
        using var ctx = _window.Context("/setup/wiki");
        var cut = Step(ctx, SetupSteps.Wiki);

        Assert.Contains("Skipped", cut.Find("[data-testid=setup-step-board]").TextContent, StringComparison.Ordinal);
        Assert.Equal("Where should Stomme's documents live?", cut.Find("#setup-step-heading").TextContent.Trim());
        Assert.Equal("Skipping keeps a local wiki.", cut.Find(".setup-skip-caption").TextContent.Trim());
    }

    [Fact]
    public void A_joined_project_keeps_its_own_board_and_the_step_says_so()
    {
        var shared = _window.Projects.Create("Shared");
        OnStep(SetupSteps.Board, d =>
        {
            d.ProjectMode = SetupProjectMode.Join;
            d.JoinProjectId = shared.Id.Value;
        });
        using var ctx = _window.Context("/setup/board");
        var cut = Step(ctx, SetupSteps.Board);

        Assert.Contains("Shared keeps its board where it is", cut.Find("[data-testid=setup-provider-joined]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=setup-provider-local]"));
    }

    // ---- Import ----

    [Fact]
    public async Task Import_lists_what_was_found_with_its_defaults_and_a_change_is_saved()
    {
        Fixture();
        OnStep(SetupSteps.Import);
        using var ctx = _window.Context("/setup/import");
        var cut = Step(ctx, SetupSteps.Import);
        TitlesRead(cut);

        Assert.Equal("Bring in what Stomme already has", cut.Find("#setup-step-heading").TextContent.Trim());
        var row = cut.Find("[data-testid=setup-import-row][data-path='docs/backlog.md']");
        Assert.Equal("true", row.QuerySelector("[data-testid=setup-import-check]")!.GetAttribute("aria-checked"));
        Assert.Equal("Import docs/backlog.md", row.QuerySelector("[data-testid=setup-import-check]")!.GetAttribute("aria-label"));
        Assert.Equal("2 items, numbers kept", row.QuerySelector("[data-testid=setup-import-note]")!.TextContent.Trim());
        Assert.Equal("docs/backlog.md becomes", row.QuerySelector("select")!.GetAttribute("aria-label"));
        Assert.Equal("col", cut.Find("th").GetAttribute("scope"));
        Assert.Equal("mixed", cut.Find("[data-testid=setup-import-all]").GetAttribute("aria-checked"));

        // A package README is listed and not checked; checking it is remembered.
        // A folder with nothing chosen in it starts folded, even with one file in it; it unfolds and folds from its
        // header, which says so.
        var toggle = "[data-testid=setup-import-group][data-folder='packages/a'] [data-testid=setup-import-group-toggle]";
        Assert.Equal("false", cut.Find(toggle).GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll("[data-testid=setup-import-row][data-path='packages/a/README.md']"));
        Assert.Contains("0 of 1 chosen", cut.Find(toggle).TextContent, StringComparison.Ordinal);
        await cut.InvokeAsync(() => cut.Find(toggle).Click());
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find(toggle).GetAttribute("aria-expanded")), Wait);
        TitlesRead(cut);
        await cut.InvokeAsync(() => cut.Find("[data-testid=setup-import-row][data-path='packages/a/README.md'] [data-testid=setup-import-check]").Click());
        Assert.True(Draft.Import["packages/a/README.md"].Include);

        // Turning a document into another type is remembered too, and turning it back forgets the change.
        cut.Find("[data-testid=setup-import-row][data-path='docs/guide.md'] select").Change("plan");
        Assert.Equal("plan", Draft.Import["docs/guide.md"].Target);
        cut.Find("[data-testid=setup-import-row][data-path='docs/guide.md'] select").Change("reference");
        Assert.False(Draft.Import.ContainsKey("docs/guide.md"));
    }

    [Fact]
    public void Skip_on_Import_imports_nothing_when_set_up()
    {
        Fixture();
        OnStep(SetupSteps.Agents, d => d.MarkSkipped(SetupSteps.Import, true));
        using var ctx = _window.Context("/setup/agents");
        var cut = Step(ctx, SetupSteps.Agents);

        cut.Find("[data-testid=setup-next]").Click();
        cut.WaitForState(() => Draft.Outcome is not null, Wait);

        Assert.True(Draft.Outcome!.Import.Skipped);
        Assert.Equal(ProjectContents.Empty, new Fravashi.Infrastructure.Persistence.SqliteProjectContents(_window.Factory).Of(new ProjectId(Draft.Outcome.ProjectId)));
    }

    [Fact]
    public void A_folder_with_nothing_to_import_says_so()
    {
        OnStep(SetupSteps.Import);
        using var ctx = _window.Context("/setup/import");
        var cut = Step(ctx, SetupSteps.Import);
        cut.WaitForState(() => cut.FindAll("[data-testid=setup-import-none]").Count > 0, Wait);

        Assert.Contains("No markdown documents or backlog files were found in Stomme.", cut.Find("[data-testid=setup-import-none]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Joining_a_project_that_holds_work_skips_the_import_and_says_why()
    {
        var shared = _window.Projects.Create("Shared");
        await _window.Wikis.For(shared.Id).CreateAsync(new Fravashi.Application.Wiki.NewWikiItem
        {
            Type = Fravashi.Domain.Wiki.WikiItemType.Reference, Title = "Here already",
        });
        OnStep(SetupSteps.Import, d =>
        {
            d.ProjectMode = SetupProjectMode.Join;
            d.JoinProjectId = shared.Id.Value;
        });
        using var ctx = _window.Context("/setup/import");
        var cut = Step(ctx, SetupSteps.Import);

        Assert.Contains("Shared already holds work, so this step is skipped.", cut.Find("[data-testid=setup-import-joined]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=setup-import-table]"));
    }

    // ---- Agents, and the apply ----

    [Fact]
    public void Agents_defaults_to_the_installs_and_Ask_and_its_primary_button_sets_the_folder_up()
    {
        OnStep(SetupSteps.Agents);
        using var ctx = _window.Context("/setup/agents");
        var cut = Step(ctx, SetupSteps.Agents);

        Assert.Equal("How should agents work in Stomme?", cut.Find("#setup-step-heading").TextContent.Trim());
        Assert.Equal(string.Empty, cut.Find("[data-testid=setup-agents-model]").GetAttribute("value"));
        Assert.True(cut.Find("[data-testid=setup-trust-ask-input]").HasAttribute("checked"));
        Assert.Equal("Set up Stomme", cut.Find("[data-testid=setup-next]").TextContent.Trim());

        cut.Find("[data-testid=setup-agents-effort]").Change("high");
        cut.Find("[data-testid=setup-trust-edits-input]").Change(true);
        cut.Find("[data-testid=setup-next]").Click();
        cut.WaitForState(() => Draft.Outcome is not null, Wait);

        Assert.Equal(("high", true), (Draft.Outcome!.Effort, Draft.Outcome.TrustEdits));
        Assert.Equal(WorkspaceTrust.Edits, _window.Trust.For(SetupWindow.FolderId));
        Assert.EndsWith("/setup/done", Uri(ctx), StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_set_up_says_why_and_nothing_was_made()
    {
        _window.RunningIn.Add(SetupWindow.FolderId);
        OnStep(SetupSteps.Agents);
        var before = _window.Projects.List().Count;
        using var ctx = _window.Context("/setup/agents");
        var cut = Step(ctx, SetupSteps.Agents);

        cut.Find("[data-testid=setup-next]").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid=setup-apply-refusal]").Count > 0, Wait);

        Assert.Equal("alert", cut.Find("[data-testid=setup-apply-refusal]").GetAttribute("role"));
        Assert.Contains("conversation running", cut.Find("[data-testid=setup-apply-refusal]").TextContent, StringComparison.Ordinal);
        Assert.Equal(before, _window.Projects.List().Count);
    }

    // ---- Done, and a folder already set up ----

    [Fact]
    public void Done_lists_what_was_done_each_line_a_link_and_offers_the_way_on()
    {
        Fixture();
        using (var first = _window.Context())
        {
            var cut = Settled(first, first.Render<SetupPage>());
            cut.Find("[data-testid=setup-apply]").Click();
            cut.WaitForState(() => Draft.Outcome is not null, Wait);
        }

        using var ctx = _window.Context("/setup/done");
        var done = ctx.Render<SetupPage>(p => p.Add(c => c.Step, SetupSteps.Done));

        Assert.Equal("Stomme is set up", done.Find("#setup-done-heading").TextContent.Trim());
        Assert.Contains("Stomme (new)", done.Find("[data-testid=setup-done-project]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Here in Fravashi, 2 work items imported", done.Find("[data-testid=setup-done-board]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Here in Fravashi, 3 documents imported in 1 folder", done.Find("[data-testid=setup-done-wiki]").TextContent, StringComparison.Ordinal);
        Assert.Contains("The install's defaults, every change asks first", done.Find("[data-testid=setup-done-agents]").TextContent, StringComparison.Ordinal);
        Assert.Equal("/sessions", done.Find("[data-testid=setup-done-start]").GetAttribute("href"));
        Assert.Equal("/board", done.Find("[data-testid=setup-done-open-board]").GetAttribute("href"));
        Assert.Equal("/", done.Find("[data-testid=setup-done-home]").GetAttribute("href"));
    }

    [Fact]
    public void A_folder_already_set_up_is_told_so_and_an_overtaken_draft_is_dropped()
    {
        _window.Drafts.Save(SetupWindow.FolderId, new SetupDraft { NewName = "Left over", StepByStep = true });
        var project = _window.Projects.Create("Core");
        _window.Projects.BindWorkspace(SetupWindow.FolderId, project.Id);
        using var ctx = _window.Context();
        var cut = ctx.Render<SetupPage>();

        Assert.Equal("Stomme is already set up", cut.Find("#setup-already-heading").TextContent.Trim());
        Assert.Null(_window.Drafts.Get(SetupWindow.FolderId));
    }

    // ---- Entry points ----

    [Fact]
    public void The_not_set_up_state_says_Continue_setting_up_once_a_setup_was_started()
    {
        using var ctx = _window.Context("/workitems");
        Assert.Equal("Set up Stomme", ctx.Render<FolderNotSetUp>().Find("[data-testid=folder-set-up]").TextContent.Trim());

        _window.Drafts.Save(SetupWindow.FolderId, new SetupDraft { NewName = "Stomme", StepByStep = true });
        var cut = ctx.Render<FolderNotSetUp>();

        Assert.Equal("Continue setting up", cut.Find("[data-testid=folder-set-up]").TextContent.Trim());
        Assert.Equal("/setup?from=%2Fworkitems", cut.Find("[data-testid=folder-set-up]").GetAttribute("href"));
    }

    // ---- Landing: a window loaded on a folder that is not set up opens on its setup ----

    /// <summary>The landing over a page that counts how often it was started, the way the layout draws it.</summary>
    private static IRenderedComponent<FirstOpenLanding> Land(BunitContext ctx, Action started) =>
        ctx.Render<FirstOpenLanding>(p => p.AddChildContent(builder =>
        {
            builder.OpenComponent<StartedPage>(0);
            builder.AddAttribute(1, nameof(StartedPage.Started), started);
            builder.CloseComponent();
        }));

    private static void WindowHasLoaded(BunitContext ctx, params string[] folders) =>
        ctx.JSInterop.Setup<string?>("sessionStorage.getItem", FirstOpenLandingRule.StorageKey)
            .SetResult(System.Text.Json.JsonSerializer.Serialize(folders));

    [Theory]
    [InlineData("/code/src/Stomme.Server/Calendar.cs?ws=ws-core", "/setup?from=%2Fcode%2Fsrc%2FStomme.Server%2FCalendar.cs")]
    [InlineData("/code?ws=ws-core", "/setup?from=%2Fcode")]
    [InlineData("/workitems?ws=ws-core", "/setup?from=%2Fworkitems")]
    [InlineData("/?ws=ws-core", "/setup")]
    public void A_window_loaded_on_a_folder_that_is_not_set_up_lands_on_its_setup_and_never_starts_the_page(string url, string target)
    {
        // The v0.9.100 check: Stomme's window restored its last Code tab, and that page's restore took the window back
        // to the file after the landing had moved it. The page underneath must never be started.
        using var ctx = _window.Context(url);
        var started = 0;
        Land(ctx, () => started++);

        Assert.EndsWith(target, Uri(ctx), StringComparison.Ordinal);
        Assert.Equal(0, started);
        var remembered = ctx.JSInterop.VerifyInvoke("sessionStorage.setItem");
        Assert.Equal(["fravashi.window.folders", "[\"ws-core\"]"], remembered.Arguments.Select(a => a as string ?? string.Empty).ToArray());
    }

    [Fact]
    public void Once_on_the_setup_the_layout_draws_its_pages_again()
    {
        using var ctx = _window.Context("/code?ws=ws-core");
        var started = 0;
        var cut = Land(ctx, () => started++);
        Assert.Equal(0, started);

        // The router moves to the setup and the layout draws it inside the landing.
        cut.Render(p => p.AddChildContent(builder =>
        {
            builder.OpenComponent<StartedPage>(0);
            builder.AddAttribute(1, nameof(StartedPage.Started), (Action)(() => started++));
            builder.CloseComponent();
        }));
        cut.WaitForAssertion(() => Assert.Equal(1, started));
    }

    [Fact]
    public void A_reload_of_a_window_that_already_landed_goes_where_it_was_asked_to()
    {
        using var ctx = _window.Context("/code/src/A.cs?ws=ws-core");
        WindowHasLoaded(ctx, SetupWindow.FolderId);
        var started = 0;
        Land(ctx, () => started++);

        Assert.Contains("/code/src/A.cs?ws=ws-core", Uri(ctx), StringComparison.Ordinal);
        Assert.Equal(1, started);
    }

    [Fact]
    public void A_link_followed_inside_an_open_window_is_not_taken_to_the_setup()
    {
        // A session row on Home switches the window to another folder with a full load; that is a deep link, not a first open.
        using var ctx = _window.Context("/sessions/5f0c?ws=ws-core");
        WindowHasLoaded(ctx, "ws-other");
        var started = 0;
        Land(ctx, () => started++);

        Assert.Contains("/sessions/5f0c?ws=ws-core", Uri(ctx), StringComparison.Ordinal);
        Assert.Equal(1, started);
    }

    [Fact]
    public void Opening_the_folder_in_a_window_that_was_on_another_one_lands_on_its_setup()
    {
        // The start dialog's Open in an open window loads /code?ws= for the folder: that IS a first open.
        using var ctx = _window.Context("/code?ws=ws-core");
        WindowHasLoaded(ctx, "ws-other");
        var started = 0;
        Land(ctx, () => started++);

        Assert.EndsWith("/setup?from=%2Fcode", Uri(ctx), StringComparison.Ordinal);
        Assert.Equal(0, started);
    }

    [Fact]
    public void When_the_window_cannot_say_what_it_loaded_the_page_is_shown()
    {
        using var ctx = _window.Context("/code/src/A.cs?ws=ws-core");
        ctx.JSInterop.Setup<string?>("sessionStorage.getItem", FirstOpenLandingRule.StorageKey).SetException(new Microsoft.JSInterop.JSException("blocked"));
        var started = 0;
        Land(ctx, () => started++);

        Assert.Contains("/code/src/A.cs?ws=ws-core", Uri(ctx), StringComparison.Ordinal);
        Assert.Equal(1, started);
    }

    [Theory]
    [InlineData("/setup/board?ws=ws-core")]
    [InlineData("/setup?ws=ws-core")]
    public void The_setup_itself_is_never_held_back(string url)
    {
        using var ctx = _window.Context(url);
        var started = 0;
        Land(ctx, () => started++);

        Assert.Contains(url, Uri(ctx), StringComparison.Ordinal);
        Assert.Equal(1, started);
    }

    [Fact]
    public void After_Not_now_the_folder_opens_where_it_was_asked_to()
    {
        _window.Drafts.Save(SetupWindow.FolderId, new SetupDraft { NotNow = true });
        using var ctx = _window.Context("/code/src/A.cs?ws=ws-core");
        var started = 0;
        Land(ctx, () => started++);

        Assert.Contains("/code/src/A.cs?ws=ws-core", Uri(ctx), StringComparison.Ordinal);
        Assert.Equal(1, started);
    }

    [Fact]
    public void A_folder_already_set_up_opens_where_it_was_asked_to()
    {
        _window.Projects.BindWorkspace(SetupWindow.FolderId, _window.Projects.Create("Core").Id);
        using var ctx = _window.Context("/code/src/A.cs?ws=ws-core");
        var started = 0;
        Land(ctx, () => started++);

        Assert.Contains("/code/src/A.cs?ws=ws-core", Uri(ctx), StringComparison.Ordinal);
        Assert.Equal(1, started);
    }

    [Fact]
    public void Not_now_after_a_landing_goes_back_to_the_page_the_window_was_loaded_on()
    {
        using var ctx = _window.Context("/setup?from=%2Fcode%2Fsrc%2FA.cs");
        var cut = Settled(ctx, ctx.Render<SetupPage>());

        cut.Find("[data-testid=setup-not-now]").Click();

        Assert.EndsWith("/code/src/A.cs", Uri(ctx), StringComparison.Ordinal);
        Assert.True(Draft.NotNow);
    }

    /// <summary>Stands in for the page the layout would draw, and says when it was started.</summary>
    private sealed class StartedPage : ComponentBase
    {
        [Parameter]
        public Action? Started { get; set; }

        protected override void OnInitialized() => Started?.Invoke();
    }
}
