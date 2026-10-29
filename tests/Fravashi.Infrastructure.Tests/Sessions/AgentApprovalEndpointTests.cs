using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Plan 2e Task 3: what <c>POST /api/agent/approval</c> does with the hook's POST. The request body in
/// these tests is the payload the REAL hook received on stdin during the 2026-08-04 container spike
/// (Fixtures/claude-pretooluse-hook-input.json), and the response shape is the one the CLI actually
/// honored when it executed the approved Write.
/// <para>
/// The endpoint sits under /api, which every studio gate exempts by design, so the per-session secret
/// IS the authentication. Anything that cannot be authenticated or understood is DENIED, never allowed.
/// </para>
/// </summary>
public class AgentApprovalEndpointTests
{
    private static string HookPayload() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", "claude-pretooluse-hook-input.json"));

    /// <summary>
    /// The PreToolUse payload the CLI really sent for its own turn-terminating document, captured from
    /// claude 2.1.223 in the fravashi container on 2026-08-07 under the studio's own flag set. The
    /// argument is the document, nested, and there is no path anywhere in it.
    /// </summary>
    private static string OutcomePayload() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", "claude-pretooluse-structured-output.json"));

    private static string PayloadFor(string tool) =>
        $$"""{"session_id":"s","hook_event_name":"PreToolUse","tool_name":"{{tool}}","tool_input":{"file_path":"/workspaces/w1/a.cs"},"tool_use_id":"toolu_9"}""";

    private static (string Decision, string? Reason) ReadDecision(string json)
    {
        using var document = JsonDocument.Parse(json);
        var output = document.RootElement.GetProperty("hookSpecificOutput");
        output.GetProperty("hookEventName").GetString().Should().Be("PreToolUse");
        return (
            output.GetProperty("permissionDecision").GetString()!,
            output.TryGetProperty("permissionDecisionReason", out var reason) ? reason.GetString() : null);
    }

    /// <summary>Answers the first request that parks, the way the user clicking Approve would.</summary>
    private static async Task AnswerWhenParkedAsync(AgentApprovalBroker broker, AgentSessionId session, bool approved, string reason)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var pending = broker.PendingFor(session);
            if (pending.Count > 0)
            {
                broker.Answer(pending[0].RequestId, approved, reason);
                return;
            }

            await Task.Delay(10); // poll interval inside a wait helper, not an arm-window guess (backlog 107)
        }

        throw new InvalidOperationException("The call never reached the approval queue.");
    }

    [Fact]
    public async Task The_captured_hook_payload_parks_the_real_write_and_approving_it_answers_allow()
    {
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.New();
        var secret = broker.RegisterSession(session);

        var handling = AgentApprovalEndpoint.HandleAsync(secret, HookPayload(), broker);
        await AnswerWhenParkedAsync(broker, session, approved: true, "approved in the studio");
        var response = await handling;

        response.StatusCode.Should().Be(200);
        var (decision, reason) = ReadDecision(response.Json);
        decision.Should().Be("allow");
        reason.Should().Be("approved in the studio");
    }

    [Fact]
    public async Task The_pending_request_shows_the_user_what_the_agent_wants_to_write()
    {
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.New();
        var secret = broker.RegisterSession(session);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var handling = AgentApprovalEndpoint.HandleAsync(secret, HookPayload(), broker);
        await AnswerWhenParkedAsync(broker, session, approved: false, "no");
        await handling;

        // Captured from the real hook: a Write of /tmp/agent-spike/run/note.txt with "approved by hook".
        seen.Should().NotBeNull();
        seen!.ToolName.Should().Be("Write");
        seen.ToolUseId.Should().Be("toolu_016bDtCrTkJQc5DYS9AeBDep");
        seen.Detail.Should().Be("/tmp/agent-spike/run/note.txt");
        seen.Input["content"].Should().Be("approved by hook");
    }

    [Fact]
    public async Task The_real_turn_terminating_document_is_answered_allow_without_reaching_the_user()
    {
        // End to end over the wire the CLI really speaks, because that is the only place this defect
        // lived: the "*" matcher makes the CLI run the hook before StructuredOutput too, so a studio that
        // only knew about reads parked the studio's OWN turn terminator in front of the user and every
        // turn ended on a button. Measured in the container 2026-08-07 (claude 2.1.223): one such hook
        // call per ordinary typed turn and five on a turn that burns its validation attempts.
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.New();
        var secret = broker.RegisterSession(session, "/workspaces/w1", typedTurns: true);
        var asked = false;
        broker.Requested += _ => asked = true;

        var response = await AgentApprovalEndpoint.HandleAsync(secret, OutcomePayload(), broker);

        response.StatusCode.Should().Be(200);
        ReadDecision(response.Json).Decision.Should().Be("allow");
        asked.Should().BeFalse();
        broker.PendingFor(session).Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_document_asks_when_the_studio_never_requested_one()
    {
        // Fail closed on the unexpected: no schema went out with this session, so the CLI built no such
        // tool and the name alone buys nothing.
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.New();
        var secret = broker.RegisterSession(session, "/workspaces/w1");

        var handling = AgentApprovalEndpoint.HandleAsync(secret, OutcomePayload(), broker);
        await AnswerWhenParkedAsync(broker, session, approved: false, "no");
        var response = await handling;

        ReadDecision(response.Json).Decision.Should().Be("deny");
    }

    [Fact]
    public async Task An_agent_task_bound_for_a_remote_cloud_environment_reaches_the_user_saying_so()
    {
        // Over the real wire format, because that is where the value stops being a C# string: the CLI
        // sends run_in_background as a JSON BOOLEAN, which flattens through GetRawText() rather than
        // GetString(). Field names and the isolation values are the CLI's own (sdk-tools.d.ts, read in
        // the container): "remote" launches the agent in a remote cloud environment.
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.New();
        var secret = broker.RegisterSession(session, "/workspaces/w1");
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        const string payload = """
            {"session_id":"s","hook_event_name":"PreToolUse","tool_name":"Agent",
             "tool_input":{"description":"Tidy the helpers","prompt":"Rename the helpers.",
             "isolation":"remote","run_in_background":true},"tool_use_id":"toolu_9"}
            """;

        var handling = AgentApprovalEndpoint.HandleAsync(secret, payload, broker);
        await AnswerWhenParkedAsync(broker, session, approved: false, "no");
        await handling;

        seen.Should().NotBeNull();

        // Nothing else on the request carries this: an Agent call names no path, so Target is null and
        // the prompt's whole path block is absent for it.
        seen!.Target.Should().BeNull();

        var isolation = seen.Effects.Should().NotBeNull()
            .And.ContainSingle(e => e.Field == "isolation").Subject;
        isolation.Weight.Should().Be(AgentEffectWeight.Alarm);
        isolation.Notice.Should().ContainEquivalentOf("not on this machine");
        seen.Effects.Should().Contain(e => e.Field == "run_in_background");
    }

    [Fact]
    public async Task Denying_answers_deny_with_the_reason_the_agent_will_be_told()
    {
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.New();
        var secret = broker.RegisterSession(session);

        var handling = AgentApprovalEndpoint.HandleAsync(secret, HookPayload(), broker);
        await AnswerWhenParkedAsync(broker, session, approved: false, "the user declined this change in the studio");
        var response = await handling;

        response.StatusCode.Should().Be(200);
        ReadDecision(response.Json).Should().Be(("deny", "the user declined this change in the studio"));
    }

    [Fact]
    public async Task A_read_inside_the_sessions_copy_is_answered_immediately_without_asking()
    {
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var secret = broker.RegisterSession(AgentSessionId.New(), "/workspaces/w1");

        var response = await AgentApprovalEndpoint.HandleAsync(secret, PayloadFor("Read"), broker);

        response.StatusCode.Should().Be(200);
        ReadDecision(response.Json).Decision.Should().Be("allow");
    }

    [Fact]
    public async Task A_read_of_pid_ones_environment_reaches_the_user_instead_of_being_allowed()
    {
        // The hook is what stands between an approved-without-reading call and the host-bridge token
        // that PID 1 in this same container holds.
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.New();
        var secret = broker.RegisterSession(session, "/workspaces/w1");
        const string payload =
            """{"tool_name":"Read","tool_input":{"file_path":"/proc/1/environ"},"tool_use_id":"toolu_9"}""";

        var handling = AgentApprovalEndpoint.HandleAsync(secret, payload, broker);
        await AnswerWhenParkedAsync(broker, session, approved: false, "not that file");
        var response = await handling;

        response.StatusCode.Should().Be(200);
        ReadDecision(response.Json).Decision.Should().Be("deny");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("someone-elses-guess")]
    public async Task Without_the_sessions_own_secret_the_call_is_refused_and_denied(string? secret)
    {
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        broker.RegisterSession(AgentSessionId.New());
        var asked = false;
        broker.Requested += _ => asked = true;

        var response = await AgentApprovalEndpoint.HandleAsync(secret, HookPayload(), broker);

        response.StatusCode.Should().Be(401);
        ReadDecision(response.Json).Decision.Should().Be("deny");
        asked.Should().BeFalse("an unauthenticated caller must never reach the user");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"hook_event_name":"PreToolUse"}""")]
    [InlineData("""{"tool_name":"","tool_input":{}}""")]
    public async Task A_body_the_endpoint_cannot_understand_is_denied_not_allowed(string body)
    {
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var secret = broker.RegisterSession(AgentSessionId.New());

        var response = await AgentApprovalEndpoint.HandleAsync(secret, body, broker);

        response.StatusCode.Should().Be(400);
        ReadDecision(response.Json).Decision.Should().Be("deny");
    }

    [Fact]
    public async Task An_unreadable_call_from_a_known_session_is_refused_where_its_transcript_can_see_it()
    {
        // Backlog 117: the session is known, so this refusal is an enforcement action against it and must
        // not be invisible.
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.New();
        var secret = broker.RegisterSession(session);
        var refusals = new List<AutomaticToolRefusal>();
        broker.Refused += refusals.Add;

        var response = await AgentApprovalEndpoint.HandleAsync(secret, "not json", broker);

        response.StatusCode.Should().Be(400);
        var refusal = refusals.Should().ContainSingle().Subject;
        refusal.SessionId.Should().Be(session);
        refusal.Reason.Should().Be(ReadDecision(response.Json).Reason);
    }

    [Fact]
    public async Task A_caller_without_a_session_raises_no_refusal_into_anybodys_transcript()
    {
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        broker.RegisterSession(AgentSessionId.New());
        var refused = false;
        broker.Refused += _ => refused = true;

        (await AgentApprovalEndpoint.HandleAsync("someone-elses-guess", HookPayload(), broker)).StatusCode.Should().Be(401);

        refused.Should().BeFalse("a secret that belongs to no session must not write into some session's transcript");
    }

    [Fact]
    public async Task A_tool_input_the_ui_must_render_survives_as_readable_strings()
    {
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.New();
        var secret = broker.RegisterSession(session);
        const string payload =
            """{"tool_name":"Bash","tool_input":{"command":"npm test","timeout":120000,"quiet":false,"env":{"CI":"1"}},"tool_use_id":"t1"}""";
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var handling = AgentApprovalEndpoint.HandleAsync(secret, payload, broker);
        await AnswerWhenParkedAsync(broker, session, approved: true, "ok");
        await handling;

        seen.Should().NotBeNull();
        seen!.Detail.Should().Be("npm test");
        seen.Input["timeout"].Should().Be("120000");
        seen.Input["quiet"].Should().Be("false");
        seen.Input["env"].Should().Contain("CI");
    }

    // ---- the display cap must never decide containment -------------------------------------

    /// <summary>
    /// A path padded past the endpoint's display cap with harmless "./" segments. Whatever cuts it
    /// short keeps a prefix that normalises to the copy itself, while the CLI (which never sees the
    /// cut) resolves the WHOLE argument and lands on the studio's own key ring.
    /// </summary>
    private static string PaddedEscape(string root, string target) =>
        root + "/" + string.Concat(Enumerable.Repeat("./", 5000)) + target;

    private static string ReadPayload(string filePath) => new JsonObject
    {
        ["tool_name"] = "Read",
        ["tool_input"] = new JsonObject { ["file_path"] = filePath },
        ["tool_use_id"] = "toolu_pad",
    }.ToJsonString();

    [Theory]
    [InlineData("../../data/keys/key-8c2f.xml")]
    [InlineData("../../data/fravashi.db")]
    [InlineData("../../data/agent-sessions/s1/approve.sh")]
    public async Task A_path_padded_past_the_display_cap_still_reaches_the_user(string target)
    {
        // The endpoint caps a tool argument so one call cannot flood the UI. That cap must NEVER be
        // what containment is judged on: a value cut at the cap resolves to "/workspaces/w1/...",
        // which is inside the copy, while the untruncated value the CLI runs leaves it entirely.
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.New();
        var secret = broker.RegisterSession(session, "/workspaces/w1");
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var handling = AgentApprovalEndpoint.HandleAsync(secret, ReadPayload(PaddedEscape("/workspaces/w1", target)), broker);

        // Parking happens synchronously inside RequestAsync, so an empty queue here means the call was
        // auto-allowed: the escape worked.
        var pending = broker.PendingFor(session);
        pending.Should().ContainSingle("a read the studio had to shorten must never be auto-allowed");
        broker.Answer(pending[0].RequestId, approved: false, "not that file");
        var response = await handling;

        ReadDecision(response.Json).Decision.Should().Be("deny");
        seen.Should().NotBeNull();
        seen!.Warning.Should().NotBeNullOrWhiteSpace("the prompt has to say why a read is asking");
    }

    [Fact]
    public async Task A_long_path_that_really_stays_inside_the_copy_is_still_answered_immediately()
    {
        // The point is not "long paths are suspicious", it is "the real value decides".
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var secret = broker.RegisterSession(AgentSessionId.New(), "/workspaces/w1");
        var inside = PaddedEscape("/workspaces/w1", "src/Program.cs");

        var response = await AgentApprovalEndpoint.HandleAsync(secret, ReadPayload(inside), broker);

        ReadDecision(response.Json).Decision.Should().Be("allow");
    }

    // ---- a path-bearing field that is not a JSON string is not a path ----------------------

    /// <summary>
    /// Every one of these sends a path-bearing argument that is NOT a JSON string. Flattened with
    /// <c>GetRawText()</c> they became text the containment check then walked as if it were a
    /// directory name, and JSON punctuation turned into path segments: the object literal below
    /// manufactured an eight-deep base out of nothing, which is exactly enough for the pattern's
    /// eight ".." to land back inside the copy and be auto-allowed. The array, the number, the
    /// boolean and the null all read as present, non-empty, relative directory names.
    /// </summary>
    public static TheoryData<string, string> NotAPathPayloads() => new()
    {
        {
            "an object literal whose keys manufacture directory segments",
            """{"tool_name":"Glob","tool_input":{"path":{"a/b/c/d/e/f/g/h":1},"pattern":"../../../../../../../../data/keys/*.xml"}}"""
        },
        {
            "an array around the path the CLI would open",
            """{"tool_name":"Read","tool_input":{"file_path":["/data/fravashi.db"]}}"""
        },
        {
            "a number",
            """{"tool_name":"Read","tool_input":{"file_path":5}}"""
        },
        {
            "a boolean",
            """{"tool_name":"LS","tool_input":{"path":true}}"""
        },
        {
            "a null",
            """{"tool_name":"Read","tool_input":{"file_path":null}}"""
        },
        {
            "an empty array",
            """{"tool_name":"NotebookRead","tool_input":{"notebook_path":[]}}"""
        },
        {
            "a nested object whose keys are themselves slash-separated",
            """{"tool_name":"Glob","tool_input":{"path":{"a/b":{"c/d":{"e/f":{"g/h":1}}}},"pattern":"../../../../../../../../data/keys/*.xml"}}"""
        },
        {
            "a pattern that is not a string either",
            """{"tool_name":"Glob","tool_input":{"path":"/workspaces/w1","pattern":["../../../../data/keys/*.xml"]}}"""
        },
    };

    [Theory]
    [MemberData(nameof(NotAPathPayloads))]
    public async Task A_path_bearing_field_that_is_not_a_string_reaches_the_user_instead_of_being_allowed(
        string because,
        string payload)
    {
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.New();
        var secret = broker.RegisterSession(session, "/workspaces/w1");
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var handling = AgentApprovalEndpoint.HandleAsync(secret, payload, broker);

        // Parking happens synchronously inside RequestAsync, so an empty queue means it was
        // auto-allowed and the type confusion worked.
        var pending = broker.PendingFor(session);
        pending.Should().ContainSingle(because);
        broker.Answer(pending[0].RequestId, approved: false, "not that");
        var response = await handling;

        ReadDecision(response.Json).Decision.Should().Be("deny");
        seen.Should().NotBeNull();
        seen!.Warning.Should().NotBeNullOrWhiteSpace("the prompt has to say why the read is asking");
    }

    [Fact]
    public async Task An_ordinary_glob_inside_the_copy_is_still_answered_immediately()
    {
        // The fix must be a TYPE check, not a blanket deny: the honest call still runs unattended.
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var secret = broker.RegisterSession(AgentSessionId.New(), "/workspaces/w1");
        const string payload =
            """{"tool_name":"Glob","tool_input":{"path":"/workspaces/w1/src","pattern":"**/*.cs"},"tool_use_id":"t1"}""";

        var response = await AgentApprovalEndpoint.HandleAsync(secret, payload, broker);

        response.StatusCode.Should().Be(200);
        ReadDecision(response.Json).Decision.Should().Be("allow");
    }

    [Fact]
    public async Task A_non_path_argument_may_still_be_any_json_the_ui_has_to_render()
    {
        // Only path-bearing fields are type-checked. A read whose OTHER arguments are objects,
        // numbers or nulls is still an ordinary read, and the prompt still gets something to show.
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var secret = broker.RegisterSession(AgentSessionId.New(), "/workspaces/w1");
        const string payload =
            """{"tool_name":"Read","tool_input":{"file_path":"/workspaces/w1/a.cs","offset":40,"limit":null,"opts":{"raw":true}}}""";

        var response = await AgentApprovalEndpoint.HandleAsync(secret, payload, broker);

        ReadDecision(response.Json).Decision.Should().Be("allow");
    }

    [Fact]
    public async Task A_change_carrying_a_non_string_path_still_asks_and_still_shows_what_it_sent()
    {
        // A tool that is not on the read-only list asks anyway; this only checks nothing about the
        // type rule breaks the display of a value that was never a string.
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.New();
        var secret = broker.RegisterSession(session, "/workspaces/w1");
        const string payload =
            """{"tool_name":"Write","tool_input":{"file_path":["/workspaces/w1/a.txt"],"content":"x"},"tool_use_id":"t1"}""";
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var handling = AgentApprovalEndpoint.HandleAsync(secret, payload, broker);
        await AnswerWhenParkedAsync(broker, session, approved: false, "no");
        await handling;

        seen.Should().NotBeNull();
        seen!.Input["file_path"].Should().Be("""["/workspaces/w1/a.txt"]""");
    }

    [Fact]
    public async Task A_huge_argument_is_still_capped_before_the_ui_renders_it()
    {
        // The cap keeps doing its own job: nothing the agent sends can flood the approval prompt.
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(10));
        var session = AgentSessionId.New();
        var secret = broker.RegisterSession(session, "/workspaces/w1");
        var content = new string('x', 50_000);
        var payload = new JsonObject
        {
            ["tool_name"] = "Write",
            ["tool_input"] = new JsonObject { ["file_path"] = "/workspaces/w1/a.txt", ["content"] = content },
        }.ToJsonString();
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var handling = AgentApprovalEndpoint.HandleAsync(secret, payload, broker);
        await AnswerWhenParkedAsync(broker, session, approved: false, "no");
        await handling;

        seen.Should().NotBeNull();
        seen!.Input["content"].Length.Should().BeLessThan(11_000);
        // Backlog 240: the cut names itself and keeps the tail, instead of a bare ellipsis.
        seen.Input["content"].Should().Contain("40,000 characters not shown");
        seen.Input["content"].Should().EndWith("xxxxx");
        seen.CutArguments!.Should().ContainSingle(c => c.Name == "content" && c.Full == content);
    }
}
