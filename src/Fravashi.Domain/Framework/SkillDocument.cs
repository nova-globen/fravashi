namespace Fravashi.Domain.Framework;

/// <summary>
/// Serializes a <see cref="SkillDefinition"/> to its canonical <c>SKILL.md</c> form: YAML frontmatter (under
/// the quoted-value/unquoted-key contract) followed by the Markdown body. This is the Claude source-of-truth
/// projection used by the read-only preview pane (UIN1). <b>There is no file sync</b> (ADR 0005) - this
/// produces the text; nothing writes it to disk this version.
/// </summary>
public static class SkillDocument
{
    /// <summary>
    /// The frontmatter contract keys, in emission order: <c>name</c> and <c>description</c> always; the
    /// optional <c>allowed-tools</c> list only when non-empty. Only the Claude-canonical contract fields go in
    /// the frontmatter - Fravashi's interface.* presentation metadata does not.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, YamlValue>> Frontmatter(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var entries = new List<KeyValuePair<string, YamlValue>>
        {
            new("name", new YamlValue.Scalar(skill.Name)),
            new("description", new YamlValue.Scalar(skill.Description)),
        };

        if (skill.AllowedTools.Count > 0)
        {
            entries.Add(new KeyValuePair<string, YamlValue>(
                "allowed-tools", new YamlValue.List(skill.AllowedTools)));
        }

        return entries;
    }

    /// <summary>Renders the full <c>SKILL.md</c>: <c>---</c> frontmatter <c>---</c>, a blank line, then the body.</summary>
    public static string ToSkillMarkdown(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var frontmatter = YamlFrontmatter.Serialize(Frontmatter(skill));
        var body = (skill.Content ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        return $"---\n{frontmatter}---\n\n{body}\n";
    }
}
