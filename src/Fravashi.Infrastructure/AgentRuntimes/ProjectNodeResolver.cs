using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>What a project asked for and where it said so (backlog 10's banner names both).</summary>
/// <param name="Version">The version-shaped token, without a leading <c>v</c>.</param>
/// <param name="Source">The file that asked: <c>.nvmrc</c>, <c>.node-version</c> or <c>package.json</c>.</param>
public sealed record ProjectNodeRequest(string Version, string Source);

/// <summary>
/// The Node version a project asks for (plan 2d, decision 10): <c>.nvmrc</c> first, then
/// <c>.node-version</c>, then <c>package.json</c> <c>engines.node</c> (first version-shaped token
/// of the range). Null when nothing asks; the caller then keeps the image Node.
/// </summary>
public static partial class ProjectNodeResolver
{
    [GeneratedRegex(@"\d+(?:\.\d+){0,2}")]
    private static partial Regex VersionToken();

    public static string? Resolve(string workingDirectory) => ResolveRequest(workingDirectory)?.Version;

    /// <summary>The request with its source file, for a surface that has to say who asked.</summary>
    public static ProjectNodeRequest? ResolveRequest(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        foreach (var file in new[] { ".nvmrc", ".node-version" })
        {
            var path = Path.Combine(workingDirectory, file);
            if (!File.Exists(path))
            {
                continue;
            }

            var version = Normalize(SafeRead(path));
            if (version is not null)
            {
                return new ProjectNodeRequest(version, file);
            }
        }

        var packageJson = Path.Combine(workingDirectory, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(SafeRead(packageJson) ?? "{}");
                if (doc.RootElement.TryGetProperty("engines", out var engines)
                    && engines.ValueKind == JsonValueKind.Object
                    && engines.TryGetProperty("node", out var node)
                    && node.ValueKind == JsonValueKind.String
                    && Normalize(node.GetString()) is { } version)
                {
                    return new ProjectNodeRequest(version, "package.json");
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static string? SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var match = VersionToken().Match(raw);
        return match.Success ? match.Value : null;
    }
}
