using Fravashi.Domain.Framework;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Domain.Tests;

/// <summary>The skill field rules the form, unit tests, and bUnit tests all defer to.</summary>
public class SkillValidationTests
{
    private static SkillDefinition Valid() => new()
    {
        Name = "weekly-status-update",
        DisplayName = "Weekly status",
        ShortDescription = "Drafts a concise weekly status update", // 37 chars
        Description = "Use when the user asks for a weekly status summary.",
        BrandColor = "#7C4DFF",
        DefaultPrompt = "Use $skill-name to draft a concise weekly status update.",
        Content = "# body",
    };

    [Fact]
    public void A_fully_populated_skill_is_valid() => SkillValidation.IsValid(Valid()).Should().BeTrue();

    [Theory]
    [InlineData("weekly-status", true)]
    [InlineData("skill1", true)]
    [InlineData("Weekly-Status", false)]   // uppercase
    [InlineData("weekly_status", false)]   // underscore
    [InlineData("weekly--status", false)]  // double hyphen
    [InlineData("-weekly", false)]         // leading hyphen
    [InlineData("weekly ", false)]         // space
    [InlineData("", false)]
    public void IsKebabCase_enforces_the_name_shape(string name, bool expected) =>
        SkillValidation.IsKebabCase(name).Should().Be(expected);

    [Theory]
    [InlineData("#abc", true)]
    [InlineData("#7C4DFF", true)]
    [InlineData("7C4DFF", false)]
    [InlineData("#7C4DF", false)]
    [InlineData("#zzzzzz", false)]
    public void IsHexColor_enforces_hex(string value, bool expected) =>
        SkillValidation.IsHexColor(value).Should().Be(expected);

    [Theory]
    [InlineData(24, false)] // below 25
    [InlineData(25, true)]
    [InlineData(64, true)]
    [InlineData(65, false)] // above 64
    public void Short_description_must_be_25_to_64_chars(int length, bool valid)
    {
        var skill = Valid() with { ShortDescription = new string('a', length) };
        var hasError = SkillValidation.Validate(skill).Any(e => e.Field == "short_description");
        hasError.Should().Be(!valid);
    }

    [Fact]
    public void Default_prompt_must_contain_the_skill_name_placeholder()
    {
        var skill = Valid() with { DefaultPrompt = "Draft a status update without the token." };
        SkillValidation.Validate(skill).Should().Contain(e => e.Field == "default_prompt");
    }

    [Fact]
    public void Default_prompt_with_the_placeholder_passes()
    {
        var skill = Valid() with { DefaultPrompt = "Run $skill-name now." };
        SkillValidation.Validate(skill).Should().NotContain(e => e.Field == "default_prompt");
    }

    [Fact]
    public void An_empty_description_is_rejected_as_load_bearing()
    {
        var skill = Valid() with { Description = "  " };
        SkillValidation.Validate(skill).Should().Contain(e => e.Field == "description");
    }

    [Fact]
    public void Missing_display_name_content_and_color_are_each_reported()
    {
        var skill = Valid() with { DisplayName = "", Content = "", BrandColor = "" };
        var fields = SkillValidation.Validate(skill).Select(e => e.Field).ToHashSet();
        fields.Should().Contain(["display_name", "content", "brand_color"]);
    }
}
