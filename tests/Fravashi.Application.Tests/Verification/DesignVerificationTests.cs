using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Verification;
using Fravashi.Domain.WorkItems.AgentLayer;
using Xunit;

namespace Fravashi.Application.Tests.Verification;

/// <summary>Backlog 224: which page of the running application shows which screen of the design.</summary>
public sealed class DesignVerificationTests
{
    private static ScenarioStep Step(string command) => new("s", "Run", "It works", Command: command);

    private static MockupNode Screen(string? id, string? text) => new() { Type = MockupComponent.Window, Id = id, Text = text };

    [Fact]
    public void Every_spelling_of_the_address_with_its_path_is_a_reached_page_each_once()
    {
        var reached = DesignVerification.ReachedPaths(
        [
            Step("curl -fsS \"$VERIFY_APP_URL/\""),
            Step("curl ${VERIFY_APP_URL}/people?sort=name#top && curl $VERIFY_APP_URL/people?sort=name"),
            Step("node check.js $VERIFY_APP_URL"),
            Step("echo $VERIFY_APP_URL_OTHER/nope"),
            new ScenarioStep("p", "Look", "It looks right"),
        ]);

        reached.Should().Equal(["/", "/people?sort=name"]);
    }

    [Fact]
    public void A_screen_is_the_page_whose_last_segment_folds_to_its_id_or_title_and_the_first_falls_back_to_the_root()
    {
        string[] reached = ["/", "/team/Team-Directory/", "/people/profile?x=1"];

        DesignVerification.PathFor(Screen("directory", "Team directory"), 0, reached).Should().Be("/team/Team-Directory/");
        DesignVerification.PathFor(Screen("profile", "Profile"), 1, reached).Should().Be("/people/profile?x=1");
        DesignVerification.PathFor(Screen("home", null), 0, reached).Should().Be("/");
        DesignVerification.PathFor(Screen("settings", "Settings"), 2, reached).Should().BeNull("a later screen nobody reaches is NOT RUN");
    }

    [Fact]
    public void Each_kind_is_named_as_ADR_0030_decision_6_names_it()
    {
        DesignVerification.KindLabel(DesignFindingKind.DifferentName).Should().Be("DIFFERENT NAME");
        DesignVerification.KindLabel(DesignFindingKind.NotRun).Should().Be("NOT RUN");
        DesignVerification.KindLabel(DesignFindingKind.OutOfOrder).Should().Be("OUT OF ORDER");
    }
}
