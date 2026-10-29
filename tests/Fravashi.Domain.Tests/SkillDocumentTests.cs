using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Framework;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Domain.Tests;

/// <summary>The SKILL.md projection (UIN1): quoted-value/unquoted-key frontmatter + Markdown body.</summary>
public class SkillDocumentTests
{
    private static SkillDefinition Sample() => new()
    {
        Name = "weekly-status-update",
        Description = "Use when the user asks for a concise weekly status: summarise recent commits.",
        DisplayName = "Weekly status",
        ShortDescription = "Drafts a concise weekly status update",
        DefaultPrompt = "Use $skill-name to draft the update.",
        BrandColor = "#7C4DFF",
        Content = "# Weekly status\n\nSteps here.",
        Targets = [AgentTool.Claude],
    };

    [Fact]
    public void ToSkillMarkdown_wraps_frontmatter_in_fences_with_body_following()
    {
        var md = SkillDocument.ToSkillMarkdown(Sample());

        md.Should().StartWith("---\n");
        md.Should().Contain("name: \"weekly-status-update\"\n");
        md.Should().Contain("description: \"Use when the user asks for a concise weekly status: summarise recent commits.\"\n");
        md.Should().Contain("---\n\n# Weekly status\n\nSteps here.\n");
    }

    [Fact]
    public void ToSkillMarkdown_omits_allowed_tools_when_empty()
    {
        SkillDocument.ToSkillMarkdown(Sample()).Should().NotContain("allowed-tools");
    }

    [Fact]
    public void ToSkillMarkdown_includes_allowed_tools_as_a_quoted_block_sequence()
    {
        var md = SkillDocument.ToSkillMarkdown(Sample() with { AllowedTools = ["Read", "Edit"] });

        md.Should().Contain("allowed-tools:\n  - \"Read\"\n  - \"Edit\"\n");
    }

    [Fact]
    public void ToSkillMarkdown_only_serializes_the_claude_contract_fields_not_interface_metadata()
    {
        // Brand colour / display name / short description are Fravashi interface fields - never in the frontmatter.
        var md = SkillDocument.ToSkillMarkdown(Sample());

        md.Should().NotContain("brand_color");
        md.Should().NotContain("#7C4DFF");
        md.Should().NotContain("display_name");
    }
}
