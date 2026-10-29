using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Fravashi.Domain.Telemetry;
using Xunit;

namespace Fravashi.Domain.Tests.Telemetry;

/// <summary>
/// The standing proof for backlog 74 decision (c): <b>a recorded product event cannot carry a prompt,
/// a path, a command, a transcript or anything else someone typed</b>.
/// <para>
/// The point of testing it structurally rather than by example is that "we only record safe fields" is
/// a promise a future field would quietly break, and nothing would report it. Here it is the TYPE that
/// makes the promise: the payload has no <see cref="string"/>-typed member at all, and the one
/// string-SHAPED member is a <see cref="WorkspaceHash"/> whose only public factory is one-way. Add a
/// <c>string Detail</c> to <see cref="ProductEvent"/> and this file goes red before the field ever
/// reaches a database.
/// </para>
/// </summary>
public class ProductEventSchemaTests
{
    /// <summary>
    /// Everything an event is allowed to be made of. Each entry is on the list for a reason that holds
    /// for every possible value of it: an enum can only be a name from a closed set, a timestamp and a
    /// number carry no content, and a <see cref="WorkspaceHash"/> is proved below to be sixteen hex
    /// characters whatever went in.
    /// </summary>
    private static bool IsAllowed(Type type) =>
        type.IsEnum
        || type == typeof(DateTimeOffset)
        || type == typeof(long?)
        || type == typeof(long)
        || type == typeof(WorkspaceHash);

    [Fact]
    public void The_payload_has_no_field_free_text_could_be_written_into()
    {
        var members = typeof(ProductEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            // EqualityContract is a compiler-generated record member, not part of the payload.
            .Where(p => p.Name != "EqualityContract")
            .ToList();

        members.Should().NotBeEmpty("the schema must actually have fields to be a schema");

        foreach (var member in members)
        {
            IsAllowed(member.PropertyType).Should().BeTrue(
                "{0} is typed {1}, which is not one of the shapes a recorded event may carry; "
                + "a recorded event must never be able to hold a prompt, a path or a transcript",
                member.Name,
                member.PropertyType.Name);
        }

        // And specifically: nothing string-typed, which is the shape everything sensitive arrives in.
        members.Should().NotContain(p => p.PropertyType == typeof(string));
    }

    [Fact]
    public void The_constructor_takes_nothing_text_could_be_written_into_either()
    {
        // A property check alone could be satisfied by a constructor parameter that accepts text and
        // stores a derived value. The public construction surface is held to the same rule.
        var parameters = typeof(ProductEvent)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .ToList();

        parameters.Should().NotBeEmpty();
        parameters.Should().OnlyContain(p => IsAllowed(p.ParameterType));
    }

    [Theory]
    // The things that must never reach a recorded event, fed straight into the one field that takes a
    // string at all. Each comes out as sixteen hex characters with nothing of the input left in it.
    [InlineData("C:/Users/someone/Secret Project/src/keys.txt")]
    [InlineData("/home/me/work/acme-takeover/.env")]
    [InlineData("please refactor the billing module and do not tell anyone about project raven")]
    [InlineData("sk-" + "ant-api03-Fake0123456789Fake0123456789Fake0123456789FakeAA")]
    [InlineData("git commit -m \"fix the thing\"")]
    public void A_workspace_hash_is_one_way_and_keeps_nothing_of_what_went_in(string sensitive)
    {
        var hash = WorkspaceHash.Of(sensitive);

        hash.HasValue.Should().BeTrue();
        Regex.IsMatch(hash.Value, "^[0-9a-f]{16}$").Should().BeTrue(
            "a workspace hash is always exactly sixteen lower-case hex characters, whatever went in");

        // Nothing recognisable survives: no fragment of the input of any useful length is in the hash.
        foreach (var fragment in sensitive.Split(['/', '\\', ' ', '-', '.'], StringSplitOptions.RemoveEmptyEntries)
                     .Where(f => f.Length >= 4))
        {
            hash.Value.Should().NotContain(fragment, "no part of the input may survive the hash");
        }
    }

    [Fact]
    public void The_same_workspace_always_hashes_the_same_way_and_different_ones_do_not()
    {
        // What makes "did this keep failing in one workspace" answerable without naming the workspace.
        WorkspaceHash.Of("workspace-a").Should().Be(WorkspaceHash.Of("workspace-a"));
        WorkspaceHash.Of("workspace-a").Should().NotBe(WorkspaceHash.Of("workspace-b"));
    }

    [Fact]
    public void A_blank_workspace_is_no_workspace_rather_than_a_hash_of_nothing()
    {
        WorkspaceHash.Of(null).HasValue.Should().BeFalse();
        WorkspaceHash.Of("   ").HasValue.Should().BeFalse();
        WorkspaceHash.Of(null).Value.Should().BeEmpty();
    }

    [Theory]
    // Reading a row back cannot resurrect text either: a stored value that is not already a hash reads
    // as no workspace, so a hand-edited database cannot put a path in front of whoever opens a bundle.
    [InlineData("/home/me/work")]
    [InlineData("0123456789abcdefg")]      // too long
    [InlineData("0123456789abcde")]        // too short
    [InlineData("0123456789ABCDEF")]       // upper case is not the shape Of() produces
    [InlineData("0123456789abcdeZ")]       // right length, wrong alphabet
    [InlineData("")]
    [InlineData(null)]
    public void Reading_back_anything_that_is_not_a_hash_yields_no_workspace(string? stored)
    {
        WorkspaceHash.Parse(stored).HasValue.Should().BeFalse();
    }

    [Fact]
    public void A_real_hash_round_trips_through_storage()
    {
        var hash = WorkspaceHash.Of("ws-1");

        WorkspaceHash.Parse(hash.Value).Should().Be(hash);
    }

    [Fact]
    public void Every_event_kind_the_item_asked_for_is_declared_including_the_two_with_no_producer()
    {
        // Backlog 74 decision (b): verification flakiness and time-to-a-verified-change have no producer
        // until roadmap Phase E, and are declared anyway so the reliability targets can name the event
        // that will measure them. This test is what keeps someone from "tidying away" an unused kind.
        Enum.GetNames<ProductEventKind>().Should().BeEquivalentTo(
        [
            nameof(ProductEventKind.OnboardingCompleted),
            nameof(ProductEventKind.SessionStart),
            nameof(ProductEventKind.ToolDiscoveryFailed),
            nameof(ProductEventKind.ApprovalResolved),
            nameof(ProductEventKind.TurnRetried),
            nameof(ProductEventKind.TurnUntyped),
            nameof(ProductEventKind.SessionAborted),
            nameof(ProductEventKind.VerificationFlaked),
            nameof(ProductEventKind.VerifiedChangeReached),
        ]);
    }

    [Fact]
    public void An_unset_outcome_is_the_neutral_one()
    {
        // Same reasoning as ToolApprovalDecidedBy (backlog 114): the default must never read as a claim.
        ((int)ProductEventOutcome.Unspecified).Should().Be(0);
        default(ProductEventOutcome).Should().Be(ProductEventOutcome.Unspecified);
    }

    [Fact]
    public void The_factories_put_the_measurement_where_the_kind_says_it_is()
    {
        var at = new DateTimeOffset(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

        ProductEvent.OnboardingCompleted(at, TimeSpan.FromMinutes(4))
            .Should().BeEquivalentTo(new
            {
                Kind = ProductEventKind.OnboardingCompleted,
                Outcome = ProductEventOutcome.Succeeded,
                DurationMs = 240_000L,
                Count = (long?)null,
            });

        ProductEvent.ApprovalResolved(at, "ws-1", ProductEventOutcome.DeniedByStudio, TimeSpan.FromSeconds(90))
            .Should().BeEquivalentTo(new
            {
                Kind = ProductEventKind.ApprovalResolved,
                Outcome = ProductEventOutcome.DeniedByStudio,
                DurationMs = 90_000L,
            });

        ProductEvent.TurnRetried(at, "ws-1", 5)
            .Should().BeEquivalentTo(new
            {
                Kind = ProductEventKind.TurnRetried,
                Count = 5L,
                DurationMs = (long?)null,
            });

        ProductEvent.TurnUntyped(at, "ws-1")
            .Should().BeEquivalentTo(new
            {
                Kind = ProductEventKind.TurnUntyped,
                Count = 1L,
                DurationMs = (long?)null,
            });

        // A negative duration is nonsense (a clock that moved), and nonsense is recorded as "unknown"
        // rather than as a measurement.
        ProductEvent.SessionStarted(at, "ws-1", TimeSpan.FromSeconds(-3)).DurationMs.Should().BeNull();
    }
}
