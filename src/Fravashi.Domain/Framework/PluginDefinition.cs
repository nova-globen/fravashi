using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fravashi.Domain.Framework;

/// <summary>Author metadata for a plugin manifest - an object, not a bare string. All parts optional.</summary>
/// <param name="Name">The author's name.</param>
/// <param name="Email">The author's email.</param>
/// <param name="Url">The author's URL.</param>
public sealed record PluginAuthor(string Name = "", string Email = "", string Url = "")
{
    /// <summary>True when at least one author field is populated.</summary>
    public bool IsSet =>
        !string.IsNullOrWhiteSpace(Name) || !string.IsNullOrWhiteSpace(Email) || !string.IsNullOrWhiteSpace(Url);
}

/// <summary>
/// Component path references for a plugin manifest. Each is a list of <c>./</c>-relative paths to the component
/// files/directories the plugin ships. <b>Metadata-only in this version</b> (D03-11 does not settle runtime
/// consumption and there is no file sync - ADR 0005): Fravashi records the declared paths for the manifest but
/// does not resolve or execute them. The UI says so.
/// </summary>
public sealed record PluginComponents
{
    /// <summary><c>commands</c> component paths.</summary>
    public IReadOnlyList<string> Commands { get; init; } = [];

    /// <summary><c>agents</c> component paths.</summary>
    public IReadOnlyList<string> Agents { get; init; } = [];

    /// <summary><c>hooks</c> component paths.</summary>
    public IReadOnlyList<string> Hooks { get; init; } = [];

    /// <summary><c>mcpServers</c> component paths.</summary>
    public IReadOnlyList<string> McpServers { get; init; } = [];

    /// <summary><c>skills</c> component paths.</summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>True when any component list has entries.</summary>
    public bool Any =>
        Commands.Count + Agents.Count + Hooks.Count + McpServers.Count + Skills.Count > 0;
}

/// <summary>
/// The authoring shape of a Plugin (UI11) - the Claude Code <c>.claude-plugin/plugin.json</c> manifest. Only
/// <see cref="Name"/> is required upstream; everything else is optional (an omitted <see cref="Version"/> means
/// the loader falls back to the git SHA). Verified against <c>code.claude.com/docs/en/plugins-reference</c>.
/// Claude-only (ADR 0005).
/// </summary>
public sealed record PluginDefinition
{
    /// <summary>Kebab-case identifier, unique among plugins. Used to namespace the plugin's components.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional human-readable name; falls back to <see cref="Name"/> in UIs.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Optional brief purpose.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Optional semver version (e.g. <c>2.1.0</c>). Omitted → the loader uses the git commit SHA.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Optional author object.</summary>
    public PluginAuthor Author { get; init; } = new();

    /// <summary>Optional documentation URL.</summary>
    public string Homepage { get; init; } = string.Empty;

    /// <summary>Optional source-code URL.</summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>Optional SPDX license id (e.g. <c>MIT</c>).</summary>
    public string License { get; init; } = string.Empty;

    /// <summary>Optional discovery keywords.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Optional component path references (metadata-only this version).</summary>
    public PluginComponents Components { get; init; } = new();
}

/// <summary>Field validation for a <see cref="PluginDefinition"/> - kebab name required; version, if present, semver.</summary>
public static partial class PluginValidation
{
    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)*$")]
    private static partial Regex SemverRegex();

    /// <summary>True when <paramref name="value"/> is a valid (loose) semver string.</summary>
    public static bool IsSemver(string value) =>
        !string.IsNullOrEmpty(value) && SemverRegex().IsMatch(value);

    /// <summary>Every validation failure for <paramref name="plugin"/>, in field order. Empty when valid.</summary>
    public static IReadOnlyList<SkillValidationError> Validate(PluginDefinition plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var errors = new List<SkillValidationError>();

        if (!SkillValidation.IsKebabCase(plugin.Name))
        {
            errors.Add(new SkillValidationError(
                "name", "Name must be kebab-case (lowercase letters, digits and single hyphens), 1-64 characters."));
        }

        if (!string.IsNullOrWhiteSpace(plugin.Version) && !IsSemver(plugin.Version))
        {
            errors.Add(new SkillValidationError(
                "version", "Version must be semver, such as 1.0.0 or 2.1.0-beta.1."));
        }

        return errors;
    }

    /// <summary>True when <paramref name="plugin"/> has no validation errors.</summary>
    public static bool IsValid(PluginDefinition plugin) => Validate(plugin).Count == 0;
}

/// <summary>
/// Serializes a <see cref="PluginDefinition"/> to its canonical <c>plugin.json</c> manifest. Optional fields are
/// emitted only when set; a component list of one is written as a string, of many as an array (idiomatic
/// manifests). Indented, stable JSON for the read-only preview (no file sync).
/// </summary>
public static class PluginDocument
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Renders the manifest as indented JSON.</summary>
    public static string ToPluginJson(PluginDefinition plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        var root = new JsonObject { ["name"] = string.IsNullOrWhiteSpace(plugin.Name) ? "plugin" : plugin.Name };

        AddIfSet(root, "displayName", plugin.DisplayName);
        AddIfSet(root, "version", plugin.Version);
        AddIfSet(root, "description", plugin.Description);

        if (plugin.Author.IsSet)
        {
            var author = new JsonObject();
            AddIfSet(author, "name", plugin.Author.Name);
            AddIfSet(author, "email", plugin.Author.Email);
            AddIfSet(author, "url", plugin.Author.Url);
            root["author"] = author;
        }

        AddIfSet(root, "homepage", plugin.Homepage);
        AddIfSet(root, "repository", plugin.Repository);
        AddIfSet(root, "license", plugin.License);

        if (plugin.Keywords.Count > 0)
        {
            root["keywords"] = new JsonArray([.. plugin.Keywords.Select(k => (JsonNode?)k)]);
        }

        AddComponent(root, "commands", plugin.Components.Commands);
        AddComponent(root, "agents", plugin.Components.Agents);
        AddComponent(root, "hooks", plugin.Components.Hooks);
        AddComponent(root, "mcpServers", plugin.Components.McpServers);
        AddComponent(root, "skills", plugin.Components.Skills);

        return root.ToJsonString(Options);
    }

    private static void AddIfSet(JsonObject obj, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            obj[key] = value;
        }
    }

    private static void AddComponent(JsonObject obj, string key, IReadOnlyList<string> paths)
    {
        var trimmed = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (trimmed.Count == 1)
        {
            obj[key] = trimmed[0];
        }
        else if (trimmed.Count > 1)
        {
            obj[key] = new JsonArray([.. trimmed.Select(p => (JsonNode?)p)]);
        }
    }
}
