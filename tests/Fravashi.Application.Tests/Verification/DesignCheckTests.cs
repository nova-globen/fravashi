using AwesomeAssertions;
using Fravashi.Application.Verification;
using Xunit;

namespace Fravashi.Application.Tests.Verification;

/// <summary>Backlog 223: how a design check's pictures are named, so build and prototype pair up wherever they are kept.</summary>
public sealed class DesignCheckTests
{
    [Fact]
    public void A_key_names_the_screen_the_viewport_and_the_phase_in_the_designs_order()
    {
        DesignCheckScreenshot.KeyFor(0, "wide", 1, "add-dialog opened by Add person")
            .Should().Be("screen-0.wide.01-add-dialog-opened-by-add-person");
        DesignCheckScreenshot.KeyFor(2, null, 0, DesignExpectations.AtRest).Should().Be("screen-2.base.00-at-rest");
        DesignCheckScreenshot.KeyFor(1, "narrow", 3, "/screens/1/children/2/children/1 chosen by Activity")
            .Should().Be("screen-1.narrow.03-screens-1-children-2-children-1-chosen-by-activity");
    }

    [Fact]
    public void A_key_is_safe_for_a_file_name_and_bounded()
    {
        var key = DesignCheckScreenshot.KeyFor(0, "wide", 4, new string('x', 200) + " ../ \\ : * ? \"<>| é");

        key.Should().MatchRegex("^[a-z0-9.-]+$");
        key.Split('.').Last().Length.Should().BeLessThanOrEqualTo(3 + DesignCheckScreenshot.MaxSlug);
        DesignCheckScreenshot.KeyFor(0, "wide", 5, "!!!").Should().Be("screen-0.wide.05-phase");
    }

    [Fact]
    public void The_two_sides_of_one_moment_differ_only_in_their_last_word()
    {
        var shot = new DesignCheckScreenshot("screen-0.wide.00-at-rest", 0, DesignExpectations.AtRest, "wide", new byte[] { 1 });

        shot.FileName(DesignCheckSideKind.Build).Should().Be("screen-0.wide.00-at-rest.build.png");
        shot.FileName(DesignCheckSideKind.Prototype).Should().Be("screen-0.wide.00-at-rest.prototype.png");
    }
}
