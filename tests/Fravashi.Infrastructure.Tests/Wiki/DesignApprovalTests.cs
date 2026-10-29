using AwesomeAssertions;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Wiki;

/// <summary>
/// Backlog 54, the design gate: a design that names no work cannot be approved, and the refusal says how
/// to fix that; an approved design becomes the work item's one approved-design reference, which is what
/// the verifier reads.
/// </summary>
public sealed class DesignApprovalTests : IDisposable
{
    private readonly SqliteTestContext _store = new();
    private readonly LocalTrackerResolver _trackers;
    private readonly ITracker _board;
    private readonly SqliteWikiLibrary _wiki;
    private readonly DesignApproval _approval;

    public DesignApprovalTests()
    {
        _trackers = new LocalTrackerResolver(_store.Factory, TimeProvider.System);
        _board = _trackers.For(ProjectId.Default);
        _wiki = new SqliteWikiLibrary(_store.Factory, TimeProvider.System, Fravashi.Domain.Projects.ProjectId.Default);
        _approval = new DesignApproval(_trackers, new OneWiki(_wiki));
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task A_design_that_names_no_work_is_refused_with_the_action_that_fixes_it()
    {
        var item = await ItemAsync("Redesign the welcome page");
        var design = await DesignAsync("A picture of something", body: "No idea what this is for.");

        var result = await _approval.ApproveAsync(ProjectId.Default, design.Number, item.Number);

        result.Approved.Should().BeFalse();
        result.Code.Should().Be(DesignApproval.UnlinkedCode);
        result.Message.Should().Contain("wiki_link").And.Contain("#" + item.Number);

        // And nothing was written: a refused approval leaves the item exactly as it was.
        (await _board.GetAsync(item.Id))!.RefFor(ArtifactRefRole.ApprovedDesign).Should().BeNull();
    }

    [Fact]
    public async Task A_design_that_names_the_work_in_its_prose_can_be_approved()
    {
        var item = await ItemAsync("Redesign the welcome page");
        var design = await DesignAsync("The new welcome page", body: $"This is the design for #{item.Number}.");

        (await _approval.ApproveAsync(ProjectId.Default, design.Number, item.Number)).Approved.Should().BeTrue();

        var reference = (await _board.GetAsync(item.Id))!.RefFor(ArtifactRefRole.ApprovedDesign);
        reference.Should().NotBeNull();
        reference!.Kind.Should().Be(ArtifactRefKind.Design);
        reference.Target.Should().Be(design.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        reference.Label.Should().Be("The new welcome page");
    }

    [Fact]
    public async Task A_design_that_names_the_work_in_a_typed_link_can_be_approved_too()
    {
        var item = await ItemAsync("Redesign the welcome page");
        var design = await DesignAsync(
            "The new welcome page",
            body: "How it should look.",
            links: [new WikiLink(WikiLinkKind.WorkItem, item.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))]);

        (await _approval.ApproveAsync(ProjectId.Default, design.Number, item.Number)).Approved.Should().BeTrue();
    }

    [Fact]
    public async Task An_item_has_one_approved_design_and_approving_again_replaces_it()
    {
        // A second would make "the approved design" a question rather than an answer.
        var item = await ItemAsync("Redesign the welcome page");
        var first = await DesignAsync("First try", body: $"For #{item.Number}.");
        var second = await DesignAsync("Second try", body: $"For #{item.Number}.");

        (await _approval.ApproveAsync(ProjectId.Default, first.Number, item.Number)).Approved.Should().BeTrue();
        (await _approval.ApproveAsync(ProjectId.Default, second.Number, item.Number)).Approved.Should().BeTrue();

        var reloaded = (await _board.GetAsync(item.Id))!;
        reloaded.Refs.Count(r => r.Role == ArtifactRefRole.ApprovedDesign).Should().Be(1);
        reloaded.RefFor(ArtifactRefRole.ApprovedDesign)!.Label.Should().Be("Second try");
    }

    [Fact]
    public async Task Approving_leaves_the_items_other_references_alone()
    {
        var item = await ItemAsync("Redesign the welcome page");
        var withPlan = await _board.UpdateAsync(
            item.Id,
            new WorkItemPatch
            {
                Refs = Optional<IReadOnlyList<ArtifactRef>>.Of(
                [
                    new ArtifactRef(string.Empty, ArtifactRefRole.Context, ArtifactRefKind.Url, "https://example/x"),
                ]),
            },
            item.Version,
            MutationContext.User);
        withPlan.Outcome.Should().Be(TrackerOutcome.Ok);

        var design = await DesignAsync("The design", body: $"For #{item.Number}.");
        (await _approval.ApproveAsync(ProjectId.Default, design.Number, item.Number)).Approved.Should().BeTrue();

        var reloaded = (await _board.GetAsync(item.Id))!;
        reloaded.Refs.Should().HaveCount(2);
        reloaded.RefFor(ArtifactRefRole.Context).Should().NotBeNull();
    }

    [Fact]
    public async Task The_approved_design_reads_back_as_the_document_itself()
    {
        // "is what the verifier reads": the reference resolves to the document, not to a number.
        var item = await ItemAsync("Redesign the welcome page");
        var design = await DesignAsync("The new welcome page", body: $"For #{item.Number}.");
        await _approval.ApproveAsync(ProjectId.Default, design.Number, item.Number);

        var read = await _approval.ApprovedFor((await _board.GetAsync(item.Id))!);

        read.Should().NotBeNull();
        read!.Number.Should().Be(design.Number);
        read.Title.Should().Be("The new welcome page");
    }

    [Fact]
    public async Task An_item_with_no_approved_design_reads_back_as_nothing()
    {
        var item = await ItemAsync("Nothing approved");

        (await _approval.ApprovedFor(item)).Should().BeNull();
    }

    // ---- backlog 209: the approved design is pinned to its version ------------------------------------

    [Fact]
    public async Task Approving_pins_the_designs_version_at_that_moment()
    {
        var item = await ItemAsync("Redesign the welcome page");
        var design = await DesignAsync("The new welcome page", body: $"For #{item.Number}.");

        (await _approval.ApproveAsync(ProjectId.Default, design.Number, item.Number)).Approved.Should().BeTrue();
        (await _board.GetAsync(item.Id))!.RefFor(ArtifactRefRole.ApprovedDesign)!.Version.Should().Be(1);

        // The document moves on; re-approving (the same call, run again) pins the new version.
        var edited = await _wiki.UpdateAsync(
            design.Id, new WikiItemEdit { Body = $"Nine changes later. For #{item.Number}." }, design.Version);
        edited.IsValid.Should().BeTrue();
        (await _approval.ApproveAsync(ProjectId.Default, design.Number, item.Number)).Approved.Should().BeTrue();

        (await _board.GetAsync(item.Id))!.RefFor(ArtifactRefRole.ApprovedDesign)!.Version.Should().Be(2);
    }

    [Fact]
    public async Task ApprovedFor_reads_the_pinned_version_rather_than_whatever_the_document_says_now()
    {
        var item = await ItemAsync("Redesign the welcome page");
        var design = await DesignAsync("The new welcome page", body: $"The approved shape. For #{item.Number}.");
        (await _approval.ApproveAsync(ProjectId.Default, design.Number, item.Number)).Approved.Should().BeTrue();

        // The host edits it after approval (backlog 209's exact scenario): nine changes, a deleted button,
        // a new state - represented here as a changed body.
        var edited = await _wiki.UpdateAsync(
            design.Id, new WikiItemEdit { Body = "A very different shape nobody approved." }, design.Version);
        edited.IsValid.Should().BeTrue();

        var read = await _approval.ApprovedFor((await _board.GetAsync(item.Id))!);

        read.Should().NotBeNull();
        read!.Version.Should().Be(1);
        read.Body.Should().Be($"The approved shape. For #{item.Number}.");
    }

    [Fact]
    public async Task A_reference_with_no_recorded_version_reads_as_the_current_document()
    {
        // Simulates a reference written before pinning existed: no Version at all.
        var item = await ItemAsync("Redesign the welcome page");
        var design = await DesignAsync("The new welcome page", body: $"Version one. For #{item.Number}.");
        await AttachUnpinnedReferenceAsync(item, design);

        var edited = await _wiki.UpdateAsync(design.Id, new WikiItemEdit { Body = "Version two." }, design.Version);
        edited.IsValid.Should().BeTrue();

        var read = await _approval.ApprovedFor((await _board.GetAsync(item.Id))!);

        read.Should().NotBeNull();
        read!.Body.Should().Be("Version two.", "with nothing pinned, this reads as it always did: the current document");
    }

    [Fact]
    public async Task StandingFor_says_whether_the_design_moved_on_since_it_was_approved()
    {
        var item = await ItemAsync("Redesign the welcome page");
        var design = await DesignAsync("The new welcome page", body: $"For #{item.Number}.");
        await _approval.ApproveAsync(ProjectId.Default, design.Number, item.Number);

        var fresh = await _approval.StandingFor((await _board.GetAsync(item.Id))!);
        fresh.Should().NotBeNull();
        fresh!.ApprovedVersion.Should().Be(1);
        fresh.CurrentVersion.Should().Be(1);
        fresh.ChangedSinceApproval.Should().BeFalse();

        var edited = await _wiki.UpdateAsync(design.Id, new WikiItemEdit { Body = "Nine changes later." }, design.Version);
        edited.IsValid.Should().BeTrue();

        var moved = await _approval.StandingFor((await _board.GetAsync(item.Id))!);
        moved.Should().NotBeNull();
        moved!.ApprovedVersion.Should().Be(1);
        moved.CurrentVersion.Should().Be(2);
        moved.ChangedSinceApproval.Should().BeTrue();
    }

    [Fact]
    public async Task StandingFor_reports_nothing_to_compare_for_a_reference_that_predates_pinning()
    {
        var item = await ItemAsync("Redesign the welcome page");
        var design = await DesignAsync("The new welcome page", body: $"For #{item.Number}.");
        await AttachUnpinnedReferenceAsync(item, design);

        var standing = await _approval.StandingFor((await _board.GetAsync(item.Id))!);

        standing.Should().NotBeNull();
        standing!.ApprovedVersion.Should().BeNull();
        standing.ChangedSinceApproval.Should().BeFalse();
    }

    [Fact]
    public async Task StandingFor_is_null_with_no_approved_design()
    {
        var item = await ItemAsync("Nothing approved");

        (await _approval.StandingFor(item)).Should().BeNull();
    }

    /// <summary>Writes an approved-design reference the way one looked before backlog 209: no version.</summary>
    private async Task AttachUnpinnedReferenceAsync(WorkItem item, WikiItem design)
    {
        var updated = await _board.UpdateAsync(
            item.Id,
            new WorkItemPatch
            {
                Refs = Optional<IReadOnlyList<ArtifactRef>>.Of(
                [
                    new ArtifactRef(
                        string.Empty,
                        ArtifactRefRole.ApprovedDesign,
                        ArtifactRefKind.Design,
                        design.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Label: design.Title),
                ]),
            },
            item.Version,
            MutationContext.User);

        updated.Outcome.Should().Be(TrackerOutcome.Ok);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_number_nothing_answers_to_is_refused_by_name(bool designMissing)
    {
        var item = await ItemAsync("The work");
        var design = await DesignAsync("The design", body: $"For #{item.Number}.");

        var result = designMissing
            ? await _approval.ApproveAsync(ProjectId.Default, 9999, item.Number)
            : await _approval.ApproveAsync(ProjectId.Default, design.Number, 9999);

        result.Approved.Should().BeFalse();
        result.Code.Should().Be(designMissing ? DesignApproval.NotFoundCode : DesignApproval.ItemNotFoundCode);
    }

    private async Task<WorkItem> ItemAsync(string title)
    {
        var created = await _board.CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = title, Description = "Why it exists." },
            MutationContext.User);
        created.Outcome.Should().Be(TrackerOutcome.Ok);
        return created.Value;
    }

    private async Task<WikiItem> DesignAsync(string title, string body, IReadOnlyList<WikiLink>? links = null)
    {
        var created = await _wiki.CreateAsync(new NewWikiItem
        {
            Type = WikiItemType.Draft,
            Title = title,
            Body = body,
            Links = links ?? [],
        });

        created.IsValid.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));
        return created.Value;
    }
}
