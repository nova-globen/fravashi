using System.Text.Json;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// Whether a call to one of the studio's OWN tools (<c>mcp__fravashi__*</c>) carries arguments its own declared input
/// schema could accept (follow-up filed 2026-10-22, approval card with no arguments). Seen three times on 2026-10-21:
/// an agent called a deferred studio tool before loading its schema with ToolSearch, so the call carried <c>{}</c>, and a
/// person was asked to approve a call the tool itself would refuse for want of an item number.
/// <para>
/// This is deliberately NOT a JSON Schema engine. It checks exactly two things against the schema the tool already
/// declares: every name in <c>required</c> is present, and every declared property that IS present has the top-level
/// JSON kind its <c>type</c> names. Anything it cannot read or evaluate (an unparsable schema, no <c>properties</c>
/// object, a <c>type</c> it does not know, a value whose kind it cannot tell) is skipped, which leaves the call to be
/// decided exactly as before. It only ever produces a REFUSAL sentence; it never says a call is fine, so nothing it
/// answers can widen what the broker allows.
/// </para>
/// </summary>
public static class StudioToolArgumentCheck
{
    /// <summary>
    /// The refusal sentence for a call whose arguments do not satisfy the tool's schema, or null when they do, when
    /// the tool is not a studio tool, or when the schema cannot be evaluated.
    /// </summary>
    /// <param name="toolName">The tool name as the CLI sent it (<c>mcp__fravashi__work_item_update</c>).</param>
    /// <param name="schemaJson">The tool's <see cref="Application.Sessions.Tools.IStudioTool.InputSchemaJson"/>, or null when unknown.</param>
    /// <param name="input">The call's raw arguments, with each value's JSON kind.</param>
    public static string? Check(string? toolName, string? schemaJson, AgentToolInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrEmpty(toolName)
            || !toolName.StartsWith(AgentWorkGate.StudioToolPrefix, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(schemaJson))
        {
            return null;
        }

        List<string> missing = [];
        List<string> wrong = [];
        try
        {
            using var schema = JsonDocument.Parse(schemaJson);
            var root = schema.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in required.EnumerateArray())
                {
                    if (name.ValueKind == JsonValueKind.String
                        && name.GetString() is { Length: > 0 } field
                        && input.KindOf(field) == AgentToolValueKind.Absent)
                    {
                        missing.Add(field);
                    }
                }
            }

            if (root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    if (input.KindOf(property.Name) == AgentToolValueKind.Absent
                        || property.Value.ValueKind != JsonValueKind.Object
                        || !property.Value.TryGetProperty("type", out var type)
                        || DeclaredKinds(type) is not { } declared
                        || KindOf(input, property.Name) is not { } actual)
                    {
                        continue;
                    }

                    if (!declared.Any(kind => Matches(kind, actual)))
                    {
                        wrong.Add($"{property.Name} (expected {string.Join(" or ", declared)}, got {actual.Name})");
                    }
                }
            }
        }
        catch (JsonException)
        {
            // A schema the studio cannot read is not a reason to refuse: the call is decided as before.
            return null;
        }

        if (missing.Count == 0 && wrong.Count == 0)
        {
            return null;
        }

        var bare = toolName[AgentWorkGate.StudioToolPrefix.Length..];
        var problems = new List<string>();
        if (missing.Count > 0)
        {
            problems.Add("missing required " + string.Join(", ", missing));
        }

        if (wrong.Count > 0)
        {
            problems.Add("wrong kind for " + string.Join(", ", wrong));
        }

        var empty = input.Count == 0 ? " The call carried no arguments at all." : string.Empty;
        return $"The studio refused this {bare} call without asking anyone: its arguments do not match the tool's own "
            + $"input schema ({string.Join("; ", problems)}).{empty} If you have not loaded this tool's schema yet, load "
            + $"it first with ToolSearch (query \"select:{toolName}\"), then call it again with those arguments.";
    }

    /// <summary>The type names a property declares, or null when any of them is one this check does not know.</summary>
    private static IReadOnlyList<string>? DeclaredKinds(JsonElement type)
    {
        List<string> kinds = [];
        if (type.ValueKind == JsonValueKind.String)
        {
            kinds.Add(type.GetString() ?? string.Empty);
        }
        else if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in type.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                kinds.Add(entry.GetString() ?? string.Empty);
            }
        }

        return kinds.Count > 0 && kinds.All(Known.Contains) ? kinds : null;
    }

    private static readonly HashSet<string> Known =
        new(["string", "integer", "number", "boolean", "array", "object", "null"], StringComparer.Ordinal);

    /// <summary>What a value arrived as, read from the raw input; null when it cannot be told.</summary>
    private sealed record ValueKind(string Name, bool Integral = false);

    private static ValueKind? KindOf(AgentToolInput input, string field)
    {
        if (input.KindOf(field) == AgentToolValueKind.Text)
        {
            return new ValueKind("string");
        }

        // Not a JSON string: the endpoint kept its raw JSON text, so its kind can be read back from it.
        try
        {
            using var value = JsonDocument.Parse(input[field]);
            var element = value.RootElement;
            return element.ValueKind switch
            {
                JsonValueKind.Number => new ValueKind(
                    "number",
                    // A number too large for a decimal cannot be told apart here, so it is not called fractional.
                    !element.TryGetDecimal(out var number) || decimal.Truncate(number) == number),
                JsonValueKind.True or JsonValueKind.False => new ValueKind("boolean"),
                JsonValueKind.Array => new ValueKind("array"),
                JsonValueKind.Object => new ValueKind("object"),
                JsonValueKind.Null => new ValueKind("null"),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Matches(string declared, ValueKind actual) => declared switch
    {
        "integer" => actual.Name == "number" && actual.Integral,
        _ => string.Equals(declared, actual.Name, StringComparison.Ordinal),
    };
}
