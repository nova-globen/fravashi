using System.Text.Json;
using System.Text.Json.Serialization;
using Fravashi.Domain.Framework;

namespace Fravashi.Application.Framework;

/// <summary>
/// Serializes a typed framework-artifact definition (agent, command, hook, MCP server, plugin) to and from the
/// JSON <see cref="FrameworkArtifact.PayloadJson"/> it is stored in. The same options the T2 <see cref="SkillPayload"/>
/// uses - enums as names (stable across catalog reordering), indented for a human-diffable row, nulls omitted.
/// The definition's kebab name is mirrored onto <see cref="FrameworkArtifact.Name"/> by the caller so uniqueness
/// and lookup work on the row without parsing the payload.
/// </summary>
/// <typeparam name="T">The kind-specific definition record.</typeparam>
public static class FrameworkPayload<T>
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes the definition to indented JSON.</summary>
    public static string Serialize(T definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(definition, Options);
    }

    /// <summary>Deserializes a definition from its JSON payload.</summary>
    public static T Deserialize(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        return JsonSerializer.Deserialize<T>(payloadJson, Options)
            ?? throw new FormatException($"The {typeof(T).Name} payload could not be deserialized.");
    }

    /// <summary>Reads the definition out of a stored artifact.</summary>
    public static T From(FrameworkArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return Deserialize(artifact.PayloadJson);
    }
}
