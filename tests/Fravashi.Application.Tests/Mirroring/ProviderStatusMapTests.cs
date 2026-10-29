using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Application.Onboarding;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Application.Tests.Mirroring;

/// <summary>
/// Backlog 47: "an unknown native status degrades to its category rather than breaking", and "per-board
/// mapping is configuration, not code". Onboarding has collected that mapping since item 37 and nothing
/// consumed it; this is what reads it.
/// </summary>
public sealed class ProviderStatusMapTests
{
    private static readonly BoardStatusMapping Configured = new("jira",
    [
        new StatusMappingEntry("In Review", "Awaiting sign-off"),
        new StatusMappingEntry("Done", "Shipped"),
    ]);

    [Fact]
    public void Several_names_the_mapping_step_saved_in_one_entry_are_each_a_name()
    {
        // Moving this repository's project to Azure DevOps, 2026-09-24: the step's "several states" mode saved
        // "Done; Closed; Completed" in one entry, and that whole string was sent as the state to move to.
        var map = ProviderStatusMap.From(new BoardStatusMapping("AzureDevOps",
        [
            new StatusMappingEntry("Done", "Done; Closed; Completed"),
            new StatusMappingEntry("Cancelled", "Removed"),
        ]));

        map.ReverseMap(WorkItemStatus.Done).Candidates.Should().Equal("Done", "Closed", "Completed");
        map.ReverseMap(WorkItemStatus.Cancelled).Settled.Should().Be("Removed");
        map.Translate("Closed", null, WorkItemStatus.Backlog).Status.Should().Be(WorkItemStatus.Done);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_name_listed_under_two_statuses_is_a_question_whichever_entry_came_last(bool reversed)
    {
        // Backlog 181. It used to read as whichever entry came last, so the order of two rows decided whether
        // somebody's work read as finished or abandoned. "Closed" is also a catalog name for Done, which is exactly
        // the guess that must not be fallen back on.
        StatusMappingEntry[] entries =
        [
            new("Done", "Shipped; Closed"),
            new("Cancelled", "closed"),
        ];
        var map = ProviderStatusMap.From(new BoardStatusMapping("jira", reversed ? [entries[1], entries[0]] : entries));

        var read = map.Translate("Closed", "done", WorkItemStatus.InProgress);

        read.Status.Should().BeNull("nothing is picked");
        read.How.Should().Be(StatusReading.Ambiguous);
        read.Said.Should().Contain("'Closed'").And.Contain("Done").And.Contain("Cancelled").And.Contain("Board and status mapping");
        map.Translate("Shipped", null, WorkItemStatus.InProgress).Status.Should().Be(
            WorkItemStatus.Done, "the other names in the same entry are unaffected");
    }

    [Fact]
    public void The_same_name_twice_under_one_status_is_one_name_and_not_a_question() =>
        ProviderStatusMap.From(new BoardStatusMapping("jira",
            [
                new StatusMappingEntry("Done", "Shipped"),
                new StatusMappingEntry("Done", "shipped; Closed"),
            ]))
            .Translate("Shipped", null, WorkItemStatus.Backlog).How.Should().Be(StatusReading.Configured);

    [Fact]
    public void Duplicates_name_each_state_listed_under_more_than_one_status()
    {
        var mapping = new BoardStatusMapping("AzureDevOps",
        [
            new StatusMappingEntry("In Review", "Resolved"),
            new StatusMappingEntry("Done", "Done; Closed; resolved"),
            new StatusMappingEntry("Done", "Closed"),
            new StatusMappingEntry("Cancelled", "Removed; CLOSED"),
        ]);

        var duplicates = mapping.Duplicates();

        duplicates.Select(d => d.NativeStatus).Should().Equal("Resolved", "Closed");
        duplicates[0].CanonicalStatuses.Should().Equal("In Review", "Done");
        duplicates[1].CanonicalStatuses.Should().Equal("Done", "Cancelled");
        duplicates[1].Sentence.Should().Be("'Closed' is listed under Done and Cancelled.");
        BoardStatusMapping.Default("jira").Duplicates().Should().BeEmpty();
    }

    [Fact]
    public void A_configured_native_status_is_what_the_person_said_it_is()
    {
        var map = ProviderStatusMap.From(Configured);

        var read = map.Translate("Awaiting sign-off", null, WorkItemStatus.Backlog);

        read.Status.Should().Be(WorkItemStatus.InReview);
        read.How.Should().Be(StatusReading.Configured);
    }

    [Theory]
    [InlineData("awaiting sign-off")]
    [InlineData("  Awaiting Sign-Off  ")]
    public void The_match_ignores_case_and_surrounding_space(string native) =>
        ProviderStatusMap.From(Configured).Translate(native, null, WorkItemStatus.Backlog)
            .Status.Should().Be(WorkItemStatus.InReview);

    [Fact]
    public void A_status_nobody_configured_but_the_catalog_names_is_still_understood()
    {
        // The catalog ships the names providers actually use ("Selected for Dev", "Won't Do"), so an install
        // that mapped two statuses is not blind to the rest.
        var map = ProviderStatusMap.From(Configured);

        map.Translate("Selected for Dev", null, WorkItemStatus.Triage).Status.Should().Be(WorkItemStatus.Ready);
        map.Translate("Won't Do", null, WorkItemStatus.Triage).Status.Should().Be(WorkItemStatus.Cancelled);
    }

    [Fact]
    public void What_the_person_configured_beats_the_catalog()
    {
        // The catalog would read "Done" as done; this board says its "Done" means something else.
        var map = ProviderStatusMap.From(new BoardStatusMapping("jira", [new StatusMappingEntry("In Review", "Done")]));

        map.Translate("Done", null, WorkItemStatus.Backlog).Status.Should().Be(WorkItemStatus.InReview);
    }

    // ---- the degrade ------------------------------------------------------------------------------------

    [Theory]
    // Each case starts the item in a DIFFERENT category from the one the provider names, so the degrade has
    // to move it and the representative status is what is being asserted.
    [InlineData("new", WorkItemStatus.InProgress, WorkItemStatus.Backlog)]
    [InlineData("To Do", WorkItemStatus.Done, WorkItemStatus.Backlog)]
    [InlineData("indeterminate", WorkItemStatus.Backlog, WorkItemStatus.InProgress)]
    [InlineData("In Progress", WorkItemStatus.Triage, WorkItemStatus.InProgress)]
    [InlineData("done", WorkItemStatus.InProgress, WorkItemStatus.Done)]
    [InlineData("Completed", WorkItemStatus.Ready, WorkItemStatus.Done)]
    [InlineData("Removed", WorkItemStatus.Backlog, WorkItemStatus.Cancelled)]
    public void An_unknown_status_falls_back_to_the_category_the_provider_gave_it(
        string category, WorkItemStatus current, WorkItemStatus expected)
    {
        var read = ProviderStatusMap.From(Configured).Translate("Bespoke Workflow State", category, current);

        read.Status.Should().Be(expected);
        read.How.Should().Be(StatusReading.Category);
        read.Said.Should().Contain("Bespoke Workflow State");
    }

    [Theory]
    [InlineData("new", WorkItemStatus.Triage)]
    [InlineData("To Do", WorkItemStatus.Ready)]
    [InlineData("indeterminate", WorkItemStatus.InReview)]
    public void A_category_that_agrees_with_where_the_item_already_is_moves_nothing(string category, WorkItemStatus current) =>
        // Triage, Backlog and Ready are all "proposed"; a provider saying "to do" has contradicted none of
        // them, and picking one would throw away a distinction nobody questioned.
        ProviderStatusMap.From(Configured).Translate("Bespoke", category, current).Status.Should().Be(current);

    [Fact]
    public void A_degrade_that_would_not_change_the_category_leaves_the_item_where_it_is()
    {
        // The provider says "somewhere in progress" and the item is already In Review, which IS in progress.
        // Moving it to In Progress would lose a distinction the provider never contradicted.
        var read = ProviderStatusMap.From(Configured).Translate("Bespoke", "indeterminate", WorkItemStatus.InReview);

        read.Status.Should().Be(WorkItemStatus.InReview);
        read.How.Should().Be(StatusReading.Category);
    }

    [Fact]
    public void An_unknown_status_with_no_category_at_all_moves_nothing_and_says_so()
    {
        // Breaking is not allowed and guessing is worse: the item stays and the pass reports it.
        var read = ProviderStatusMap.From(Configured).Translate("Bespoke", null, WorkItemStatus.Ready);

        read.Status.Should().BeNull();
        read.How.Should().Be(StatusReading.Unknown);
        read.Said.Should().Contain("Bespoke");
    }

    [Fact]
    public void A_provider_that_says_nothing_about_status_is_not_an_unknown_status()
    {
        var read = ProviderStatusMap.From(Configured).Translate(null, null, WorkItemStatus.Ready);

        read.Status.Should().BeNull();
        read.How.Should().Be(StatusReading.NotSaid);
        read.Said.Should().BeNull("there is nothing to report about a provider with no opinion");
    }

    [Fact]
    public void With_no_mapping_configured_at_all_the_catalog_and_the_category_still_work()
    {
        var map = ProviderStatusMap.From(null);

        map.Translate("Testing", null, WorkItemStatus.Backlog).Status.Should().Be(WorkItemStatus.InReview);
        map.Translate("Whatever", "done", WorkItemStatus.Backlog).Status.Should().Be(WorkItemStatus.Done);
    }

    // ---- the other direction, for what is sent -----------------------------------------------------------

    [Fact]
    public void A_canonical_status_becomes_the_native_name_the_person_configured()
    {
        var map = ProviderStatusMap.From(Configured);

        map.ToNative(WorkItemStatus.InReview).Should().Be("Awaiting sign-off");
        map.ToNative(WorkItemStatus.Done).Should().Be("Shipped");
    }

    [Fact]
    public void A_canonical_status_nobody_mapped_names_no_native_status_at_all()
    {
        // Backlog 48: it used to answer the catalog's first example, which is a guess at what somebody's board
        // calls a state. Guessing here is how an item lands in a state that means something else there.
        ProviderStatusMap.From(Configured).ToNative(WorkItemStatus.InProgress).Should().BeNull();
    }

    // ---- backlog 48: the reverse map is one to many, so it is asked rather than assumed ------------------

    private static readonly BoardStatusMapping Ambiguous = new("jira",
    [
        new StatusMappingEntry("Done", "Shipped"),
        new StatusMappingEntry("Done", "Closed"),
        new StatusMappingEntry("Done", "Released"),
        new StatusMappingEntry("In Progress", "Active"),
    ]);

    [Fact]
    public void One_native_name_for_a_status_is_not_a_question()
    {
        var reverse = ProviderStatusMap.From(Ambiguous).ReverseMap(WorkItemStatus.InProgress);

        reverse.How.Should().Be(ReverseReading.One);
        reverse.Settled.Should().Be("Active");
        reverse.Candidates.Should().Equal("Active");
    }

    [Fact]
    public void Several_native_names_for_a_status_offer_all_of_them_and_settle_on_none()
    {
        var reverse = ProviderStatusMap.From(Ambiguous).ReverseMap(WorkItemStatus.Done);

        reverse.How.Should().Be(ReverseReading.Several);
        reverse.Candidates.Should().Equal("Shipped", "Closed", "Released");
        reverse.Settled.Should().BeNull("picking one of three would be picking for the person");
    }

    [Fact]
    public void The_same_native_name_mapped_twice_is_one_name_and_not_a_question()
    {
        var twice = ProviderStatusMap.From(new BoardStatusMapping("jira",
        [
            new StatusMappingEntry("Done", "Closed"),
            new StatusMappingEntry("Done", "closed"),
        ]));

        var reverse = twice.ReverseMap(WorkItemStatus.Done);

        reverse.How.Should().Be(ReverseReading.One);
        reverse.Settled.Should().Be("Closed", "the first spelling is the one the person wrote");
    }

    [Fact]
    public void A_status_nobody_mapped_offers_the_catalogs_examples_and_settles_on_none()
    {
        var reverse = ProviderStatusMap.From(Ambiguous).ReverseMap(WorkItemStatus.InReview);

        reverse.How.Should().Be(ReverseReading.Unmapped);
        reverse.Candidates.Should().Equal("Resolved", "In Review", "Testing", "QA");
        reverse.Settled.Should().BeNull("these are names this product suggests, not names that board uses");
    }

    [Fact]
    public void Every_canonical_status_can_be_asked_about_and_none_of_them_throws()
    {
        var map = ProviderStatusMap.From(null);

        foreach (var status in WorkItemStatuses.All)
        {
            var reverse = map.ReverseMap(status);
            reverse.How.Should().Be(ReverseReading.Unmapped);
            reverse.Candidates.Should().NotBeEmpty($"{status} has examples in the catalog to offer");
        }
    }
}
