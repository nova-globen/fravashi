using AwesomeAssertions;
using Fravashi.Launcher;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// What the studio window says when it cannot reach the studio (backlog 15, then backlog 122).
/// <para>
/// The second item exists because of a measured failure. On 2026-09-20 v0.9.23 could not build its
/// service provider; the container was restarted 1501 times in 35 minutes under an unless-stopped
/// policy, and this page told the owner it "may still be starting up" for seventeen minutes. The reason,
/// a named missing service and a stack trace, was in the container log the entire time. The product knew
/// and did not say, and the owner's first question was "why".
/// </para>
/// </summary>
public class StudioUnavailableMessageTests
{
    /// <summary>The real thing, trimmed: what v0.9.23 actually printed before dying.</summary>
    private const string RealCrash = """
        info: Microsoft.Hosting.Lifetime[0]
              Starting up
        Unhandled exception. System.InvalidOperationException: No service for type 'Fravashi.Infrastructure.Git.GitCliService' has been registered.
           at Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService(IServiceProvider provider, Type serviceType)
           at Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService[T](IServiceProvider provider)
           at Fravashi.Infrastructure.Sessions.SessionsServiceCollectionExtensions.<>c.<AddAgentSessions>b__0_26(IServiceProvider sp)
           at Microsoft.Extensions.DependencyInjection.ServiceLookup.CallSiteRuntimeResolver.VisitRootCache(ServiceCallSite callSite, RuntimeResolverContext context)
           at Program.<Main>$(String[] args)
        """;

    [Fact]
    public void A_container_that_is_dying_is_not_described_as_still_starting_up()
    {
        var message = StudioUnavailableMessage.For(ProductRunState.Running, RealCrash);

        message.Headline.Should().Be("Fravashi is not starting");
        message.Detail.Should().NotContain("may still be starting up",
            "that sentence is true for a few seconds and a lie after that");
        message.Detail.Should().Contain("starts and then stops again");
    }

    [Fact]
    public void It_shows_the_line_that_names_the_fault()
    {
        var message = StudioUnavailableMessage.For(ProductRunState.Running, RealCrash);

        message.Detail.Should().Contain("No service for type")
            .And.Contain("GitCliService",
                "the owner's first question is why, and this is the answer the product already had");
    }

    [Fact]
    public void It_starts_at_the_fault_rather_than_at_the_deepest_frame()
    {
        // A .NET stack trace is mostly framework frames and the sentence that explains the fault is at
        // the TOP. Showing the last few lines would show CallSiteRuntimeResolver and say nothing.
        var message = StudioUnavailableMessage.For(ProductRunState.Running, RealCrash);

        message.Detail.Should().NotContain("info: Microsoft.Hosting.Lifetime",
            "the lines before the fault are noise on a page this small");
        var faultIndex = message.Detail.IndexOf("Unhandled exception", System.StringComparison.Ordinal);
        var frameIndex = message.Detail.IndexOf("CallSiteRuntimeResolver", System.StringComparison.Ordinal);
        faultIndex.Should().BeGreaterThan(-1);
        if (frameIndex >= 0)
        {
            faultIndex.Should().BeLessThan(frameIndex);
        }
    }

    [Fact]
    public void A_container_that_is_merely_slow_still_gets_the_patient_message()
    {
        // Starting up IS the usual case, and a page that cried crash on every cold start would be worse
        // than the one this replaces.
        var message = StudioUnavailableMessage.For(
            ProductRunState.Running,
            "info: Microsoft.Hosting.Lifetime[14]\n      Now listening on: http://[::]:8080");

        message.Headline.Should().Be("Fravashi did not answer");
        message.Detail.Should().Contain("may still be starting up");
    }

    [Fact]
    public void No_log_at_all_changes_nothing()
    {
        // The logs can be unreadable: no engine, no container, a runtime that is not answering. That must
        // not turn into a worse message than the one we had before.
        StudioUnavailableMessage.For(ProductRunState.Running, null)
            .Should().Be(StudioUnavailableMessage.For(ProductRunState.Running));

        StudioUnavailableMessage.For(ProductRunState.Running, string.Empty)
            .Headline.Should().Be("Fravashi did not answer");
    }

    [Theory]
    [InlineData(ProductRunState.NotCreated)]
    [InlineData(ProductRunState.NeverStarted)]
    [InlineData(ProductRunState.RuntimeUnavailable)]
    public void Every_other_state_is_unchanged_even_when_a_log_is_supplied(ProductRunState state)
    {
        // These three say something true and actionable whatever a log holds: there is no container, or
        // there is no engine to ask. A crash excerpt would be answering a question nobody asked.
        StudioUnavailableMessage.For(state, RealCrash)
            .Should().Be(StudioUnavailableMessage.For(state));
    }

    [Fact]
    public void A_stopped_container_whose_last_words_were_a_crash_says_so()
    {
        // Since the restart policy became bounded (backlog 122), this is where a studio that cannot start
        // ENDS UP: the engine uses its attempts and leaves the container stopped. "Starting it again takes
        // a few seconds" would send the user round the same loop with no idea why.
        var message = StudioUnavailableMessage.For(ProductRunState.Stopped, RealCrash);

        message.Headline.Should().Be("Fravashi is stopped, and it crashed");
        message.Detail.Should().Contain("GitCliService");
        message.ActionLabel.Should().Be("Start Fravashi", "starting it is still the only thing to offer");
    }

    [Fact]
    public void A_stopped_container_is_never_told_why_it_stopped()
    {
        // `fravashi stop` on a container that had crashed earlier lands in exactly this state, and the page
        // cannot tell that from the engine giving up without another round trip. So it says what is true
        // of both (it is stopped, and the last thing it printed was a crash) and claims neither cause.
        var message = StudioUnavailableMessage.For(ProductRunState.Stopped, RealCrash);

        message.Detail.Should().NotContain("gave up").And.NotContain("times");
    }

    [Fact]
    public void A_container_stopped_cleanly_still_gets_the_ordinary_message()
    {
        var message = StudioUnavailableMessage.For(
            ProductRunState.Stopped,
            "info: Microsoft.Hosting.Lifetime[0]\n      Application is shutting down...");

        message.Should().Be(StudioUnavailableMessage.For(ProductRunState.Stopped));
        message.Headline.Should().Be("Fravashi is stopped");
    }
}
