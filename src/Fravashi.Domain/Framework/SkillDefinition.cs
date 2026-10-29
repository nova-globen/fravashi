using Fravashi.Domain.AgentSessions;

namespace Fravashi.Domain.Framework;

/// <summary>
/// An icon reference: either a FluentUI icon picked from the installed icon set (<see cref="FluentIconName"/>)
/// or an uploaded image stored as a <see cref="FrameworkResource"/> (<see cref="ResourceId"/>). Exactly one is
/// set when the icon is chosen; neither when it is unset.
/// </summary>
/// <param name="FluentIconName">The FluentUI icon name (e.g. <c>Sparkle</c>), or null.</param>
/// <param name="ResourceId">The uploaded-image resource id, or null.</param>
public sealed record SkillIcon(string? FluentIconName, string? ResourceId)
{
    /// <summary>An unset icon.</summary>
    public static SkillIcon None { get; } = new(null, null);

    /// <summary>True when either an icon name or an uploaded image is set.</summary>
    public bool IsSet =>
        !string.IsNullOrWhiteSpace(FluentIconName) || !string.IsNullOrWhiteSpace(ResourceId);

    /// <summary>True when this icon is an uploaded image rather than a picked FluentUI icon.</summary>
    public bool IsUpload => !string.IsNullOrWhiteSpace(ResourceId);
}

/// <summary>
/// The full authoring shape of a Skill (UI3 - UI7, UI12). Split into the Fravashi <c>interface.*</c> fields (how
/// the skill presents inside Fravashi - display name, card subtitle, icons, brand colour, default prompt, body,
/// resources, targets) and the Claude source-of-truth contract fields that serialize into <c>SKILL.md</c>
/// frontmatter (<see cref="Name"/>, <see cref="Description"/>, <see cref="AllowedTools"/>). Claude is canonical
/// (ADR 0005 / D03-11); <see cref="Targets"/> is declared intent only until file-sync/execution lands.
/// </summary>
public sealed record SkillDefinition
{
    // ── Claude contract (SKILL.md frontmatter - the source-of-truth fields) ──────────────────────────────────

    /// <summary>Kebab-case name, unique among skills. The invocation identity a prompt references as <c>$name</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The load-bearing "when to use" description Claude reads to decide whether to invoke the skill. Distinct
    /// from <see cref="ShortDescription"/> (which is Fravashi's card subtitle) - this is the frontmatter
    /// <c>description</c> and must be specific about when the skill applies.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Optional <c>allowed-tools</c> frontmatter list (documented for Claude Code Agent Skills).</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    // ── interface.* (how the skill presents inside Fravashi) ─────────────────────────────────────────────────

    /// <summary><c>interface.display_name</c> - the human-facing title.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary><c>interface.short_description</c> - the card subtitle, enforced to 25-64 characters.</summary>
    public string ShortDescription { get; init; } = string.Empty;

    /// <summary><c>interface.icon_small</c> - a picked FluentUI icon or an uploaded image.</summary>
    public SkillIcon IconSmall { get; init; } = SkillIcon.None;

    /// <summary><c>interface.icon_large</c> - same picker; falls back to <see cref="IconSmall"/> when unset.</summary>
    public SkillIcon IconLarge { get; init; } = SkillIcon.None;

    /// <summary><c>interface.brand_color</c> - a hex colour (e.g. <c>#7C4DFF</c>) used as the card accent.</summary>
    public string BrandColor { get; init; } = string.Empty;

    /// <summary><c>interface.default_prompt</c> - must explicitly reference the skill via the <c>$skill-name</c> placeholder.</summary>
    public string DefaultPrompt { get; init; } = string.Empty;

    /// <summary><c>interface.content</c> - the skill body, Markdown, serialized as the <c>SKILL.md</c> body.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary><c>interface.targets[]</c> - the configured agent runtimes this skill targets (metadata now; D03-11).</summary>
    public IReadOnlyList<AgentTool> Targets { get; init; } = [];

    /// <summary>An optional pinned model id (UI12/D03-12), drawn from the selected targets' available models.</summary>
    public string? PinnedModel { get; init; }

    /// <summary>The literal placeholder <see cref="DefaultPrompt"/> must contain (a fixed token, not the skill's name).</summary>
    public const string SkillNamePlaceholder = "$skill-name";

    /// <summary>The effective large icon: <see cref="IconLarge"/> when set, otherwise <see cref="IconSmall"/>.</summary>
    public SkillIcon EffectiveIconLarge => IconLarge.IsSet ? IconLarge : IconSmall;
}
