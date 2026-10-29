namespace Fravashi.Domain.Framework;

/// <summary>
/// The authoring shape of a Command (UI11) - a Claude Code custom <b>slash command</b>. The command name is the
/// kebab <see cref="Name"/> (invoked as <c>/name</c>, taken from the file name, not the frontmatter); the
/// frontmatter carries <see cref="Description"/>, an optional <see cref="ArgumentHint"/>, an optional
/// <see cref="AllowedTools"/> permission-grant list, and an optional <see cref="Model"/>; the markdown
/// <see cref="Body"/> is the prompt template Claude runs. Verified against
/// <c>code.claude.com/docs/en/slash-commands</c> (now folded into Skills; legacy <c>.claude/commands/*.md</c>
/// still uses this exact frontmatter). Claude is canonical (ADR 0005) - this is a Claude-only artifact.
/// </summary>
public sealed record CommandDefinition
{
    /// <summary>Kebab-case name, unique among commands. Invoked as <c>/name</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The frontmatter <c>description</c> - what the command does / when to use it.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Optional <c>argument-hint</c> - an autocomplete hint, e.g. <c>[issue-number]</c>.</summary>
    public string ArgumentHint { get; init; } = string.Empty;

    /// <summary>
    /// Optional <c>allowed-tools</c> - tools granted without a permission prompt while the command runs. Note
    /// this is a permission grant (Claude Code's permission-rule syntax, e.g. <c>Bash(git add *)</c>), NOT an
    /// availability allowlist like a subagent's <c>tools</c>.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>Optional <c>model</c> override for the command's turn (an alias, a full id, or <c>inherit</c>). Null omits it.</summary>
    public string? Model { get; init; }

    /// <summary>The markdown body - the command's prompt template (supports <c>$ARGUMENTS</c> / <c>$N</c>).</summary>
    public string Body { get; init; } = string.Empty;
}

/// <summary>Field validation for a <see cref="CommandDefinition"/>. Kebab name, description, and a body are required.</summary>
public static class CommandValidation
{
    /// <summary>Every validation failure for <paramref name="command"/>, in field order. Empty when valid.</summary>
    public static IReadOnlyList<SkillValidationError> Validate(CommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var errors = new List<SkillValidationError>();

        if (!SkillValidation.IsKebabCase(command.Name))
        {
            errors.Add(new SkillValidationError(
                "name", "Name must be kebab-case (lowercase letters, digits and single hyphens), 1-64 characters."));
        }

        if (string.IsNullOrWhiteSpace(command.Description))
        {
            errors.Add(new SkillValidationError("description", "A description is required."));
        }

        if (string.IsNullOrWhiteSpace(command.Body))
        {
            errors.Add(new SkillValidationError("body", "The command body (prompt) is required."));
        }

        return errors;
    }

    /// <summary>True when <paramref name="command"/> has no validation errors.</summary>
    public static bool IsValid(CommandDefinition command) => Validate(command).Count == 0;
}

/// <summary>
/// Serializes a <see cref="CommandDefinition"/> to its canonical slash-command markdown: YAML frontmatter (under
/// the quoted-value/unquoted-key contract) followed by the prompt body. Read-only preview only (no file sync).
/// </summary>
public static class CommandDocument
{
    /// <summary>
    /// The command frontmatter, in emission order: <c>description</c> always; <c>argument-hint</c>,
    /// <c>allowed-tools</c> (a block list), and <c>model</c> only when set. The command name is the file
    /// identity, not a frontmatter field, so it is not emitted here.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, YamlValue>> Frontmatter(CommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var entries = new List<KeyValuePair<string, YamlValue>>
        {
            new("description", new YamlValue.Scalar(command.Description)),
        };

        if (!string.IsNullOrWhiteSpace(command.ArgumentHint))
        {
            entries.Add(new KeyValuePair<string, YamlValue>(
                "argument-hint", new YamlValue.Scalar(command.ArgumentHint)));
        }

        if (command.AllowedTools.Count > 0)
        {
            entries.Add(new KeyValuePair<string, YamlValue>(
                "allowed-tools", new YamlValue.List(command.AllowedTools)));
        }

        if (!string.IsNullOrWhiteSpace(command.Model))
        {
            entries.Add(new KeyValuePair<string, YamlValue>("model", new YamlValue.Scalar(command.Model!)));
        }

        return entries;
    }

    /// <summary>Renders the full command markdown: <c>---</c> frontmatter <c>---</c>, a blank line, then the body.</summary>
    public static string ToCommandMarkdown(CommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var frontmatter = YamlFrontmatter.Serialize(Frontmatter(command));
        var body = (command.Body ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        return $"---\n{frontmatter}---\n\n{body}\n";
    }
}
