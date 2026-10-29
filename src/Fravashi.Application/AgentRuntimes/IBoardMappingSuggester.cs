using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.AgentRuntimes;

/// <summary>A request to have a configured runtime suggest a board-status mapping (S16).</summary>
/// <param name="Tool">The authenticated runtime to run headlessly.</param>
/// <param name="BoardStates">The user's real board status names, fetched from their verified connection.</param>
/// <remarks>
/// It names no folder: the prompt is self-contained and the CLI runs with no tools, so there is nothing a working
/// directory could give it. Before 2026-09-30 it carried one, filled from the dev fallback's <c>Repository:Root</c>,
/// and a folder that no longer existed was what made every suggestion fail in the installed product.
/// </remarks>
public sealed record BoardMappingSuggestionRequest(
    AgentTool Tool,
    IReadOnlyList<string> BoardStates);

/// <summary>
/// The parsed result of an AI mapping suggestion. <see cref="Assignments"/> maps each canonical status name to
/// the board states the runtime assigned to it. On any failure (CLI error, timeout, unparseable output),
/// <see cref="Success"/> is false and <see cref="Error"/> explains - the UI then keeps the name-based prefill
/// and never silently trusts a malformed suggestion.
/// </summary>
/// <param name="Success">True when the runtime returned strict, parseable JSON.</param>
/// <param name="Assignments">Canonical-status-name → assigned board states (only present on success).</param>
/// <param name="Error">A human-readable reason when unsuccessful.</param>
/// <param name="RawOutput">The raw CLI output, for a diagnostic disclosure in the UI.</param>
public sealed record BoardMappingSuggestion(
    bool Success,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Assignments,
    string? Error,
    string? RawOutput)
{
    public static BoardMappingSuggestion Failed(string error, string? raw = null) =>
        new(false, new Dictionary<string, IReadOnlyList<string>>(), error, raw);
}

/// <summary>
/// Runs a configured + authenticated coding-agent runtime headlessly to suggest how the user's real board
/// statuses map onto the canonical seven (S16). It requires strict JSON out and parses defensively; it never
/// writes the mapping itself - it returns a suggestion the user confirms in the wizard.
/// </summary>
public interface IBoardMappingSuggester
{
    Task<BoardMappingSuggestion> SuggestAsync(BoardMappingSuggestionRequest request, CancellationToken cancellationToken = default);
}
