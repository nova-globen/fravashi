using AwesomeAssertions;
using Fravashi.Domain.Wiki;
using Xunit;

namespace Fravashi.Domain.Tests.Wiki;

/// <summary>
/// The rule a publish follows (backlog 83), held directly. The publisher's own tests prove the machinery
/// obeys it against a misbehaving site; these say what the rule IS, so an edit to it fails here first.
/// </summary>
public sealed class WikiPublishDisciplineTests
{
    private static WikiItem Item(long version = 3) => WikiItem.Create(new WikiItemState
    {
        Id = "doc", Number = 1, Type = WikiItemType.Plan, Title = "Rollout", Slug = "rollout", Version = version,
    }).Value;

    private static WikiPublication Row(WikiPublicationState state, string? pageId = "555", long? pushed = 3) => new(
        "doc", "Confluence", "TEAM", pageId, pageId is null ? null : 1, pushed, state, "op", "Rollout",
        null, null, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_document_never_published_is_created() =>
        WikiPublishDiscipline.Next(null, Item()).Should().Be(WikiPublishStep.Create);

    [Fact]
    public void A_published_document_that_has_not_moved_sends_nothing() =>
        WikiPublishDiscipline.Next(Row(WikiPublicationState.Published), Item()).Should().Be(WikiPublishStep.NothingToSend);

    [Fact]
    public void A_published_document_that_moved_here_is_an_update() =>
        WikiPublishDiscipline.Next(Row(WikiPublicationState.Published), Item(version: 4)).Should().Be(WikiPublishStep.Update);

    [Fact]
    public void A_write_in_flight_is_settled_before_anything_is_sent() =>
        WikiPublishDiscipline.Next(Row(WikiPublicationState.InFlight, pageId: null), Item()).Should().Be(WikiPublishStep.SettleFirst);

    [Theory]
    [InlineData(WikiPublicationState.Unresolved)]
    [InlineData(WikiPublicationState.SiteChanged)]
    [InlineData(WikiPublicationState.Conflict)]
    [InlineData(WikiPublicationState.Gone)]
    public void A_question_that_is_a_persons_is_never_answered_by_publishing(WikiPublicationState state) =>
        WikiPublishDiscipline.Next(Row(state), Item(version: 9)).Should().Be(WikiPublishStep.NeedsAPerson);

    [Theory]
    [InlineData(WikiPublicationState.NotSent)]
    [InlineData(WikiPublicationState.Refused)]
    public void What_never_happened_on_the_site_may_be_sent_and_the_write_depends_on_whether_a_page_is_known(
        WikiPublicationState state)
    {
        WikiPublishDiscipline.Next(Row(state, pageId: null), Item()).Should().Be(WikiPublishStep.Create);
        WikiPublishDiscipline.Next(Row(state), Item()).Should().Be(WikiPublishStep.Update);
    }

    [Fact]
    public void Every_state_has_a_sentence_and_no_two_states_share_one()
    {
        var sentences = Enum.GetValues<WikiPublicationState>()
            .Select(state => WikiPublishDiscipline.Sentence(Row(state), Item()))
            .ToList();

        sentences.Should().OnlyContain(s => s.Length > 0 && !s.StartsWith("Unknown", StringComparison.Ordinal));
        sentences.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_state_has_a_stable_stored_name()
    {
        foreach (var state in Enum.GetValues<WikiPublicationState>())
        {
            WikiNames.Parse<WikiPublicationState>(WikiNames.ToName(state)).Should().Be(state);
        }
    }
}
