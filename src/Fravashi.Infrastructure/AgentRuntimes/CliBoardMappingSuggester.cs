using System.Text;
using System.Text.Json;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Onboarding;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>
/// Suggests a board-status mapping (S16) by running a configured, authenticated runtime headlessly with a
/// self-contained prompt that lists the user's real board states and the canonical seven statuses / four
/// categories, and demands strict JSON out. Output is parsed defensively - unknown keys and states are dropped,
/// and any failure returns <see cref="BoardMappingSuggestion.Failed"/> so the wizard falls back to name-matching
/// rather than trusting a malformed answer.
/// <para>
/// The call is shaped like <see cref="Fravashi.Infrastructure.Wiki.CliWikiSummaryWriter"/>'s, for the same reason: the board's state names
/// come from the provider, so they are text somebody else wrote. The CLI gets no tools, the prompt rides on STDIN,
/// and no working directory is named. Before 2026-09-30 it ran with its tools on, inside the dev fallback's
/// <c>Repository:Root</c>, and in the installed product that setting named a folder the plan 2c retirement had
/// removed, so the process could not start there and every suggestion answered "is not installed" beside a
/// runtime the same page showed installed and signed in.
/// </para>
/// </summary>
public sealed class CliBoardMappingSuggester : IBoardMappingSuggester
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly IAgentCommandRunner _runner;
    private readonly IAgentRuntimeCredentialStore _credentials;

    public CliBoardMappingSuggester(IAgentCommandRunner runner, IAgentRuntimeCredentialStore credentials)
    {
        _runner = runner;
        _credentials = credentials;
    }

    public async Task<BoardMappingSuggestion> SuggestAsync(BoardMappingSuggestionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BoardStates.Count == 0)
        {
            return BoardMappingSuggestion.Failed("There are no board states to map.");
        }

        var prompt = BuildPrompt(request.BoardStates);
        var descriptor = AgentRuntimeCatalog.For(request.Tool);
        var command = BuildCommand(request.Tool, descriptor, prompt);

        var result = await _runner.RunAsync(command, cancellationToken);
        if (!result.Started)
        {
            // A start can fail for more than a missing binary, so the reason is said rather than assumed.
            var why = FirstNonEmpty(result.StandardError) ?? "the process did not start";
            return BoardMappingSuggestion.Failed($"{descriptor.DisplayName} could not be started here ({why}).");
        }

        if (result.TimedOut)
        {
            return BoardMappingSuggestion.Failed($"{descriptor.DisplayName} did not respond in time.", result.StandardOutput);
        }

        if (!result.Success)
        {
            var reason = FirstNonEmpty(result.StandardError, result.StandardOutput) ?? $"exit code {result.ExitCode}";
            return BoardMappingSuggestion.Failed($"{descriptor.DisplayName} could not produce a suggestion ({reason}).", result.StandardOutput);
        }

        return Parse(result.StandardOutput, request.BoardStates);
    }

    /// <summary>
    /// Parses the runtime's output into an assignment map. Public so the JSON-handling is unit-tested directly.
    /// Only canonical status names are accepted as keys; only real board states (matched case-insensitively) are
    /// accepted as values.
    /// </summary>
    public static BoardMappingSuggestion Parse(string output, IReadOnlyList<string> boardStates)
    {
        var json = ExtractJsonObject(output);
        if (json is null)
        {
            return BoardMappingSuggestion.Failed("The runtime did not return a JSON object.", output);
        }

        Dictionary<string, JsonElement>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        }
        catch (JsonException)
        {
            return BoardMappingSuggestion.Failed("The runtime's JSON could not be parsed.", output);
        }

        if (raw is null)
        {
            return BoardMappingSuggestion.Failed("The runtime's JSON could not be parsed.", output);
        }

        var canonicalByName = BoardStatusMapping.Catalog
            .ToDictionary(c => c.Status, StringComparer.OrdinalIgnoreCase);
        var stateByName = boardStates
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var assignments = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (key, value) in raw)
        {
            if (!canonicalByName.TryGetValue(key.Trim(), out var canonical))
            {
                continue;
            }

            var states = ReadStrings(value)
                .Select(s => s.Trim())
                .Where(s => stateByName.ContainsKey(s))
                .Select(s => stateByName[s])
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (states.Count > 0)
            {
                assignments[canonical.Status] = states;
            }
        }

        if (assignments.Count == 0)
        {
            return BoardMappingSuggestion.Failed("The runtime returned JSON but no usable assignments.", output);
        }

        return new BoardMappingSuggestion(true, assignments, null, output);
    }

    private static IEnumerable<string> ReadStrings(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var single = value.GetString();
                if (!string.IsNullOrWhiteSpace(single))
                {
                    yield return single;
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                    {
                        yield return s;
                    }
                }

                break;
        }
    }

    /// <summary>Finds the first balanced JSON object in text, tolerating prose and ``` fences around it.</summary>
    public static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text[start..(i + 1)];
                    }

                    break;
            }
        }

        return null;
    }

    private static string BuildPrompt(IReadOnlyList<string> boardStates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You map a software team's board statuses onto a fixed canonical model.");
        sb.AppendLine();
        sb.AppendLine("Canonical statuses (name - category):");
        foreach (var c in BoardStatusMapping.Catalog)
        {
            sb.Append("- ").Append(c.Status).Append(" - ").Append(c.Category).AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("The team's real board statuses are:");
        foreach (var state in boardStates)
        {
            sb.Append("- ").AppendLine(state);
        }

        sb.AppendLine();
        sb.AppendLine("Assign each real board status to exactly one canonical status (omit any that should not sync).");
        sb.AppendLine("Respond with ONLY a JSON object - no prose, no code fences - whose keys are the exact");
        sb.AppendLine("canonical status names above and whose values are arrays of the exact team board status");
        sb.AppendLine("names assigned to that canonical status. Example:");
        sb.AppendLine("{\"Ready\":[\"Selected for Dev\"],\"In Progress\":[\"Doing\"],\"Done\":[\"Closed\"]}");
        return sb.ToString();
    }

    /// <summary>The command for the call. Public so a test can see that no tool is granted and no folder named.</summary>
    public AgentCommand BuildCommand(AgentTool tool, AgentRuntimeDescriptor descriptor, string prompt)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in _credentials.EnvironmentFor(tool))
        {
            env[k] = v;
        }

        // Each runtime's own headless one-shot form, measured from its --help. `--tools ""` removes the built-in
        // tools altogether, as for a wiki summary. The two runtimes retired to v2 (docs/v2.md section 1) are kept
        // commented because their argv was read off the pinned binaries in the container and is not guessable;
        // neither can switch its tools off, which a return from v2 has to answer first.
        //
        // AgentTool.Codex => new List<string> { "exec", "--skip-git-repo-check", prompt },
        // AgentTool.Copilot => new List<string> { "-p", prompt, "--allow-all-tools" },
        return new AgentCommand(descriptor.Command, ["-p", "--tools", string.Empty], Timeout, env, StdIn: prompt);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
