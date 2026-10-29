using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Application.Onboarding;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Application.Tests.Mirroring;

/// <summary>
/// Backlog 48: asking the provider's WORKFLOW which of its states a move could mean. The narrowing itself
/// is driven end to end over the real stores elsewhere; what is here is the part that has no good home
/// there, which is what happens when the provider does not answer. A board is what asks this, and a drag
/// is meant to feel instant, so the waiting is as much a part of the behaviour as the answer.
/// </summary>
public sealed class ProviderMoveNamesTests
{
    private static readonly BoardStatusMapping Several = new("jira",
    [
        new StatusMappingEntry("Done", "Shipped"),
        new StatusMappingEntry("Done", "Closed"),
    ]);

    private static ReverseMapping Reverse() => ProviderStatusMap.From(Several).ReverseMap(WorkItemStatus.Done);

    [Fact]
    public async Task A_workflow_that_never_answers_falls_back_to_the_mapping_within_the_budget()
    {
        var silent = new Silent();

        var offer = await ProviderMoveNames.ResolveAsync(
            silent, "DD-1", Reverse(), WorkItemStatus.Done, default, TimeSpan.FromMilliseconds(120));

        offer.FromWorkflow.Should().BeFalse("nothing was learned from the workflow");
        offer.Candidates.Should().Equal("Shipped", "Closed");
        offer.Sentence.Should().Contain("did not answer within");
        silent.Cancelled.Should().BeTrue("the call was actually given up on, not left running");
    }

    [Fact]
    public async Task A_caller_who_walks_away_is_not_told_the_provider_was_slow()
    {
        // The caller's own cancellation is theirs. Swallowing it into the fallback would turn a person
        // navigating away into a sentence blaming the provider, which is a lie about whose fault it was.
        using var walkedAway = new CancellationTokenSource();
        walkedAway.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = async () => await ProviderMoveNames.ResolveAsync(
            new Silent(), "DD-1", Reverse(), WorkItemStatus.Done, walkedAway.Token, TimeSpan.FromMinutes(5));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void The_budget_is_short_enough_to_be_worth_having()
    {
        // The point of the budget is that it is much shorter than the HTTP client's own default of a
        // hundred seconds. A budget near that would be the defect it exists to prevent.
        ProviderMoveNames.WorkflowBudget.Should().BeGreaterThan(TimeSpan.FromSeconds(1))
            .And.BeLessThan(TimeSpan.FromSeconds(15));
    }

    /// <summary>A provider that accepts the call and then never answers, like a black-holed network.</summary>
    private sealed class Silent : IProviderGateway
    {
        public bool Cancelled { get; private set; }

        public string Connection => "Jira";

        public ProviderCapabilities Capabilities { get; } = new(true, true, true, true, true);

        public async Task<ProviderResult<IReadOnlyList<string>>> OfferedTransitionsAsync(string key, CancellationToken ct = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            return ProviderResult<IReadOnlyList<string>>.Ok([]);
        }

        public Task<ProviderResult<ProviderItem>> CreateAsync(string payload, string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ProviderResult<ProviderItem>> GetAsync(string key, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ProviderResult<ProviderItem?>> FindByOperationIdAsync(string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ProviderResult<ProviderItem>> UpdateAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ProviderResult<ProviderItem>> TransitionAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
