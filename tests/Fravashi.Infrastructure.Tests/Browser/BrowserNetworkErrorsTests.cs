using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Network;
using Fravashi.Infrastructure.Browser;
using Microsoft.Playwright;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Browser;

/// <summary>
/// Backlog 233: the agent browser tells a navigation that failed on the NETWORK from a page that answered, over
/// Playwright's own texts as they were MEASURED in the studio container on 2026-10-07 (Fixtures/playwright-network-errors.json),
/// never over texts written here. Opening a page is a read and safe to repeat; a click that led to a page that could not
/// load may have submitted something, so its outcome is unknown.
/// </summary>
public sealed class BrowserNetworkErrorsTests
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("name_not_resolved", NetworkFailureKind.NameNotResolved, "fravashi-netfault.invalid", "name not resolved")]
    [InlineData("closed_port", NetworkFailureKind.Refused, "127.0.0.1", "connection refused")]
    [InlineData("reset_or_closed_without_answer", NetworkFailureKind.LeftWithNoAnswer, "127.0.0.1", "no answer came back")]
    [InlineData("internet_disconnected", NetworkFailureKind.Unreachable, "example.com", "no network route")]
    public void A_failed_open_measured_in_the_container_is_the_network_in_words_and_safe_to_repeat(
        string fixture, NetworkFailureKind kind, string host, string words)
    {
        var measured = Case(fixture);
        measured.GetProperty("exception").GetString().Should().Be("Microsoft.Playwright.PlaywrightException");

        var network = BrowserNetworkErrors.OfOpen(
            new PlaywrightException(measured.GetProperty("message").GetString()!), UrlIn(measured), OpenTimeout);

        network.Should().NotBeNull();
        network!.Failure.Kind.Should().Be(kind);
        network.Host.Should().Be(host);
        network.RetrySafe.Should().BeTrue("opening a page is a read");
        network.OutcomeUnknown.Should().BeFalse();
        network.Sentence.Should().StartWith($"Could not reach {host} ({words}")
            .And.Contain("This is the network, not the page")
            .And.Contain("safe to try again in a minute")
            .And.Contain("browser is still open")
            .And.NotContain("net::", "Chromium's code is for the log, the agent reads words");

        // The failed request's own text names the same failure.
        BrowserNetworkErrors.Classify(measured.GetProperty("requestFailure").GetString()).Kind.Should().Be(kind);
    }

    [Theory]
    [InlineData("black_holed")]
    [InlineData("connected_no_answer")]
    public void A_navigation_timeout_is_said_as_the_network_or_a_slow_site(string fixture)
    {
        var measured = Case(fixture);
        measured.GetProperty("exception").GetString().Should().Be("System.TimeoutException");

        var network = BrowserNetworkErrors.OfOpen(
            new TimeoutException(measured.GetProperty("message").GetString()), UrlIn(measured), OpenTimeout);

        network.Should().NotBeNull();
        network!.Failure.Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer);
        network.TimedOutAfter.Should().Be(OpenTimeout);
        network.RetrySafe.Should().BeTrue();
        network.Sentence.Should().Contain("did not finish loading in 30 seconds")
            .And.Contain("the network or a slow site")
            .And.Contain("not an error the page answered with");
    }

    [Fact]
    public void The_late_reset_measured_after_an_abandoned_navigation_is_a_network_text() =>
        BrowserNetworkErrors.Classify("net::ERR_CONNECTION_RESET").Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer);

    [Fact]
    public void A_click_that_submitted_a_form_to_a_name_that_does_not_resolve_has_an_unknown_outcome()
    {
        var measured = Case("form_submit_to_unresolvable_name");

        var network = BrowserNetworkErrors.OfAct(
            "click",
            "http://fravashi-netfault.invalid/submit",
            measured.GetProperty("requestFailure").GetString()!.Split(' ')[0],
            measured.GetProperty("urlAfter").GetString());

        network.Should().NotBeNull();
        network!.Failure.Kind.Should().Be(NetworkFailureKind.NameNotResolved);
        network.Host.Should().Be("fravashi-netfault.invalid");
        network.OutcomeUnknown.Should().BeTrue("the click also ran the page's own scripts");
        network.RetrySafe.Should().BeFalse();
        network.Sentence.Should().StartWith("The click was made")
            .And.Contain("could not reach fravashi-netfault.invalid (name not resolved)")
            .And.Contain("is unknown, so do not repeat it yet")
            .And.Contain("look first");
    }

    [Fact]
    public void A_late_failure_from_an_abandoned_navigation_does_not_make_a_click_look_failed() =>
        BrowserNetworkErrors.OfAct("click", "http://192.0.2.1/", "net::ERR_CONNECTION_RESET", "https://example.com/next")
            .Should().BeNull("the page the click led to loaded");

    [Theory]
    [InlineData("net::ERR_ABORTED")]
    [InlineData("net::ERR_BLOCKED_BY_CLIENT")]
    [InlineData("net::ERR_CERT_AUTHORITY_INVALID at https://self-signed.example/")]
    [InlineData("Timeout 10000ms exceeded.\nCall log:\n  - waiting for getByRole(Button, new() { Name = \"Save\" })")]
    [InlineData("Element is not attached to the DOM")]
    [InlineData("")]
    public void What_is_not_about_the_network_keeps_the_browsers_own_words(string text)
    {
        BrowserNetworkErrors.Classify(text).IsNetwork.Should().BeFalse();
        BrowserNetworkErrors.OfOpen(new PlaywrightException(text), "https://example.com/", OpenTimeout).Should().BeNull();
        BrowserNetworkErrors.OfAct("click", "https://example.com/", text, "chrome-error://chromewebdata/").Should().BeNull();
    }

    private static JsonElement Case(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Browser", "Fixtures", "playwright-network-errors.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("case").GetString() == name)
            .Clone();
    }

    /// <summary>The address the measured navigation went to, as Playwright's call log names it.</summary>
    private static string UrlIn(JsonElement measured)
    {
        var message = measured.GetProperty("message").GetString()!;
        var start = message.IndexOf("navigating to \"", StringComparison.Ordinal) + "navigating to \"".Length;
        return message[start..message.IndexOf('"', start)];
    }
}
