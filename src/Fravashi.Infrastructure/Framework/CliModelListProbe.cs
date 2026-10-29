using System.Text.Json;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Framework;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Framework;

/// <summary>
/// The live model-list probe (D03-12). No runtime this version offers has a scriptable list-models command, so
/// it honestly answers null for every one of them and the resolver falls back to the curated catalog. Claude
/// Code (anthropics/claude-code#12612) has none.
/// <para>
/// It is NOT an empty seam: the one runtime that did have such a command was OpenAI Codex
/// (<c>codex debug models</c>), which was retired to v2 by decision on 2026-09-22 (docs/v2.md section 1).
/// Its call and its defensive parser are kept below, commented and tested, because the parser was written
/// against a debug surface whose exact shape is not contractual, and re-deriving that is real work.
/// </para>
/// </summary>
public sealed class CliModelListProbe(IAgentCommandRunner runner) : IModelListProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The command runner the retired probe shelled out through. Held rather than dropped so the constructor,
    /// the DI registration and the tests keep their shape, and restoring the path in v2 is an uncommenting
    /// rather than a rewrite.
    /// </summary>
    internal IAgentCommandRunner Runner { get; } = runner;

    public async Task<IReadOnlyList<string>?> TryListAsync(
        AgentTool tool, CancellationToken cancellationToken = default)
    {
        // No runtime this version offers has a scriptable list-models command, so nothing is shelled out and
        // the resolver falls back to the curated catalog. The Codex path is retired to v2 (docs/v2.md
        // section 1); Parse below is what it called and stays covered by its own tests.
        //
        // if (tool != AgentTool.Codex)
        // {
        //     return null;
        // }
        //
        // var descriptor = AgentRuntimeCatalog.For(tool);
        // var command = new AgentCommand(descriptor.Command, ["debug", "models"], Timeout);
        // AgentCommandResult result;
        // try
        // {
        //     result = await runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        // }
        // catch (OperationCanceledException)
        // {
        //     throw;
        // }
        // catch (Exception)
        // {
        //     return null;
        // }
        //
        // if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        // {
        //     return null;
        // }
        //
        // return Parse(result.StandardOutput);
        await Task.CompletedTask.ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Extracts model ids from <c>codex debug models</c> JSON. The command's exact shape is a debug surface, so
    /// the parser is defensive: it accepts either an array of objects/strings or an object with a
    /// <c>models</c>/<c>data</c> array, and pulls an <c>id</c>/<c>name</c>/<c>model</c> field or a bare string.
    /// Unparseable output yields null (fall back to the catalog).
    /// </summary>
    internal static IReadOnlyList<string>? Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var array = doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => doc.RootElement,
                JsonValueKind.Object when TryGetArray(doc.RootElement, out var inner) => inner,
                _ => default,
            };

            if (array.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var ids = new List<string>();
            foreach (var element in array.EnumerateArray())
            {
                var id = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Object => IdField(element),
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id))
                {
                    ids.Add(id);
                }
            }

            return ids.Count > 0 ? ids : null;
        }
    }

    private static bool TryGetArray(JsonElement obj, out JsonElement array)
    {
        foreach (var key in (ReadOnlySpan<string>)["models", "data", "available"])
        {
            if (obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                array = value;
                return true;
            }
        }

        array = default;
        return false;
    }

    private static string? IdField(JsonElement obj)
    {
        foreach (var key in (ReadOnlySpan<string>)["id", "name", "model", "slug"])
        {
            if (obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}
