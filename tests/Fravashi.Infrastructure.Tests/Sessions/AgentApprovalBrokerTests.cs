using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Plan 2e Task 3: the studio side of the CLI's PreToolUse hook. The hook BLOCKS on the HTTP call this
/// broker answers, so every path here has to end in a decision: the user's, the read-only policy's, or
/// a denial. Nothing may hang forever and nothing may default to allow.
/// </summary>
public class AgentApprovalBrokerTests
{
    private static readonly AgentSessionId Session = AgentSessionId.New();

    /// <summary>The contained copy this session's agent is confined to.</summary>
    private const string Copy = "/workspaces/w1";

    private static readonly Dictionary<string, string> WriteInput = new()
    {
        ["file_path"] = "/workspaces/w1/notes.md",
        ["content"] = "hello",
    };

    private static AgentApprovalBroker Broker(TimeSpan? timeout = null) =>
        new(timeout ?? TimeSpan.FromMinutes(5));

    private static Dictionary<string, string> Reading(string path) => new() { ["file_path"] = path };

    // ---- session secrets ----------------------------------------------------------------

    [Fact]
    public void Each_session_gets_its_own_unguessable_secret()
    {
        var broker = Broker();
        var other = AgentSessionId.New();

        var first = broker.RegisterSession(Session);
        var second = broker.RegisterSession(other);

        first.Should().NotBe(second);
        first.Length.Should().BeGreaterThanOrEqualTo(32);
        broker.ResolveSecret(first).Should().Be(Session);
        broker.ResolveSecret(second).Should().Be(other);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-secret")]
    public void An_unknown_secret_belongs_to_no_session(string? secret) =>
        Broker().ResolveSecret(secret).Should().BeNull();

    [Fact]
    public void A_released_session_stops_answering_to_its_secret()
    {
        var broker = Broker();
        var secret = broker.RegisterSession(Session);

        broker.ReleaseSession(Session);

        broker.ResolveSecret(secret).Should().BeNull();
    }

    // ---- the read-only policy -----------------------------------------------------------

    [Fact]
    public async Task A_read_inside_the_sessions_copy_is_allowed_without_disturbing_the_user()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        var asked = false;
        broker.Requested += _ => asked = true;

        var decision = await broker.RequestAsync(Session, "Read", "toolu_1", Reading("/workspaces/w1/a.cs"));

        decision.Approved.Should().BeTrue();
        asked.Should().BeFalse();
        broker.PendingFor(Session).Should().BeEmpty();
    }

    [Theory]
    [InlineData("/proc/1/environ")]
    [InlineData("/proc/self/environ")]
    [InlineData("/data/fravashi.db")]
    [InlineData("/data/keys/key-8c2f.xml")]
    [InlineData("/workspaces/w1/../../proc/1/environ")]
    [InlineData("../../data/fravashi.db")]
    public async Task A_read_that_leaves_the_copy_asks_the_user_like_any_other_call(string path)
    {
        // Backlog 72's invariant only holds if the boundary covers reads too: the agent shares the
        // studio's container and uid, so PID 1's environment (the host-bridge token), the database and
        // the key ring are all one auto-allowed Read away.
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(Session, "Read", "toolu_1", Reading(path));

        pending.IsCompleted.Should().BeFalse("the agent must block until the user answers");
        seen.Should().NotBeNull();
        seen!.ToolName.Should().Be("Read");
        seen.Warning.Should().NotBeNullOrWhiteSpace("the prompt has to say why a read is asking");
        broker.PendingFor(Session).Should().ContainSingle();

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task A_read_whose_path_cannot_be_worked_out_asks_rather_than_running()
    {
        // Fail closed, the same way the rest of this hook does: no path, no auto-allow.
        var broker = Broker(TimeSpan.FromMilliseconds(80));
        broker.RegisterSession(Session, Copy);

        var decision = await broker.RequestAsync(
            Session, "Read", "toolu_1", new Dictionary<string, string> { ["offset"] = "0" });

        decision.Approved.Should().BeFalse();
    }

    [Fact]
    public async Task A_session_registered_without_a_copy_auto_allows_no_file_read_at_all()
    {
        var broker = Broker();
        broker.RegisterSession(Session);

        var pending = broker.RequestAsync(Session, "Read", "toolu_1", Reading("/workspaces/w1/a.cs"));

        pending.IsCompleted.Should().BeFalse();
        broker.PendingFor(Session).Should().ContainSingle();

        // A tool that reads nothing off the disk has no boundary to cross, so it still runs.
        var harness = await broker.RequestAsync(Session, "TaskList", "toolu_2", new Dictionary<string, string>());
        harness.Approved.Should().BeTrue();
    }

    [Fact]
    public async Task Re_registering_a_session_without_a_copy_revokes_the_previous_one()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        (await broker.RequestAsync(Session, "Read", "toolu_1", Reading("/workspaces/w1/a.cs")))
            .Approved.Should().BeTrue();

        broker.RegisterSession(Session);

        var pending = broker.RequestAsync(Session, "Read", "toolu_2", Reading("/workspaces/w1/a.cs"));
        pending.IsCompleted.Should().BeFalse();
        broker.PendingFor(Session).Should().ContainSingle();
    }

    [Fact]
    public async Task An_escaping_read_for_an_unregistered_session_is_denied_not_allowed()
    {
        var decision = await Broker().RequestAsync(
            AgentSessionId.New(), "Read", "toolu_1", Reading("/proc/1/environ"));

        decision.Approved.Should().BeFalse();
    }

    [Fact]
    public async Task A_change_always_asks_even_when_the_same_tool_was_just_approved()
    {
        // Decision 5: never "remember this tool for the session".
        var broker = Broker();
        broker.RegisterSession(Session);

        var first = broker.RequestAsync(Session, "Write", "toolu_1", WriteInput);
        broker.PendingFor(Session).Should().ContainSingle();
        broker.Answer(broker.PendingFor(Session)[0].RequestId, approved: true, "ok").Should().BeTrue();
        (await first).Approved.Should().BeTrue();

        var second = broker.RequestAsync(Session, "Write", "toolu_2", WriteInput);

        broker.PendingFor(Session).Should().ContainSingle();
        second.IsCompleted.Should().BeFalse();
        broker.Answer(broker.PendingFor(Session)[0].RequestId, approved: false, "no");
        (await second).Approved.Should().BeFalse();
    }

    // ---- what the user is actually answering about ---------------------------------------
    //
    // The padded-path escape, at the HUMAN gate this time. The machine gate stopped deciding on a
    // shortened copy of the argument; the person answering was still shown one. A file_path of several
    // hundred characters lands on the studio's own data directory while the 200-character subject the
    // prompt renders reads as an ordinary folder deep inside the copy, and mutations got no containment
    // sentence at all. The worst target is this session's own approve.sh: it is what answers the NEXT
    // approval, so one disguised yes becomes every yes.

    /// <summary>A path that reads as deep-but-ordinary for far longer than any display cap, and then
    /// climbs back to the root and into <paramref name="leaf"/>.</summary>
    private static string PaddedEscape(string leaf) =>
        Copy + "/src"
        + string.Concat(Enumerable.Repeat("/aaaa", 60))
        + string.Concat(Enumerable.Repeat("/..", 63))
        + leaf;

    [Fact]
    public async Task A_change_that_lands_outside_the_copy_tells_the_user_where_it_really_lands()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var path = PaddedEscape("/data/agent-sessions/s1/approve.sh");
        var pending = broker.RequestAsync(Session, "Write", "toolu_1", new Dictionary<string, string>
        {
            ["file_path"] = path,
            ["content"] = "#!/bin/sh\necho allow",
        });

        pending.IsCompleted.Should().BeFalse("a change always asks");
        seen.Should().NotBeNull();

        // The premise: the subject line the PROMPT shows is capped (it shortens on render), so it
        // alone cannot say this. The request's Detail itself is the RAW value (backlog 103): the
        // store keeps what the CLI was really asked to do, and display copies derive from it.
        path.Length.Should().BeGreaterThan(AgentToolSubject.MaxLength * 2);
        seen!.Detail.Should().Be(path);

        seen.Target.Should().NotBeNull("the user cannot answer about a destination nobody showed them");
        seen.Target!.Field.Should().Be("file_path");
        seen.Target.Containment.Should().Be(AgentTargetContainment.Outside);
        seen.Target.Canonical.Should().Contain("approve.sh");
        seen.Target.Canonical.Should().NotContain("aaaa", "the resolved target is where it lands, not how it was spelled");
        seen.Warning.Should().NotBeNullOrWhiteSpace().And.Contain("OUTSIDE");

        // And it has to be in what the prompt RENDERS, not merely on the request object: before this,
        // the broker held the whole path and ApprovalPrompt.razor showed the capped subject, the
        // warning and the body field, none of which named where the write was going.
        (seen.Detail + " " + seen.Warning).Should().Contain("approve.sh");

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Theory]
    [InlineData("/data/keys/key-8c2f.xml")]
    [InlineData("/data/fravashi.db")]
    [InlineData("/proc/1/environ")]
    public async Task A_change_aimed_straight_out_of_the_copy_is_called_out_too(string path)
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        _ = broker.RequestAsync(Session, "Write", "toolu_1", new Dictionary<string, string>
        {
            ["file_path"] = path,
            ["content"] = "x",
        });

        seen.Should().NotBeNull();
        seen!.Target!.Containment.Should().Be(AgentTargetContainment.Outside);
        seen.Warning.Should().Contain("OUTSIDE");

        broker.Answer(seen.RequestId, approved: false, "no");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_ordinary_change_inside_the_copy_names_its_target_and_cries_no_escape()
    {
        // The other half of the check: a boundary that shouts at everything says nothing.
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(Session, "Write", "toolu_1", WriteInput);

        seen.Should().NotBeNull();
        seen!.Target.Should().NotBeNull();
        seen.Target!.Containment.Should().Be(AgentTargetContainment.Inside);
        seen.Target.Canonical.Should().EndWith("notes.md");
        seen.Warning.Should().BeNull("an in-copy change is the ordinary case and must read like one");

        broker.Answer(seen.RequestId, approved: true, "ok");
        (await pending).Approved.Should().BeTrue();
    }

    [Fact]
    public async Task A_relative_change_is_measured_from_the_copy_the_child_runs_in()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        var seen = new List<PendingToolApproval>();
        broker.Requested += seen.Add;

        _ = broker.RequestAsync(Session, "Edit", "toolu_1", new Dictionary<string, string>
        {
            ["file_path"] = "src/Program.cs",
            ["new_string"] = "x",
        });
        _ = broker.RequestAsync(Session, "Edit", "toolu_2", new Dictionary<string, string>
        {
            ["file_path"] = "../../data/keys/key-8c2f.xml",
            ["new_string"] = "x",
        });

        seen[0].Target!.Containment.Should().Be(AgentTargetContainment.Inside);
        seen[1].Target!.Containment.Should().Be(AgentTargetContainment.Outside);

        foreach (var request in seen)
        {
            broker.Answer(request.RequestId, approved: false, "no");
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_change_that_names_no_path_says_nothing_about_one()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        _ = broker.RequestAsync(
            Session, "Bash", "toolu_1", new Dictionary<string, string> { ["command"] = "npm test" });

        seen.Should().NotBeNull();
        seen!.Target.Should().BeNull();
        seen.Warning.Should().BeNull();

        broker.Answer(seen.RequestId, approved: false, "no");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_change_whose_path_is_not_text_is_never_shown_as_a_place_it_would_go()
    {
        // Same refusal the read scope makes: a rendering is not a path, so the studio has no
        // destination to claim and says so rather than inventing one.
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var input = AgentToolInput.Create(
            new Dictionary<string, string> { ["file_path"] = """{"a/b/c":1}""", ["content"] = "x" },
            textFields: ["content"]);

        _ = broker.RequestAsync(Session, "Write", "toolu_1", input);

        seen.Should().NotBeNull();
        seen!.Target!.Containment.Should().Be(AgentTargetContainment.Undetermined);
        seen.Target.Canonical.Should().BeNull();
        seen.Warning.Should().NotBeNullOrWhiteSpace();

        broker.Answer(seen.RequestId, approved: false, "no");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_read_that_asks_carries_its_destination_too_not_just_its_reason()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        _ = broker.RequestAsync(Session, "Read", "toolu_1", Reading(PaddedEscape("/data/fravashi.db")));

        seen.Should().NotBeNull();
        seen!.Warning.Should().Contain("OUTSIDE");
        seen.Target!.Containment.Should().Be(AgentTargetContainment.Outside);
        seen.Target.Canonical.Should().EndWith("fravashi.db");

        broker.Answer(seen.RequestId, approved: false, "no");
        await Task.CompletedTask;
    }

    // ---- asking the user ----------------------------------------------------------------

    [Fact]
    public async Task Approving_a_parked_call_releases_the_blocked_hook()
    {
        var broker = Broker();
        broker.RegisterSession(Session);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(Session, "Write", "toolu_1", WriteInput);

        seen.Should().NotBeNull();
        seen!.ToolName.Should().Be("Write");
        seen.Detail.Should().Be("/workspaces/w1/notes.md");
        seen.Input.Should().ContainKey("content").WhoseValue.Should().Be("hello");
        pending.IsCompleted.Should().BeFalse();

        broker.Answer(seen.RequestId, approved: true, "approved in the studio");

        var decision = await pending;
        decision.Approved.Should().BeTrue();
        decision.Reason.Should().Be("approved in the studio");
        broker.PendingFor(Session).Should().BeEmpty();
    }

    [Fact]
    public async Task Denying_a_parked_call_sends_the_users_reason_back_to_the_agent()
    {
        var broker = Broker();
        broker.RegisterSession(Session);
        (PendingToolApproval Request, ToolApprovalDecision Decision)? resolved = null;
        broker.Resolved += (p, d) => resolved = (p, d);

        var pending = broker.RequestAsync(Session, "Bash", "toolu_1", new Dictionary<string, string> { ["command"] = "rm -rf /" });
        broker.Answer(broker.PendingFor(Session)[0].RequestId, approved: false, "not that command");

        var decision = await pending;
        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Be("not that command");
        resolved.Should().NotBeNull();
        resolved!.Value.Request.ToolName.Should().Be("Bash");
        resolved.Value.Decision.Approved.Should().BeFalse();
    }

    [Fact]
    public async Task Several_calls_can_wait_at_once_and_are_answered_independently()
    {
        var broker = Broker();
        broker.RegisterSession(Session);

        var first = broker.RequestAsync(Session, "Write", "toolu_1", WriteInput);
        var second = broker.RequestAsync(Session, "Edit", "toolu_2", WriteInput);

        var pending = broker.PendingFor(Session);
        pending.Should().HaveCount(2);
        pending[0].ToolName.Should().Be("Write", "the oldest request comes first");

        broker.Answer(pending[1].RequestId, approved: true, "yes");
        (await second).Approved.Should().BeTrue();
        first.IsCompleted.Should().BeFalse();

        broker.Answer(pending[0].RequestId, approved: false, "no");
        (await first).Approved.Should().BeFalse();
    }

    [Fact]
    public void Answering_an_unknown_or_already_answered_request_is_refused()
    {
        var broker = Broker();
        broker.RegisterSession(Session);
        _ = broker.RequestAsync(Session, "Write", "toolu_1", WriteInput);
        var requestId = broker.PendingFor(Session)[0].RequestId;

        broker.Answer("no-such-request", true, "ok").Should().BeFalse();
        broker.Answer(requestId, true, "ok").Should().BeTrue();
        broker.Answer(requestId, false, "changed my mind").Should().BeFalse();
    }

    // ---- failing closed -----------------------------------------------------------------

    [Fact]
    public async Task A_call_for_an_unregistered_session_is_denied()
    {
        var decision = await Broker().RequestAsync(AgentSessionId.New(), "Write", "toolu_1", WriteInput);

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_unanswered_call_is_denied_once_its_timeout_expires()
    {
        var broker = Broker(TimeSpan.FromMilliseconds(80));
        broker.RegisterSession(Session);

        var decision = await broker.RequestAsync(Session, "Write", "toolu_1", WriteInput);

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Contain("no answer");
        broker.PendingFor(Session).Should().BeEmpty();
    }

    [Fact]
    public async Task Releasing_a_session_denies_whatever_it_left_waiting()
    {
        // The window closing must NOT do this (decision 6); ending the session must.
        var broker = Broker();
        broker.RegisterSession(Session);
        var pending = broker.RequestAsync(Session, "Write", "toolu_1", WriteInput);

        broker.ReleaseSession(Session);

        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task A_cancelled_request_is_denied_rather_than_left_hanging()
    {
        var broker = Broker();
        broker.RegisterSession(Session);
        using var cts = new CancellationTokenSource();

        var pending = broker.RequestAsync(Session, "Write", "toolu_1", WriteInput, cts.Token);
        await cts.CancelAsync();

        (await pending).Approved.Should().BeFalse();
    }

    // ---- who made the decision (backlog 114) ----------------------------------------------
    //
    // "Not approved" covers two completely different events: the user turning a change down, and the
    // studio refusing on their behalf because nobody answered. The transcript reported both as the
    // user's own denial, and an owner reading "You denied Bash" for a call they were never asked about
    // concluded the gate had been bypassed. The attribution has to be set where the decision is MADE,
    // so nothing downstream has to guess it back out of a sentence.

    [Fact]
    public async Task A_denial_the_user_gave_is_attributed_to_the_user()
    {
        var broker = Broker();
        broker.RegisterSession(Session);
        ToolApprovalDecision? resolved = null;
        broker.Resolved += (_, d) => resolved = d;

        var pending = broker.RequestAsync(Session, "Bash", "toolu_1", new Dictionary<string, string> { ["command"] = "rm -rf /" });
        broker.Answer(broker.PendingFor(Session)[0].RequestId, approved: false, "not that command");

        (await pending).DecidedBy.Should().Be(ToolApprovalDecidedBy.User);
        resolved.Should().NotBeNull();
        resolved!.DecidedBy.Should().Be(ToolApprovalDecidedBy.User);
    }

    [Fact]
    public async Task An_approval_the_user_gave_is_attributed_to_the_user_too()
    {
        var broker = Broker();
        broker.RegisterSession(Session);

        var pending = broker.RequestAsync(Session, "Write", "toolu_1", WriteInput);
        broker.Answer(broker.PendingFor(Session)[0].RequestId, approved: true, "approved in the studio");

        (await pending).DecidedBy.Should().Be(ToolApprovalDecidedBy.User);
    }

    [Fact]
    public async Task A_refusal_the_timeout_imposed_is_not_attributed_to_the_user()
    {
        var broker = Broker(TimeSpan.FromMilliseconds(80));
        broker.RegisterSession(Session);

        var decision = await broker.RequestAsync(Session, "Write", "toolu_1", WriteInput);

        decision.Approved.Should().BeFalse();
        decision.DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
    }

    [Fact]
    public async Task A_refusal_a_released_session_imposed_is_not_attributed_to_the_user()
    {
        var broker = Broker();
        broker.RegisterSession(Session);
        var pending = broker.RequestAsync(Session, "Write", "toolu_1", WriteInput);

        broker.ReleaseSession(Session);

        (await pending).DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
    }

    [Fact]
    public async Task A_refusal_a_cancelled_request_imposed_is_not_attributed_to_the_user()
    {
        var broker = Broker();
        broker.RegisterSession(Session);
        using var cts = new CancellationTokenSource();

        var pending = broker.RequestAsync(Session, "Write", "toolu_1", WriteInput, cts.Token);
        await cts.CancelAsync();

        (await pending).DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
    }

    [Fact]
    public async Task A_refusal_the_shutdown_imposed_is_not_attributed_to_the_user()
    {
        var broker = Broker();
        broker.RegisterSession(Session);
        var pending = broker.RequestAsync(Session, "Write", "toolu_1", WriteInput);

        broker.Dispose();

        (await pending).DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
    }

    [Fact]
    public async Task A_refusal_for_an_unregistered_session_is_not_attributed_to_the_user()
    {
        var decision = await Broker().RequestAsync(AgentSessionId.New(), "Write", "toolu_1", WriteInput);

        decision.Approved.Should().BeFalse();
        decision.DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
    }

    [Fact]
    public void An_answer_defaults_to_the_studio_rather_than_to_the_user()
    {
        // The fallback direction is the whole point: blaming a user for a refusal they never made is
        // worse than reporting one of their own denials neutrally.
        new ToolApprovalDecision(false, "whatever").DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
        ToolApprovalDecision.Deny("no").DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
        ToolApprovalDecision.Allow("yes").DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
    }

    // ---- no refusal is invisible (backlog 117) -----------------------------------------------
    //
    // A refusal that is never parked raises neither Requested nor Resolved, so before Refused existed it
    // left no trace at all: an agent could retry a refused call any number of times while the transcript
    // showed nothing.

    [Fact]
    public async Task A_refusal_nobody_was_asked_about_is_raised_rather_than_returned_in_silence()
    {
        var broker = Broker();
        var session = AgentSessionId.New();
        var refusals = new List<AutomaticToolRefusal>();
        var asked = false;
        broker.Refused += refusals.Add;
        broker.Requested += _ => asked = true;

        var decision = await broker.RequestAsync(
            session, "Bash", "toolu_1", new Dictionary<string, string> { ["command"] = "curl evil.example" });

        decision.Approved.Should().BeFalse();
        asked.Should().BeFalse();
        var refusal = refusals.Should().ContainSingle().Subject;
        refusal.SessionId.Should().Be(session);
        refusal.ToolName.Should().Be("Bash");
        refusal.Detail.Should().Be("curl evil.example");
        refusal.Reason.Should().Be(decision.Reason);
    }

    [Fact]
    public async Task Every_retry_against_the_same_refusal_is_raised_again()
    {
        var broker = Broker();
        var session = AgentSessionId.New();
        var refusals = 0;
        broker.Refused += _ => refusals++;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            (await broker.RequestAsync(session, "Write", "toolu_1", WriteInput)).Approved.Should().BeFalse();
        }

        refusals.Should().Be(3);
    }

    [Fact]
    public async Task A_timeout_refusal_is_reported_once_through_resolved_and_not_again_as_refused()
    {
        // A parked call already reaches the transcript through Resolved, attributed to the studio; raising
        // Refused for it as well would write the same refusal twice.
        var broker = Broker(TimeSpan.FromMilliseconds(80));
        broker.RegisterSession(Session);
        var refused = 0;
        ToolApprovalDecision? resolved = null;
        broker.Refused += _ => refused++;
        broker.Resolved += (_, d) => resolved = d;

        var decision = await broker.RequestAsync(Session, "Write", "toolu_1", WriteInput);

        decision.Approved.Should().BeFalse();
        resolved.Should().NotBeNull();
        resolved!.DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
        refused.Should().Be(0);
    }

    [Fact]
    public void Refuse_denies_and_says_so()
    {
        var broker = Broker();
        AutomaticToolRefusal? seen = null;
        broker.Refused += r => seen = r;

        var decision = broker.Refuse(Session, "Edit", "/workspaces/w1/a.cs", "The studio could not read this tool call.");

        decision.Approved.Should().BeFalse();
        decision.DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
        seen.Should().Be(new AutomaticToolRefusal(
            Session, "Edit", "/workspaces/w1/a.cs", "The studio could not read this tool call.", seen!.RefusedUtc));
    }

    [Fact]
    public void Pending_calls_belong_to_their_own_session()
    {
        var broker = Broker();
        var other = AgentSessionId.New();
        broker.RegisterSession(Session);
        broker.RegisterSession(other);

        _ = broker.RequestAsync(Session, "Write", "toolu_1", WriteInput);

        broker.PendingFor(Session).Should().ContainSingle();
        broker.PendingFor(other).Should().BeEmpty();
    }

    // ---- the turn's own document (ADR 0022) ----------------------------------------------
    //
    // Under --json-schema the CLI builds a StructuredOutput tool out of the schema the STUDIO sent and
    // the model calls it to end its turn. The PreToolUse matcher is "*", so it arrives here like any
    // other call. Measured in the fravashi container on 2026-08-07 against claude 2.1.223: one hook
    // call for it on an ordinary typed turn, and FIVE on a turn whose document keeps failing validation
    // (the harness's five attempts, then terminal_reason structured_output_retry_exhausted). Without a
    // branch for it, switching the typed-turn flag on would park an approval at the end of every turn
    // and five at the end of a bad one, which is a product nobody can use.

    private static readonly Dictionary<string, string> Document = new()
    {
        ["version"] = "1",
        ["kind"] = "answer",
        ["message"] = "17 times 3 is **51**.",
    };

    [Fact]
    public async Task The_turns_own_document_is_the_outcome_and_never_stops_the_user()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy, typedTurns: true);
        var asked = false;
        broker.Requested += _ => asked = true;

        var decision = await broker.RequestAsync(Session, "StructuredOutput", "toolu_1", Document);

        decision.Approved.Should().BeTrue();
        asked.Should().BeFalse("ADR 0022: this renders as the turn's outcome, not as a tool call");
        broker.PendingFor(Session).Should().BeEmpty();
    }

    [Fact]
    public async Task Five_rejected_documents_in_one_turn_still_ask_nobody_anything()
    {
        // The measured worst case, replayed: the harness feeds a failing document back to the model and
        // retries, up to five attempts, and every attempt is another PreToolUse call.
        var broker = Broker();
        broker.RegisterSession(Session, Copy, typedTurns: true);
        var asked = 0;
        broker.Requested += _ => asked++;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var decision = await broker.RequestAsync(Session, "StructuredOutput", $"toolu_{attempt}", Document);
            decision.Approved.Should().BeTrue();
        }

        asked.Should().Be(0);
        broker.PendingFor(Session).Should().BeEmpty();
    }

    [Fact]
    public async Task A_session_that_never_asked_for_a_document_asks_about_a_call_by_that_name()
    {
        // Fail closed on the unexpected. This studio sent no schema, so the CLI built no such tool, so
        // nothing explains the call. Being called StructuredOutput is not a permission.
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(Session, "StructuredOutput", "toolu_1", Document);

        pending.IsCompleted.Should().BeFalse();
        seen.Should().NotBeNull();
        seen!.ToolName.Should().Be("StructuredOutput");
        broker.PendingFor(Session).Should().ContainSingle();

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task Re_registering_a_session_without_typed_turns_revokes_the_free_pass()
    {
        // The same rule the contained copy follows: what a session may do is what its LATEST
        // registration said, never what an earlier one said.
        var broker = Broker();
        broker.RegisterSession(Session, Copy, typedTurns: true);
        (await broker.RequestAsync(Session, "StructuredOutput", "toolu_1", Document)).Approved.Should().BeTrue();

        broker.RegisterSession(Session, Copy);

        var pending = broker.RequestAsync(Session, "StructuredOutput", "toolu_2", Document);
        pending.IsCompleted.Should().BeFalse();
        broker.PendingFor(Session).Should().ContainSingle();
    }

    [Fact]
    public async Task A_released_session_denies_the_outcome_tool_like_everything_else()
    {
        var broker = Broker(TimeSpan.FromMilliseconds(80));
        broker.RegisterSession(Session, Copy, typedTurns: true);

        broker.ReleaseSession(Session);

        (await broker.RequestAsync(Session, "StructuredOutput", "toolu_1", Document)).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task The_outcome_tool_for_a_session_nobody_registered_is_denied_not_allowed() =>
        (await Broker(TimeSpan.FromMilliseconds(80))
            .RequestAsync(AgentSessionId.New(), "StructuredOutput", "toolu_1", Document))
        .Approved.Should().BeFalse();

    [Theory]
    [InlineData("structuredoutput")]
    [InlineData("STRUCTUREDOUTPUT")]
    [InlineData("StructuredOutput ")]
    [InlineData("mcp__notes__StructuredOutput")]
    public async Task A_name_that_only_resembles_the_outcome_tool_asks(string toolName)
    {
        // An MCP server can publish any tool name it likes, so the match is ordinal and exact and the
        // near miss lands in front of the user.
        var broker = Broker();
        broker.RegisterSession(Session, Copy, typedTurns: true);

        var pending = broker.RequestAsync(Session, toolName, "toolu_1", Document);

        pending.IsCompleted.Should().BeFalse();
        broker.PendingFor(Session).Should().ContainSingle();
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("Bash")]
    [InlineData("WebFetch")]
    [InlineData("Task")]
    public async Task Everything_that_should_ask_still_asks_on_a_typed_session(string toolName)
    {
        // The other half of the fix, and the one worth a test: nothing about typing a turn may loosen
        // what a change has to go through. Decision 5 is untouched.
        var broker = Broker();
        broker.RegisterSession(Session, Copy, typedTurns: true);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(Session, toolName, "toolu_1", WriteInput);

        pending.IsCompleted.Should().BeFalse();
        seen.Should().NotBeNull();
        seen!.ToolName.Should().Be(toolName);
        broker.PendingFor(Session).Should().ContainSingle();

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task A_typed_session_still_scopes_its_reads_to_the_contained_copy()
    {
        // Typing a turn says nothing about where a read lands, so the read scope has to survive it
        // intact: an in-copy read still runs unattended and an escaping one still asks.
        var broker = Broker();
        broker.RegisterSession(Session, Copy, typedTurns: true);

        (await broker.RequestAsync(Session, "Read", "toolu_1", Reading("/workspaces/w1/a.cs")))
            .Approved.Should().BeTrue();

        var escaping = broker.RequestAsync(Session, "Read", "toolu_2", Reading("/proc/1/environ"));
        escaping.IsCompleted.Should().BeFalse();
        broker.PendingFor(Session).Should().ContainSingle();
    }
}
