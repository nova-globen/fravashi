using AwesomeAssertions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 103: the store carries the RAW subject and the display copy derives from it on read,
/// never the reverse. <see cref="AgentToolSubject.ForStorage"/> is the persistence-side bound
/// (honest and large), <see cref="AgentToolSubject.Shorten"/> stays the render-time one, and the
/// two markers must never be mistakable for each other: a stored row that reads
/// "...[91 more]..." where the command belongs is exactly the defect this file exists to keep out.
/// </summary>
public class AgentToolSubjectTests
{
    [Fact]
    public void A_500_char_command_is_stored_verbatim()
    {
        var raw = "run --flag " + new string('x', 489);

        AgentToolSubject.ForStorage(raw).Should().Be(raw);
    }

    [Fact]
    public void Storage_keeps_newlines_and_edges_because_raw_means_raw()
    {
        // Shorten collapses a value to one line for the prompt; the STORED copy must not, because it
        // is the record of what the agent actually ran.
        var raw = "  echo start\nrm -rf /tmp/scratch\necho done  ";

        AgentToolSubject.ForStorage(raw).Should().Be(raw);
    }

    [Fact]
    public void A_value_over_the_storage_cap_keeps_head_and_tail_around_a_marker_naming_the_cap()
    {
        var raw = "HEAD-" + new string('a', 40_000) + new string('b', 40_000) + "-TAIL";

        var stored = AgentToolSubject.ForStorage(raw);

        stored.Length.Should().BeLessThanOrEqualTo(AgentToolSubject.MaxStoredLength);
        stored.Should().StartWith("HEAD-" + new string('a', 64));
        stored.Should().EndWith(new string('b', 64) + "-TAIL");
        stored.Should().Contain("storage cap", "the stored truncation must say the STORE cut it");
        stored.Should().NotContain("more]", "the storage marker must be distinct from the display elision");
    }

    [Fact]
    public void The_best_subject_of_an_input_is_the_raw_value_not_the_display_elision()
    {
        var raw = new string('x', 500);

        var subject = AgentToolSubject.From(new Dictionary<string, string> { ["command"] = raw });

        subject.Should().Be(raw);
        subject.Should().NotContain("more]");
    }

    [Fact]
    public void Display_shortening_still_keeps_head_and_tail_per_its_contract()
    {
        // The render-time contract the raw store depends on: a shortened path cannot disguise its
        // destination, because both ends survive the cut. This moved here from the parser tests when
        // the parser stopped shortening (backlog 103).
        var padded = "/workspaces/w1/src" + string.Concat(Enumerable.Repeat("/aaaa", 60))
            + string.Concat(Enumerable.Repeat("/..", 63)) + "/data/keys/key-8c2f.xml";

        var shown = AgentToolSubject.Shorten(padded, AgentToolSubject.MaxLength);

        shown.Length.Should().BeLessThanOrEqualTo(AgentToolSubject.MaxLength);
        shown.Should().StartWith("/workspaces/w1/src");
        shown.Should().EndWith("key-8c2f.xml");
        shown.Should().Contain("more]");
    }
}
