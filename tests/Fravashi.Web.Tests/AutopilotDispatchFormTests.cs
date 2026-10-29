using Bunit;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Application.Sessions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Autopilot;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.WorkItems;
using Fravashi.Web.Components.Autopilot;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 298 (ADR 0031 decisions 1 to 3): the dispatch form, over the real local board and the real run
/// store so a dispatch here is a dispatch any page reusing this component would really see.
/// </summary>
public sealed class AutopilotDispatchFormTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-autopilot-dispatch-form-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly LocalTrackerResolver _resolver;
    private readonly ITracker _board;
    private readonly SqliteProjectDelivery _delivery;
    private readonly SqliteAutopilotRunStore _runs;
    private readonly MemorySecrets _secrets = new();

    public AutopilotDispatchFormTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _resolver = new LocalTrackerResolver(_factory, TimeProvider.System);
        _board = _resolver.For(ProjectId.Default);
        _delivery = new SqliteProjectDelivery(_factory);
        _runs = new SqliteAutopilotRunStore(_factory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public void Set(string reference, string plaintext) => _values[reference] = plaintext;

        public string? Get(string reference) => _values.TryGetValue(reference, out var v) ? v : null;

        public void Remove(string reference) => _values.Remove(reference);
    }

    /// <summary>A readiness answer the test fixes directly, standing in for the real git-backed one.</summary>
    private sealed class FixedReadiness(AutopilotDeliveryReadiness answer) : IAutopilotDeliveryReadiness
    {
        public Task<AutopilotDeliveryReadiness> CheckAsync(CancellationToken ct = default) => Task.FromResult(answer);
    }

    /// <summary>A clean workspace by default, standing in for the real git-backed one (#310).</summary>
    private sealed class FixedWorkspaceStatus(AutopilotWorkspaceState answer) : IAutopilotWorkspaceStatus
    {
        public Task<AutopilotWorkspaceState> CheckAsync(Fravashi.Domain.Projects.ProjectId project, CancellationToken ct = default) =>
            Task.FromResult(answer);
    }

    /// <summary>A fixed Settings cap, standing in for the real one (#307).</summary>
    private sealed class FixedConcurrencySettings(int cap) : IAutopilotConcurrencySettings
    {
        public int Cap() => cap;
    }

    private BunitContext Context(
        AutopilotDeliveryReadiness readiness, AutopilotWorkspaceState? workspace = null, int concurrencyCap = 2)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ICurrentProject>(SingleProject.Instance);
        ctx.Services.AddSingleton<IProjectDelivery>(_delivery);
        ctx.Services.AddSingleton<ISecretStore>(_secrets);
        ctx.Services.AddSingleton<IAutopilotConcurrencySettings>(new FixedConcurrencySettings(concurrencyCap));
        ctx.Services.AddSingleton(new AutopilotDispatcher(
            _resolver, _delivery, new FixedReadiness(readiness),
            new FixedWorkspaceStatus(workspace ?? new AutopilotWorkspaceState("main", 0, WorkspaceId: "w1")), _runs,
            TimeProvider.System, new FixedConcurrencySettings(concurrencyCap)));
        return ctx;
    }

    [Fact]
    public void Shows_the_shipped_defaults()
    {
        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"));
        var cut = ctx.Render<AutopilotDispatchForm>();

        Assert.Equal(AutopilotLimits.Default.MoneyPerItemUsd, decimal.Parse(cut.Find("[data-testid=autopilot-money-per-item]").GetAttribute("value")!));
        Assert.Equal(AutopilotDispatchSettings.Default.Builder.Model, cut.Find("[data-testid=autopilot-builder-model]").GetAttribute("value"));
        Assert.Equal(AutopilotDispatchSettings.Default.Operator.Model, cut.Find("[data-testid=autopilot-operator-model]").GetAttribute("value"));
        Assert.Equal(string.Empty, cut.Find("[data-testid=autopilot-judge-key-input]").GetAttribute("value"));
        Assert.Equal("password", cut.Find("[data-testid=autopilot-judge-key-input]").GetAttribute("type"));
    }

    [Fact]
    public void The_building_agent_model_starts_from_the_autopilot_builders_default()
    {
        // Work item #357: Settings > Agents > Default models and effort pre-fills the run's builder model; the person can
        // still change it for this run, and the operator is not governed by it.
        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"));
        var config = new FakeConfigurationStore();
        AgentDefaultsSetting.Write(config, AgentDefaults.None with
        {
            AutopilotBuilder = new AgentDefault("sonnet", Fravashi.Domain.AgentSessions.AgentEffort.High),
        });
        ctx.Services.AddSingleton<IConfigurationStore>(config);

        var cut = ctx.Render<AutopilotDispatchForm>();

        Assert.Equal("sonnet", cut.Find("[data-testid=autopilot-builder-model]").GetAttribute("value"));
        Assert.Equal(AutopilotDispatchSettings.Default.Operator.Model, cut.Find("[data-testid=autopilot-operator-model]").GetAttribute("value"));
    }

    [Fact]
    public void A_stored_key_is_never_echoed_into_the_input()
    {
        _secrets.Set(AutopilotJudgeSecret.Reference, "sk-super-secret");

        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"));
        var cut = ctx.Render<AutopilotDispatchForm>();

        Assert.Equal(string.Empty, cut.Find("[data-testid=autopilot-judge-key-input]").GetAttribute("value"));
        Assert.DoesNotContain("sk-super-secret", cut.Markup);
        Assert.Contains("A TypeSafe key is stored.", cut.Find("[data-testid=autopilot-judge-key-status]").TextContent);
    }

    [Fact]
    public async Task No_marked_ready_item_refuses_with_the_domains_own_sentence()
    {
        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"));
        var cut = ctx.Render<AutopilotDispatchForm>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-dispatch-submit]").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "No Ready item is marked for autopilot", cut.Find("[data-testid=autopilot-dispatch-refusal]").TextContent));
    }

    [Fact]
    public async Task A_missing_token_refuses_naming_the_host()
    {
        var created = await _board.CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Do the thing", Description = "The problem and the outcome.", Status = WorkItemStatus.Ready },
            MutationContext.User);
        await _board.UpdateAsync(
            created.Value.Id, new WorkItemPatch { MarkedForAutopilot = Optional<bool>.Of(true) }, created.Value.Version, MutationContext.User);

        using var ctx = Context(new AutopilotDeliveryReadiness(false, "github.com"));
        var cut = ctx.Render<AutopilotDispatchForm>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-dispatch-submit]").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "github.com", cut.Find("[data-testid=autopilot-dispatch-refusal]").TextContent));
    }

    /// <summary>
    /// Backlog 312: the branch note names what THIS run will actually do, for the cap it will actually run
    /// with (#311's own rule: commit to main is always 1, otherwise the Settings cap lowered by a valid
    /// override), never a sentence true only at a cap of 1.
    /// </summary>
    [Fact]
    public void Shows_the_branch_switch_sentence_at_a_cap_of_one()
    {
        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"), concurrencyCap: 1);
        var cut = ctx.Render<AutopilotDispatchForm>();

        var note = cut.Find("[data-testid=autopilot-dispatch-branch-note]").TextContent;
        Assert.Contains("switches this project's workspace", note);
        Assert.DoesNotContain("worktree", note);
    }

    [Fact]
    public void Shows_the_worktree_sentence_above_a_cap_of_one()
    {
        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"), concurrencyCap: 2);
        var cut = ctx.Render<AutopilotDispatchForm>();

        var note = cut.Find("[data-testid=autopilot-dispatch-branch-note]").TextContent;
        Assert.Contains("its own git worktree", note);
        Assert.Contains("pruned once that item ends", note);
        Assert.DoesNotContain("switches this project's workspace", note);
    }

    [Fact]
    public void The_worktree_sentence_follows_a_typed_override_live()
    {
        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"), concurrencyCap: 2);
        var cut = ctx.Render<AutopilotDispatchForm>();
        Assert.Contains("its own git worktree", cut.Find("[data-testid=autopilot-dispatch-branch-note]").TextContent);

        cut.Find("[data-testid=autopilot-concurrency-override]").Input("1");

        Assert.Contains("switches this project's workspace", cut.Find("[data-testid=autopilot-dispatch-branch-note]").TextContent);
    }

    [Fact]
    public void Commit_to_main_always_shows_the_cap_of_one_sentence_whatever_the_settings_cap()
    {
        _delivery.Set(ProjectId.Default, AutopilotDeliveryMode.CommitToMain);

        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"), concurrencyCap: 5);
        var cut = ctx.Render<AutopilotDispatchForm>();

        var note = cut.Find("[data-testid=autopilot-dispatch-branch-note]").TextContent;
        Assert.Contains("switches this project's workspace", note);
        Assert.DoesNotContain("worktree", note);
    }

    [Fact]
    public async Task A_dirty_workspace_refuses_with_the_domains_own_sentence()
    {
        var created = await _board.CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Do the thing", Description = "The problem and the outcome.", Status = WorkItemStatus.Ready },
            MutationContext.User);
        await _board.UpdateAsync(
            created.Value.Id, new WorkItemPatch { MarkedForAutopilot = Optional<bool>.Of(true) }, created.Value.Version, MutationContext.User);

        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"), new AutopilotWorkspaceState("main", 2, WorkspaceId: "w1"));
        var cut = ctx.Render<AutopilotDispatchForm>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-dispatch-submit]").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "2 uncommitted changes", cut.Find("[data-testid=autopilot-dispatch-refusal]").TextContent));
    }

    [Fact]
    public async Task A_valid_dispatch_shows_a_confirmation_naming_the_queued_count()
    {
        var created = await _board.CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Do the thing", Description = "The problem and the outcome.", Status = WorkItemStatus.Ready },
            MutationContext.User);
        await _board.UpdateAsync(
            created.Value.Id, new WorkItemPatch { MarkedForAutopilot = Optional<bool>.Of(true) }, created.Value.Version, MutationContext.User);

        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"));
        var cut = ctx.Render<AutopilotDispatchForm>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-dispatch-submit]").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "1 item(s) queued", cut.Find("[data-testid=autopilot-dispatch-confirmation]").TextContent));
    }

    /// <summary>
    /// #311: the form says why, in the item's own words, exactly when commit to main is the project's
    /// delivery, and it never shows the Settings cap as something that applies to this run: the cap note and
    /// the override field, which only make sense when a dispatch can actually choose a cap above 1, are gone.
    /// </summary>
    [Fact]
    public void Shows_the_commit_to_main_concurrency_note_and_hides_the_settings_cap_as_applying()
    {
        _delivery.Set(ProjectId.Default, AutopilotDeliveryMode.CommitToMain);

        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"));
        var cut = ctx.Render<AutopilotDispatchForm>();

        var note = cut.Find("[data-testid=autopilot-concurrency-commit-to-main-note]").TextContent;
        Assert.Contains("Commit to main runs one item at a time", note);
        Assert.Contains("does not apply to this project", note);
        Assert.Empty(cut.FindAll("[data-testid=autopilot-concurrency-cap-note]"));
        Assert.Empty(cut.FindAll("[data-testid=autopilot-concurrency-override]"));
    }

    [Fact]
    public void Does_not_show_the_commit_to_main_concurrency_note_under_branch_and_pull_request()
    {
        _delivery.Set(ProjectId.Default, AutopilotDeliveryMode.BranchAndPullRequest);

        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"));
        var cut = ctx.Render<AutopilotDispatchForm>();

        Assert.Empty(cut.FindAll("[data-testid=autopilot-concurrency-commit-to-main-note]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=autopilot-concurrency-cap-note]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=autopilot-concurrency-override]"));
    }

    [Fact]
    public void Shows_the_current_settings_cap()
    {
        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"), concurrencyCap: 3);
        var cut = ctx.Render<AutopilotDispatchForm>();

        Assert.Contains("3", cut.Find("[data-testid=autopilot-concurrency-cap-note]").TextContent);
    }

    [Fact]
    public async Task A_concurrency_override_not_lower_than_the_settings_cap_shows_the_domains_own_refusal()
    {
        var created = await _board.CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Do the thing", Description = "The problem and the outcome.", Status = WorkItemStatus.Ready },
            MutationContext.User);
        await _board.UpdateAsync(
            created.Value.Id, new WorkItemPatch { MarkedForAutopilot = Optional<bool>.Of(true) }, created.Value.Version, MutationContext.User);

        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"), concurrencyCap: 2);
        var cut = ctx.Render<AutopilotDispatchForm>();
        cut.Find("[data-testid=autopilot-concurrency-override]").Input("2");

        await cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-dispatch-submit]").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "never raise it", cut.Find("[data-testid=autopilot-dispatch-refusal]").TextContent));
    }

    [Fact]
    public async Task A_non_numeric_concurrency_override_is_refused_rather_than_silently_dropped()
    {
        var created = await _board.CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Do the thing", Description = "The problem and the outcome.", Status = WorkItemStatus.Ready },
            MutationContext.User);
        await _board.UpdateAsync(
            created.Value.Id, new WorkItemPatch { MarkedForAutopilot = Optional<bool>.Of(true) }, created.Value.Version, MutationContext.User);

        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"), concurrencyCap: 2);
        var cut = ctx.Render<AutopilotDispatchForm>();
        cut.Find("[data-testid=autopilot-concurrency-override]").Input("one");

        await cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-dispatch-submit]").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "is not a whole number", cut.Find("[data-testid=autopilot-dispatch-refusal]").TextContent));
        Assert.Empty(await _runs.OpenRunsAsync());
    }

    [Fact]
    public async Task A_lower_concurrency_override_dispatches_and_rides_on_the_run()
    {
        var created = await _board.CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Do the thing", Description = "The problem and the outcome.", Status = WorkItemStatus.Ready },
            MutationContext.User);
        await _board.UpdateAsync(
            created.Value.Id, new WorkItemPatch { MarkedForAutopilot = Optional<bool>.Of(true) }, created.Value.Version, MutationContext.User);

        using var ctx = Context(new AutopilotDeliveryReadiness(true, "github.com"), concurrencyCap: 2);
        var cut = ctx.Render<AutopilotDispatchForm>();
        cut.Find("[data-testid=autopilot-concurrency-override]").Input("1");

        await cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-dispatch-submit]").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "1 item(s) queued", cut.Find("[data-testid=autopilot-dispatch-confirmation]").TextContent));
        var saved = await _runs.OpenRunsAsync();
        Assert.Equal(1, saved.Single().Settings.ConcurrencyCapOverride);
    }
}
