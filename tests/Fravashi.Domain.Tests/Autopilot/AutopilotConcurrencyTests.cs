using AwesomeAssertions;
using Fravashi.Domain.Autopilot;
using Xunit;

namespace Fravashi.Domain.Tests.Autopilot;

/// <summary>
/// ADR 0031 decision 16, #307: the concurrency cap, counted per REPOSITORY (a workspace and every worktree
/// made from it, #306), the memory floor, and the single place a Settings cap and a dispatch override combine.
/// </summary>
public sealed class AutopilotConcurrencyTests
{
    [Fact]
    public void A_worktree_sibling_path_resolves_to_its_repositorys_own_root()
    {
        AutopilotRepositoryIdentity.For(@"C:\repos\myrepo.fravashi-worktrees\307")
            .Should().Be(@"C:/repos/myrepo");
    }

    [Fact]
    public void A_worktree_sibling_path_with_forward_slashes_resolves_the_same_way()
    {
        AutopilotRepositoryIdentity.For("/repos/myrepo.fravashi-worktrees/307")
            .Should().Be("/repos/myrepo");
    }

    [Fact]
    public void A_worktree_made_before_the_rename_is_still_its_repository_s()
    {
        // The bridge release: the former sibling folder suffix still groups a worktree with its repository.
        AutopilotRepositoryIdentity.For("/repos/myrepo" + Fravashi.Domain.FormerNames.WorktreeContainerSuffix + "/307")
            .Should().Be("/repos/myrepo");
        AutopilotRepositoryIdentity.IsWorktree("/repos/myrepo" + Fravashi.Domain.FormerNames.WorktreeContainerSuffix + "/307")
            .Should().BeTrue();
    }

    [Fact]
    public void An_ordinary_workspace_path_is_its_own_identity()
    {
        AutopilotRepositoryIdentity.For(@"C:\repos\myrepo").Should().Be(@"C:/repos/myrepo");
    }

    [Fact]
    public void A_different_repositorys_worktree_resolves_to_a_different_identity()
    {
        var one = AutopilotRepositoryIdentity.For(@"C:\repos\myrepo.fravashi-worktrees\307");
        var other = AutopilotRepositoryIdentity.For(@"C:\repos\otherrepo.fravashi-worktrees\307");

        one.Should().NotBe(other);
    }

    [Fact]
    public void A_worktree_and_its_own_repository_resolve_to_the_same_identity()
    {
        var repository = AutopilotRepositoryIdentity.For(@"C:\repos\myrepo");
        var worktree = AutopilotRepositoryIdentity.For(@"C:\repos\myrepo.fravashi-worktrees\307");

        worktree.Should().Be(repository);
    }

    [Fact]
    public void A_folder_that_merely_ends_with_the_suffix_at_the_root_is_its_own_identity()
    {
        // No grandparent to climb to: the suffix alone does not manufacture a repository out of nothing.
        AutopilotRepositoryIdentity.For(".fravashi-worktrees/307").Should().Be(".fravashi-worktrees/307");
    }

    // ---- backlog 312: telling a worktree apart from its own repository folder --------------------------------

    [Fact]
    public void A_worktree_sibling_path_IsWorktree()
    {
        AutopilotRepositoryIdentity.IsWorktree(@"C:\repos\myrepo.fravashi-worktrees\307").Should().BeTrue();
    }

    [Fact]
    public void An_ordinary_workspace_path_is_not_a_worktree()
    {
        AutopilotRepositoryIdentity.IsWorktree(@"C:\repos\myrepo").Should().BeFalse();
    }

    [Fact]
    public void A_suffix_at_the_root_with_no_grandparent_is_not_a_worktree()
    {
        AutopilotRepositoryIdentity.IsWorktree(".fravashi-worktrees/307").Should().BeFalse();
    }

    [Fact]
    public void Normalize_turns_backslashes_to_forward_slashes_and_trims_a_trailing_one()
    {
        AutopilotRepositoryIdentity.Normalize(@"C:\repos\myrepo\").Should().Be("C:/repos/myrepo");
    }

    [Fact]
    public void Below_the_cap_with_no_memory_reading_is_not_refused()
    {
        AutopilotConcurrencyGuard.Refuse(running: 0, cap: 2, availableBytes: null, floorBytes: 1024).Should().BeNull();
    }

    [Fact]
    public void Below_the_cap_with_memory_above_the_floor_is_not_refused()
    {
        AutopilotConcurrencyGuard.Refuse(running: 1, cap: 2, availableBytes: 2048, floorBytes: 1024).Should().BeNull();
    }

    [Fact]
    public void At_the_cap_is_refused_naming_the_cap_and_the_count()
    {
        var refusal = AutopilotConcurrencyGuard.Refuse(running: 2, cap: 2, availableBytes: null, floorBytes: 1024);

        refusal.Should().NotBeNull();
        refusal!.Kind.Should().Be(AutopilotConcurrencyRefusalKind.Cap);
        refusal.Message.Should().Contain("2 of 2 session");
        refusal.Message.Should().Contain("concurrency cap is reached");
    }

    [Fact]
    public void Past_the_cap_is_refused_the_same_way_as_at_it()
    {
        AutopilotConcurrencyGuard.Refuse(running: 3, cap: 2, availableBytes: null, floorBytes: 1024)
            .Should().NotBeNull();
    }

    [Fact]
    public void Below_the_floor_is_refused_naming_the_free_and_required_megabytes()
    {
        var refusal = AutopilotConcurrencyGuard.Refuse(
            running: 0, cap: 2, availableBytes: 512 * 1024 * 1024, floorBytes: 1024L * 1024 * 1024);

        refusal.Should().NotBeNull();
        refusal!.Kind.Should().Be(AutopilotConcurrencyRefusalKind.Memory);
        refusal.Message.Should().Contain("512 MB free");
        refusal.Message.Should().Contain("1024 MB required");
    }

    [Fact]
    public void The_cap_is_checked_before_memory_when_both_would_fire()
    {
        var refusal = AutopilotConcurrencyGuard.Refuse(
            running: 2, cap: 2, availableBytes: 0, floorBytes: 1024L * 1024 * 1024);

        refusal!.Kind.Should().Be(AutopilotConcurrencyRefusalKind.Cap);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_cap_is_treated_as_the_shipped_default(int cap)
    {
        var refusal = AutopilotConcurrencyGuard.Refuse(
            running: AutopilotConcurrency.DefaultCap, cap: cap, availableBytes: null, floorBytes: 1024);

        refusal!.Cap.Should().Be(AutopilotConcurrency.DefaultCap);
    }

    [Fact]
    public void Effective_uses_the_settings_cap_with_no_override()
    {
        AutopilotConcurrency.Effective(settingsCap: 2, dispatchOverride: null).Should().Be(2);
    }

    [Fact]
    public void Effective_uses_a_lower_override()
    {
        AutopilotConcurrency.Effective(settingsCap: 2, dispatchOverride: 1).Should().Be(1);
    }

    [Fact]
    public void Effective_ignores_an_override_equal_to_the_settings_cap()
    {
        AutopilotConcurrency.Effective(settingsCap: 2, dispatchOverride: 2).Should().Be(2);
    }

    [Fact]
    public void Effective_ignores_an_override_above_the_settings_cap()
    {
        AutopilotConcurrency.Effective(settingsCap: 2, dispatchOverride: 5).Should().Be(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Effective_ignores_a_non_positive_override(int dispatchOverride)
    {
        AutopilotConcurrency.Effective(settingsCap: 2, dispatchOverride).Should().Be(2);
    }
}
