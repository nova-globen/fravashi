using System.Text.Json;

namespace Fravashi.Launcher.Releases;

/// <summary>
/// Parses the release manifest JSON (ADR 0006) into a <see cref="ReleaseManifest"/>. Tolerant of comments,
/// trailing commas, and unknown properties (forward/backward compatibility), but strict about the two things
/// a consumer must be able to trust: it is valid JSON and it names a version.
/// </summary>
public static class ReleaseManifestParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parses <paramref name="json"/>; throws <see cref="FormatException"/> on malformed input.</summary>
    public static ReleaseManifest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FormatException("The release manifest is empty.");
        }

        ReleaseManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The release manifest is not valid JSON: {ex.Message}", ex);
        }

        if (manifest is null)
        {
            throw new FormatException("The release manifest deserialized to null.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new FormatException("The release manifest has no version.");
        }

        return manifest;
    }

    /// <summary>Non-throwing parse - <c>true</c> with the manifest, or <c>false</c> on any malformed input.</summary>
    public static bool TryParse(string json, out ReleaseManifest manifest)
    {
        try
        {
            manifest = Parse(json);
            return true;
        }
        catch (FormatException)
        {
            manifest = new ReleaseManifest();
            return false;
        }
    }
}
