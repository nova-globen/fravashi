using System.Text.RegularExpressions;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>
/// Backlog 208: the guide is the thing that teaches the behaviour vocabulary <see cref="MockupCheckTool"/>'s
/// errors cannot, so an example that does not itself pass <see cref="MockupReader"/> would teach the same wrong
/// lesson a second time. This reads every fenced <c>json</c> block out of <see cref="MockupGuide.Text"/>, the same
/// way a person reading the markdown would find them, and checks each one.
/// </summary>
public class MockupGuideTests
{
    private static readonly Regex FencedJson = new(
        """```json\r?\n(?<body>.*?)\r?\n```""", RegexOptions.Singleline | RegexOptions.Compiled);

    private static IReadOnlyList<string> Examples() =>
        [.. FencedJson.Matches(MockupGuide.Text).Select(m => m.Groups["body"].Value)];

    [Fact]
    public void The_guide_has_at_least_one_example_per_behaviour_concept()
    {
        // states, on, at, a11y, dialog: #208's acceptance names exactly these five; #290 adds the link.
        Examples().Should().HaveCount(6);
    }

    [Fact]
    public void The_guides_link_example_is_a_valid_not_found_page_whose_link_is_a_link()
    {
        // #290's acceptance: the example the guide teaches a link with passes the reader mockup_check runs.
        var example = Examples().Single(e => e.Contains("\"type\": \"link\"", StringComparison.Ordinal));
        var read = MockupReader.Read(example);

        read.Errors.Should().BeEmpty();
        var link = read.Document!.Screens[0].Children!.Single(n => n.Type == MockupComponent.Link);
        link.Text.Should().Be("Go to the start page");
        link.Href.Should().Be("/");
        MockupGuide.Text.Should().Contain("never as a `button`");
    }

    [Fact]
    public void Every_example_in_the_guide_passes_the_reader_with_no_errors()
    {
        foreach (var example in Examples())
        {
            var result = MockupReader.Read(example);
            result.Errors.Should().BeEmpty($"this example should be valid:\n{example}");
        }
    }

    [Theory]
    [InlineData("\"states\"")]
    [InlineData("\"on\"")]
    [InlineData("\"go\"")]
    [InlineData("\"open\"")]
    [InlineData("\"close\"")]
    [InlineData("\"set\"")]
    [InlineData("\"at\"")]
    [InlineData("\"a11y\"")]
    [InlineData("\"dialog\"")]
    public void The_guides_examples_use_every_name_in_the_behaviour_vocabulary(string name) =>
        Examples().Should().Contain(e => e.Contains(name, StringComparison.Ordinal), $"no example uses {name}");
}
