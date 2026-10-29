using Fravashi.Domain.AgentSessions;

namespace Fravashi.Domain.Framework;

/// <summary>
/// The authoring shape of an Agent (UI8 - UI10) - a Claude Code <b>subagent</b>. The Claude source-of-truth
/// contract is the subagent markdown: YAML frontmatter (<see cref="Name"/>, <see cref="Description"/> - the
/// load-bearing "when to delegate", an optional <see cref="Tools"/> allowlist, an optional <see cref="Model"/>)
/// plus the markdown body (<see cref="SystemPrompt"/>). The Fravashi <c>interface.*</c> fields (display name,
/// short description, icons, brand colour, default prompt, targets) are all <b>optional</b> for agents and drive
/// only how the agent presents inside Fravashi. Claude is canonical (ADR 0005 / D03-11); <see cref="Targets"/>
/// is declared intent only until file-sync/execution lands.
/// </summary>
public sealed record AgentDefinition
{
    // ── Claude subagent contract (frontmatter + body - the source-of-truth fields) ───────────────────────────

    /// <summary>Kebab-case name, unique among agents. Claude receives it as the subagent's <c>agent_type</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The load-bearing "when to delegate" description Claude reads to decide whether to route to this subagent.
    /// This is the frontmatter <c>description</c> and must be specific about when the subagent applies.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Optional <c>tools</c> allowlist (frontmatter, a comma-separated string). Empty = the subagent inherits
    /// all tools. A tool that resolves to nothing prevents the subagent launching, so the UI keeps it optional.
    /// </summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>
    /// Optional <c>model</c> (frontmatter): a model alias (<c>sonnet</c>/<c>opus</c>/<c>haiku</c>), a full id, or
    /// <c>inherit</c> (the default - use the main conversation's model). Null omits the field.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>The markdown body - the subagent's system prompt (required).</summary>
    public string SystemPrompt { get; init; } = string.Empty;

    // ── interface.* (all optional for agents - how the agent presents inside Fravashi) ───────────────────────

    /// <summary><c>interface.display_name</c> - an optional user-facing name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary><c>interface.short_description</c> - an optional user-facing card subtitle.</summary>
    public string ShortDescription { get; init; } = string.Empty;

    /// <summary><c>interface.icon_small</c> - a picked FluentUI icon or an uploaded image (optional).</summary>
    public SkillIcon IconSmall { get; init; } = SkillIcon.None;

    /// <summary><c>interface.icon_large</c> - same picker; falls back to <see cref="IconSmall"/> when unset.</summary>
    public SkillIcon IconLarge { get; init; } = SkillIcon.None;

    /// <summary><c>interface.brand_color</c> - an optional hex colour used as the card accent.</summary>
    public string BrandColor { get; init; } = string.Empty;

    /// <summary><c>interface.default_prompt</c> - an optional surrounding prompt to invoke the agent with.</summary>
    public string DefaultPrompt { get; init; } = string.Empty;

    /// <summary><c>interface.targets[]</c> - the configured agent runtimes this agent targets (metadata now; D03-11).</summary>
    public IReadOnlyList<AgentTool> Targets { get; init; } = [];

    /// <summary>The frontmatter <c>model</c> value meaning "use the main conversation's model".</summary>
    public const string InheritModel = "inherit";

    /// <summary>The effective large icon: <see cref="IconLarge"/> when set, otherwise <see cref="IconSmall"/>.</summary>
    public SkillIcon EffectiveIconLarge => IconLarge.IsSet ? IconLarge : IconSmall;
}

/// <summary>
/// Field validation for an <see cref="AgentDefinition"/>. The Claude contract fields (kebab name, load-bearing
/// description, a non-empty system prompt) are required; the <c>interface.*</c> fields are optional and only the
/// brand colour is shape-checked when present. Name uniqueness is a store concern, enforced there.
/// </summary>
public static class AgentValidation
{
    /// <summary>Every validation failure for <paramref name="agent"/>, in field order. Empty when valid.</summary>
    public static IReadOnlyList<SkillValidationError> Validate(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var errors = new List<SkillValidationError>();

        if (!SkillValidation.IsKebabCase(agent.Name))
        {
            errors.Add(new SkillValidationError(
                "name", "Name must be kebab-case (lowercase letters, digits and single hyphens), 1-64 characters."));
        }

        if (string.IsNullOrWhiteSpace(agent.Description))
        {
            errors.Add(new SkillValidationError(
                "description",
                "A description is required - it is what Claude reads to decide when to delegate to this agent."));
        }

        if (string.IsNullOrWhiteSpace(agent.SystemPrompt))
        {
            errors.Add(new SkillValidationError(
                "system_prompt", "The system prompt (body) is required - it is the subagent's instructions."));
        }

        if (!string.IsNullOrWhiteSpace(agent.BrandColor) && !SkillValidation.IsHexColor(agent.BrandColor))
        {
            errors.Add(new SkillValidationError(
                "brand_color", "Brand colour must be a hex value such as #7C4DFF or #ABC."));
        }

        return errors;
    }

    /// <summary>True when <paramref name="agent"/> has no validation errors.</summary>
    public static bool IsValid(AgentDefinition agent) => Validate(agent).Count == 0;
}

/// <summary>
/// Serializes an <see cref="AgentDefinition"/> to its canonical Claude <b>subagent markdown</b>: YAML
/// frontmatter (under the quoted-value/unquoted-key contract, ADR 0005 / D03-6) followed by the system-prompt
/// body. Read-only preview only - there is no file sync (ADR 0005).
/// </summary>
public static class AgentDocument
{
    /// <summary>
    /// The subagent frontmatter, in emission order: <c>name</c> and <c>description</c> always; <c>tools</c>
    /// (a comma-joined scalar) and <c>model</c> only when set. Only the Claude-canonical contract fields go in
    /// the frontmatter - Fravashi's interface.* presentation metadata does not.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, YamlValue>> Frontmatter(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var entries = new List<KeyValuePair<string, YamlValue>>
        {
            new("name", new YamlValue.Scalar(agent.Name)),
            new("description", new YamlValue.Scalar(agent.Description)),
        };

        if (agent.Tools.Count > 0)
        {
            entries.Add(new KeyValuePair<string, YamlValue>(
                "tools", new YamlValue.Scalar(string.Join(", ", agent.Tools))));
        }

        if (!string.IsNullOrWhiteSpace(agent.Model))
        {
            entries.Add(new KeyValuePair<string, YamlValue>("model", new YamlValue.Scalar(agent.Model!)));
        }

        return entries;
    }

    /// <summary>Renders the full subagent markdown: <c>---</c> frontmatter <c>---</c>, a blank line, then the body.</summary>
    public static string ToAgentMarkdown(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var frontmatter = YamlFrontmatter.Serialize(Frontmatter(agent));
        var body = (agent.SystemPrompt ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        return $"---\n{frontmatter}---\n\n{body}\n";
    }
}
