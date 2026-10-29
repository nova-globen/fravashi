using AwesomeAssertions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.WorkItems;
using Xunit;
using static Fravashi.Domain.Tests.WorkItems.WorkItemFixture;

namespace Fravashi.Domain.Tests.Autopilot;

/// <summary>
/// #298's acceptance: "a dispatch with no token on branch + pull request is refused with words naming the
/// missing token", and the companion "no marked Ready items" refusal.
/// </summary>
public sealed class AutopilotDispatchValidationTests
{
    private static readonly WorkItem[] OneItem = [Restored(State(status: WorkItemStatus.Ready) with { MarkedForAutopilot = true })];

    [Fact]
    public void No_marked_ready_items_is_refused_naming_that()
    {
        var refusal = AutopilotDispatchValidation.Validate(
            [], AutopilotDeliveryMode.BranchAndPullRequest, hasGitHostToken: true, gitHost: "github.com");

        refusal.Should().Contain("No Ready item is marked for autopilot");
    }

    [Fact]
    public void Branch_and_pull_request_with_no_token_is_refused_naming_the_host()
    {
        var refusal = AutopilotDispatchValidation.Validate(
            OneItem, AutopilotDeliveryMode.BranchAndPullRequest, hasGitHostToken: false, gitHost: "github.com");

        refusal.Should().Contain("github.com");
        refusal.Should().Contain("Git hosts");
    }

    [Fact]
    public void Branch_and_pull_request_with_an_unresolved_origin_is_refused_without_a_host_name()
    {
        var refusal = AutopilotDispatchValidation.Validate(
            OneItem, AutopilotDeliveryMode.BranchAndPullRequest, hasGitHostToken: false, gitHost: null);

        refusal.Should().Contain("could not be resolved");
    }

    [Fact]
    public void Branch_and_pull_request_with_a_token_is_not_refused()
    {
        AutopilotDispatchValidation.Validate(
            OneItem, AutopilotDeliveryMode.BranchAndPullRequest, hasGitHostToken: true, gitHost: "github.com")
            .Should().BeNull();
    }

    [Fact]
    public void Commit_to_main_never_needs_a_token()
    {
        AutopilotDispatchValidation.Validate(
            OneItem, AutopilotDeliveryMode.CommitToMain, hasGitHostToken: false, gitHost: null)
            .Should().BeNull();
    }

    [Fact]
    public void A_dirty_workspace_is_refused_naming_the_count()
    {
        var refusal = AutopilotDispatchValidation.Validate(
            OneItem, AutopilotDeliveryMode.BranchAndPullRequest, hasGitHostToken: true, gitHost: "github.com",
            workspaceDirtyCount: 3);

        refusal.Should().Contain("3");
        refusal.Should().Contain("uncommitted");
    }

    [Fact]
    public void A_dirty_workspace_is_checked_before_the_no_marked_items_refusal()
    {
        var refusal = AutopilotDispatchValidation.Validate(
            [], AutopilotDeliveryMode.BranchAndPullRequest, hasGitHostToken: true, gitHost: "github.com",
            workspaceDirtyCount: 1);

        refusal.Should().NotContain("marked for autopilot");
        refusal.Should().Contain("1 uncommitted change");
    }

    [Fact]
    public void A_clean_workspace_is_not_refused_on_its_own()
    {
        AutopilotDispatchValidation.Validate(
            OneItem, AutopilotDeliveryMode.BranchAndPullRequest, hasGitHostToken: true, gitHost: "github.com",
            workspaceDirtyCount: 0)
            .Should().BeNull();
    }

    [Fact]
    public void A_concurrency_override_equal_to_the_settings_cap_is_refused_naming_that_it_cannot_raise_it()
    {
        var refusal = AutopilotDispatchValidation.Validate(
            OneItem, AutopilotDeliveryMode.BranchAndPullRequest, hasGitHostToken: true, gitHost: "github.com",
            concurrencyCapOverride: 2, concurrencyCapSetting: 2);

        refusal.Should().Contain("never raise it");
        refusal.Should().Contain("2");
    }

    [Fact]
    public void A_concurrency_override_above_the_settings_cap_is_refused()
    {
        var refusal = AutopilotDispatchValidation.Validate(
            OneItem, AutopilotDeliveryMode.BranchAndPullRequest, hasGitHostToken: true, gitHost: "github.com",
            concurrencyCapOverride: 5, concurrencyCapSetting: 2);

        refusal.Should().Contain("never raise it");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_concurrency_override_is_refused(int overrideValue)
    {
        AutopilotDispatchValidation.Validate(
            OneItem, AutopilotDeliveryMode.BranchAndPullRequest, hasGitHostToken: true, gitHost: "github.com",
            concurrencyCapOverride: overrideValue, concurrencyCapSetting: 2)
            .Should().NotBeNull();
    }

    [Fact]
    public void A_concurrency_override_lower_than_the_settings_cap_is_not_refused_on_its_own()
    {
        AutopilotDispatchValidation.Validate(
            OneItem, AutopilotDeliveryMode.BranchAndPullRequest, hasGitHostToken: true, gitHost: "github.com",
            concurrencyCapOverride: 1, concurrencyCapSetting: 2)
            .Should().BeNull();
    }

    [Fact]
    public void No_concurrency_override_is_not_refused_on_its_own()
    {
        AutopilotDispatchValidation.Validate(
            OneItem, AutopilotDeliveryMode.BranchAndPullRequest, hasGitHostToken: true, gitHost: "github.com",
            concurrencyCapOverride: null, concurrencyCapSetting: 2)
            .Should().BeNull();
    }
}
