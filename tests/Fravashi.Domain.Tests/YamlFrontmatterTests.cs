using Fravashi.Domain.Framework;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Domain.Tests;

/// <summary>
/// The locked YAML frontmatter contract (ADR 0005 / D03-6): every string value double-quoted, every key
/// unquoted, byte-stable. These assert the quoting on adversarial inputs - colons, quotes, backslashes,
/// newlines, leading spaces, hashes - the cases a naive "quote only when needed" serializer would get wrong.
/// </summary>
public class YamlFrontmatterTests
{
    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("has: colon", "\"has: colon\"")]
    [InlineData("# hash lead", "\"# hash lead\"")]
    [InlineData("  leading spaces", "\"  leading spaces\"")]
    [InlineData("quote \" inside", "\"quote \\\" inside\"")]
    [InlineData("back\\slash", "\"back\\\\slash\"")]
    [InlineData("tab\tsep", "\"tab\\tsep\"")]
    [InlineData("", "\"\"")]
    public void QuoteString_double_quotes_and_escapes(string input, string expected)
    {
        YamlFrontmatter.QuoteString(input).Should().Be(expected);
    }

    [Fact]
    public void QuoteString_escapes_newlines_and_carriage_returns()
    {
        YamlFrontmatter.QuoteString("line1\nline2\r\nline3")
            .Should().Be("\"line1\\nline2\\r\\nline3\"");
    }

    [Fact]
    public void Serialize_emits_unquoted_keys_and_quoted_scalar_values()
    {
        var yaml = YamlFrontmatter.Serialize(
        [
            new("name", new YamlValue.Scalar("my-skill")),
            new("description", new YamlValue.Scalar("Use: when needed")),
        ]);

        yaml.Should().Be("name: \"my-skill\"\ndescription: \"Use: when needed\"\n");
    }

    [Fact]
    public void Serialize_emits_a_block_sequence_with_quoted_items()
    {
        var yaml = YamlFrontmatter.Serialize(
        [
            new("allowed-tools", new YamlValue.List(["Read", "Edit: sub"])),
        ]);

        yaml.Should().Be("allowed-tools:\n  - \"Read\"\n  - \"Edit: sub\"\n");
    }
}
