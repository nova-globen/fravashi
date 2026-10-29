using System.Text.Json;
using System.Text.Json.Serialization;
using Fravashi.Domain.Framework;

namespace Fravashi.Application.Framework;

/// <summary>
/// Serializes a <see cref="SkillDefinition"/> to and from the JSON payload stored in a
/// <see cref="FrameworkArtifact"/>. Enums are written as names (stable across catalog reordering) and the JSON
/// is indented so a stored row is human-diffable. The skill's kebab <see cref="SkillDefinition.Name"/> is the
/// artifact's <see cref="FrameworkArtifact.Name"/> as well, so uniqueness and lookup work on the row without
/// parsing the payload.
/// </summary>
public static class SkillPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes the definition to indented JSON.</summary>
    public static string Serialize(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return JsonSerializer.Serialize(skill, Options);
    }

    /// <summary>Deserializes a definition from its JSON payload.</summary>
    public static SkillDefinition Deserialize(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        return JsonSerializer.Deserialize<SkillDefinition>(payloadJson, Options)
            ?? throw new FormatException("The skill payload could not be deserialized.");
    }

    /// <summary>Reads the definition out of a stored artifact.</summary>
    public static SkillDefinition From(FrameworkArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return Deserialize(artifact.PayloadJson);
    }
}
