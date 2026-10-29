using System.Text.RegularExpressions;

namespace Fravashi.Domain.Framework;

/// <summary>One field-level validation failure: which field, and a human-readable message.</summary>
/// <param name="Field">A stable field key (e.g. <c>name</c>, <c>short_description</c>) for the UI to bind to.</param>
/// <param name="Message">The message shown to the author.</param>
public sealed record SkillValidationError(string Field, string Message);

/// <summary>
/// Pure field validation for a <see cref="SkillDefinition"/> - the single source of the rules the form, the
/// unit tests, and the bUnit tests all assert against. Uniqueness of <see cref="SkillDefinition.Name"/> is a
/// store concern (it needs the other rows) and is enforced there, not here.
/// </summary>
public static partial class SkillValidation
{
    /// <summary>Lower bound (inclusive) on <c>interface.short_description</c>.</summary>
    public const int ShortDescriptionMin = 25;

    /// <summary>Upper bound (inclusive) on <c>interface.short_description</c>.</summary>
    public const int ShortDescriptionMax = 64;

    /// <summary>Upper bound on the load-bearing frontmatter <c>description</c>.</summary>
    public const int DescriptionMax = 1024;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KebabRegex();

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColorRegex();

    /// <summary>True when <paramref name="value"/> is a valid kebab-case name (lowercase, digits, single hyphens).</summary>
    public static bool IsKebabCase(string value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 64 && KebabRegex().IsMatch(value);

    /// <summary>True when <paramref name="value"/> is a valid 3- or 6-digit hex colour.</summary>
    public static bool IsHexColor(string value) =>
        !string.IsNullOrEmpty(value) && HexColorRegex().IsMatch(value);

    /// <summary>Every validation failure for <paramref name="skill"/>, in field order. Empty when valid.</summary>
    public static IReadOnlyList<SkillValidationError> Validate(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var errors = new List<SkillValidationError>();

        if (!IsKebabCase(skill.Name))
        {
            errors.Add(new SkillValidationError(
                "name", "Name must be kebab-case (lowercase letters, digits and single hyphens), 1-64 characters."));
        }

        if (string.IsNullOrWhiteSpace(skill.DisplayName))
        {
            errors.Add(new SkillValidationError("display_name", "A display name is required."));
        }

        var shortLen = (skill.ShortDescription ?? string.Empty).Trim().Length;
        if (shortLen < ShortDescriptionMin || shortLen > ShortDescriptionMax)
        {
            errors.Add(new SkillValidationError(
                "short_description",
                $"Short description must be {ShortDescriptionMin} - {ShortDescriptionMax} characters (currently {shortLen})."));
        }

        if (string.IsNullOrWhiteSpace(skill.Description))
        {
            errors.Add(new SkillValidationError(
                "description", "A description is required - it is what Claude reads to decide when to use the skill."));
        }
        else if (skill.Description.Length > DescriptionMax)
        {
            errors.Add(new SkillValidationError(
                "description", $"Description must be at most {DescriptionMax} characters."));
        }

        if (!string.IsNullOrWhiteSpace(skill.BrandColor) && !IsHexColor(skill.BrandColor))
        {
            errors.Add(new SkillValidationError(
                "brand_color", "Brand colour must be a hex value such as #7C4DFF or #ABC."));
        }
        else if (string.IsNullOrWhiteSpace(skill.BrandColor))
        {
            errors.Add(new SkillValidationError("brand_color", "A brand colour is required."));
        }

        if (string.IsNullOrWhiteSpace(skill.DefaultPrompt) ||
            !skill.DefaultPrompt.Contains(SkillDefinition.SkillNamePlaceholder, StringComparison.Ordinal))
        {
            errors.Add(new SkillValidationError(
                "default_prompt",
                $"The default prompt must reference the skill via the {SkillDefinition.SkillNamePlaceholder} placeholder."));
        }

        if (string.IsNullOrWhiteSpace(skill.Content))
        {
            errors.Add(new SkillValidationError("content", "The skill content (body) is required."));
        }

        return errors;
    }

    /// <summary>True when <paramref name="skill"/> has no validation errors.</summary>
    public static bool IsValid(SkillDefinition skill) => Validate(skill).Count == 0;
}
