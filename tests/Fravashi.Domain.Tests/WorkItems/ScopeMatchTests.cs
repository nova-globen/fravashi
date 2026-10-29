using AwesomeAssertions;
using Fravashi.Domain.WorkItems.AgentLayer;
using Xunit;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>
/// Backlog 85: whether a change is inside the area an item declares it may touch. The semantics are the
/// ones this product already documents for search globs, so an author learns one glob language, and the
/// cases that decide it are here.
/// </summary>
public class ScopeMatchTests
{
    [Theory]
    [InlineData("src/**", "src/a/b.cs", true)]
    [InlineData("src/**", "tests/a.cs", false)]
    [InlineData("src/*.cs", "src/a.cs", true)]
    [InlineData("src/*.cs", "src/a/b.cs", false)]
    [InlineData("src/**/*.cs", "src/a/b/c.cs", true)]
    [InlineData("src/**/*.cs", "src/a.cs", true)]
    [InlineData("a?c.cs", "abc.cs", true)]
    [InlineData("a?c.cs", "abbc.cs", false)]
    public void A_star_stops_at_a_separator_and_a_double_star_crosses_it(
        string pattern, string path, bool matches) =>
        ScopeMatch.Matches(pattern, path).Should().Be(matches);

    [Theory]
    [InlineData("*.cs", "src/deep/a.cs", true)]
    [InlineData("*.cs", "src/deep/a.md", false)]
    public void A_pattern_with_no_separator_is_about_the_files_name(
        string pattern, string path, bool matches) =>
        ScopeMatch.Matches(pattern, path).Should().Be(matches);

    [Theory]
    [InlineData("tests/", "tests/a/b.cs", true)]
    [InlineData("tests/", "src/tests/a.cs", true)]
    [InlineData("tests/", "src/a.cs", false)]
    public void A_pattern_ending_in_a_slash_is_a_directory_anywhere_in_the_path(
        string pattern, string path, bool matches) =>
        ScopeMatch.Matches(pattern, path).Should().Be(matches);

    [Fact]
    public void A_pattern_and_a_path_agree_whichever_slash_was_typed()
    {
        // The pattern travels between machines; a scope that opened when the file arrived from the other
        // platform would be a scope that silently stopped meaning what its author wrote.
        ScopeMatch.Matches("src\\**", "src/a/b.cs").Should().BeTrue();
        ScopeMatch.Matches("src/**", "src\\a\\b.cs").Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_pattern_matches_nothing(string? pattern) =>
        ScopeMatch.Matches(pattern, "src/a.cs").Should().BeFalse();

    // ---- whole scope sets ------------------------------------------------------------------------

    [Fact]
    public void Nothing_declared_forbids_nothing()
    {
        // An item that has not said where the work happens has not ruled anywhere out either.
        ScopeMatch.IsAllowed([], "anywhere/at/all.cs").Should().BeTrue();
    }

    [Fact]
    public void An_allowed_pattern_narrows_everything_else_out()
    {
        ScopeMatch.IsAllowed([Allow("src/**")], "src/a.cs").Should().BeTrue();
        ScopeMatch.IsAllowed([Allow("src/**")], "tests/a.cs").Should().BeFalse();
    }

    [Fact]
    public void A_forbidden_pattern_wins_over_an_allowed_one()
    {
        // The narrower statement is the deliberate one.
        var scope = new[] { Allow("src/**"), Forbid("src/secrets/**") };

        ScopeMatch.IsAllowed(scope, "src/a.cs").Should().BeTrue();
        ScopeMatch.IsAllowed(scope, "src/secrets/key.cs").Should().BeFalse();
    }

    [Fact]
    public void A_scope_of_only_forbidden_patterns_allows_everything_else()
    {
        var scope = new[] { Forbid("src/secrets/**") };

        ScopeMatch.IsAllowed(scope, "src/a.cs").Should().BeTrue();
        ScopeMatch.IsAllowed(scope, "src/secrets/key.cs").Should().BeFalse();
    }

    private static ScopePattern Allow(string pattern) =>
        new(AgentLayerEntry.NewId(), ScopeMode.Allowed, pattern);

    private static ScopePattern Forbid(string pattern) =>
        new(AgentLayerEntry.NewId(), ScopeMode.Forbidden, pattern);
}
