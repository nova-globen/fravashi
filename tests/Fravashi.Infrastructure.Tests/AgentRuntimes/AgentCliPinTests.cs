using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentRuntimes;

/// <summary>
/// Backlog 61 / ADR 0023: the agent CLIs are PINNED and promotion is a deliberate act. These tests are the
/// mechanism, not a description of one. They fail when anyone reintroduces an unpinned install, when the
/// catalog and the image disagree about which version ships, and when the pin moves without the promotion
/// procedure being followed (the literal below has to be edited too, so it shows up in the diff).
/// <para>
/// A recorded verification is a claim about three things together: a BINARY, the contract FILE that was
/// driven against it, and the production SURFACE that file drives. The last of those is the one this class
/// held nothing of until 2026-08-07: <c>ClaudeCliContractTests</c> reads its flag list out of
/// <c>ClaudeSessionRunner.BuildArguments</c>, asserts events produced by <c>ClaudeStreamJsonParser</c>, and
/// gates a real tool call through the hook files <c>SessionHookWriter</c> writes, so a flag the pinned CLI
/// would refuse could be added, or the transcript's events changed, or the hook's matcher narrowed, with
/// that file byte identical and every test here green. Three records below hold those three surfaces.
/// </para>
/// </summary>
public class AgentCliPinTests
{
    /// <summary>The Dockerfile build arg that carries each runtime's pin.</summary>
    private static readonly Dictionary<AgentTool, string> BuildArgs = new()
    {
        [AgentTool.Claude] = "CLAUDE_CODE_VERSION",

        // Retired to v2 with the runtimes they pinned (docs/v2.md section 1). The Dockerfile keeps both
        // ARG lines commented beside the install it no longer makes.
        // [AgentTool.Codex] = "CODEX_VERSION",
        // [AgentTool.Copilot] = "COPILOT_VERSION",
    };

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the test must be able to locate the repository root (Fravashi.sln)");
        return dir!.FullName;
    }

    [Fact]
    public void No_runtime_installs_latest_every_one_pins_an_exact_version()
    {
        AgentRuntimeCatalog.All.Should().NotBeEmpty();

        foreach (var descriptor in AgentRuntimeCatalog.All)
        {
            descriptor.PinnedVersion.Should().MatchRegex(
                @"^\d+\.\d+\.\d+$",
                $"{descriptor.NpmPackage} must pin one exact published version, not a range or a tag");

            descriptor.UpdateArguments.Should().Equal(
                ["install", "-g", $"{descriptor.NpmPackage}@{descriptor.PinnedVersion}"],
                "UpdateArguments is the single seam every install path goes through");

            string.Join(" ", descriptor.UpdateArguments).Should().NotContain("@latest");
        }
    }

    [Fact]
    public void The_claude_pin_cannot_move_without_this_literal_moving_with_it()
    {
        // A deliberate hard-coded literal. Its job is not to check a constant against itself: it is the
        // tripwire that makes promotion visible, because the pin cannot move without this line moving too.
        // It asserts ONLY what the pin is. It says nothing about what was verified, which the test below
        // does: the old name here claimed the two were the same fact, and for a day they were not.
        AgentRuntimeCatalog.For(AgentTool.Claude).PinnedVersion.Should().Be("2.1.280");
    }

    [Fact]
    public void The_claude_pin_is_the_version_whose_protocol_was_actually_driven()
    {
        // The defect this exists for, which happened: the pin was bumped 2.1.221 -> 2.1.222 while the
        // runner's comment, the transcript warning and the parser fixtures still rested on 2.1.221, so
        // three places began asserting a verification nobody had done. Keeping "what we pin" and "what we
        // drove the contract tests against" as separate recorded facts makes that state a red build
        // instead of a confident sentence. Promoting therefore means running ClaudeCliContractTests
        // against the new binary (ADR 0023 step 4) and moving BOTH numbers.
        var claude = AgentRuntimeCatalog.For(AgentTool.Claude);

        claude.ProtocolVerifiedVersion.Should().Be(
            claude.PinnedVersion,
            "the studio tells the user it verified its protocol against the pin, so someone has to have "
            + "run the contract tests against exactly that binary");
    }

    [Fact]
    public void The_recorded_verification_still_covers_what_the_contract_file_asserts()
    {
        // The gap the two tests above cannot see, and it opened for real: they compare constants to each
        // other, so both stayed green while ClaudeCliContractTests GREW two live tests and changed its
        // shared drive helper. The version was still 2.1.222 and still equal to the pin; what had moved was
        // the FILE, so "the whole of that file was driven green against this binary" quietly stopped being
        // a thing anyone had done. A recorded verification is a claim about a file AND a binary, and this
        // is the half nobody was holding.
        //
        // The fingerprint ignores comments and blank lines on purpose: rewording a remark is not a new
        // assertion, and a gate that demanded a live re-run for a typo would be routed around within a
        // week. Everything else counts, including a changed assertion inside a test whose name did not
        // move. It reads whole-line "//" comments only, so keep block comments out of that file.
        var path = Path.Combine(
            RepositoryRoot(), "tests", "Fravashi.Infrastructure.Tests", "Sessions", "ClaudeCliContractTests.cs");
        File.Exists(path).Should().BeTrue($"the contract file should exist at {path}");

        Fingerprint(File.ReadAllLines(path)).Should().Be(
            ContractFingerprint,
            $"ClaudeCliContractTests has changed since the recorded run on {VerifiedOn}. What the studio "
            + "tells the user is that it drove that WHOLE file against "
            + $"{AgentRuntimeCatalog.For(AgentTool.Claude).ProtocolVerifiedVersion}, so run it again, live "
            + "tier included (ADR 0023 step 4), and only then update this fingerprint and the date. Do not "
            + "update the fingerprint on its own: that is the claim rotting, written down");
    }

    /// <summary>
    /// The code of <c>ClaudeCliContractTests.cs</c> as it stood when the live tier was last driven green,
    /// reduced to what it asserts. Moved only by the promotion procedure, never on its own.
    /// </summary>
    private const string ContractFingerprint =
        "d695797bedb8c8d6e267379340fde997e37a9a10e1b580537966ca495363ff51";

    /// <summary>
    /// When that run happened, for the failure message and for the record in ADR 0023. Moved from 2026-08-07
    /// by backlog 25 (increments 5 to 7): the file grew the continue test, sent a spend ceiling on the typed
    /// document test and in its derived flag list, and pinned the cheap model on its first live test, and the
    /// whole file was then driven green against 2.1.222 on the owner's host (8 of 8, ADR 0023 has the run).
    /// <para>
    /// That record did not reproduce: review drove the same file three times against 2.1.222 the same day and
    /// the typed document test failed all three, because it counted raw assistant messages and the model wrote
    /// a second bubble (a rewording, or a sentence about the tool). Moved again, same date, once that test
    /// asserted what a reader sees open through <c>AgentTurnDrafts</c>: the typed test alone driven 16 times
    /// (4 of them carried a second bubble, all 16 green) and then the whole file twice, 8 of 8 each time.
    /// </para>
    /// <para>
    /// Moved to 2026-09-15 by backlog 84 and backlog 115, which were built in parallel and verified
    /// separately: 84 put the studio's tool server on every live turn (<c>--mcp-config</c>, a test driving a
    /// real <c>mcp__fravashi__studio_version</c> call through it, the config in the derived flag list) and 115
    /// made any non-clean end of the hook script a blocking refusal (see <see cref="VerifiedHookSettings"/>),
    /// each driven 9 of 9 against 2.1.222 on the owner's host. Neither run covered the MERGED file, so the
    /// contract was driven again after the merge and this fingerprint, the flag list and the hook record are
    /// that run's. ADR 0023 carries all three runs.
    /// </para>
    /// <para>
    /// Moved to 2026-09-29 by backlog 162: the runner names Grep and Glob in <c>--allowedTools</c>, without which
    /// the pinned native build has no such tools, and the file grew a test asking the model for a Grep call. Driven
    /// through ADR 0023's container recipe against the studio's own 2.1.222 with the owner's subscription sign-in,
    /// 11 of 11 twice in a row, and the new test seen red with the flag removed.
    /// </para>
    /// <para>
    /// Moved to 2026-09-30 when the pin was promoted from 2.1.222 to 2.1.280 (ADR 0023's procedure): this same file,
    /// unchanged, driven against 2.1.280 through the container recipe, 11 of 11 twice. The command lines, the parser
    /// surface and the hook contract did not move, so their records stand.
    /// </para>
    /// <para>
    /// Moved again the same day by backlog 174: the session's settings keep the signed-in account's claude.ai
    /// connectors, synced plugins and synced skills out (see <see cref="VerifiedHookSettings"/>), and the file grew a
    /// test reading a real session's init line. Driven through the container recipe as the studio's own user against
    /// 2.1.280 with the owner's subscription sign-in, 12 of 12 twice, and the new test seen red with both switches
    /// removed (it listed the account's 24 MCP servers).
    /// </para>
    /// <para>
    /// Driven again the same evening by backlog 179, whose empty attribution moved the hook contract record: the file
    /// unchanged, so its fingerprint stands, and the tier run the same way against 2.1.280.
    /// </para>
    /// <para>
    /// Moved to 2026-10-14 by backlog 248: the file grew two tests for a conversation held to reading (its init line
    /// lists exactly Read, Grep, Glob and ToolSearch beside StructuredOutput, and ToolSearch still reaches a studio
    /// tool under that list), its derived flag list gained <c>--tools</c>, and its live children take their config
    /// home from <c>FRAVASHI_CLI_CONTRACT_CONFIG_DIR</c> because this assembly clears an inherited
    /// <c>CLAUDE_CONFIG_DIR</c> (backlog 169). Driven on the owner's Windows host against a throwaway 2.1.280 signed
    /// into a throwaway config folder, 14 of 14 twice in a row; the init line read 32 built-in tools with the flag
    /// removed and exactly the four with it, so the new test is red without the flag.
    /// </para>
    /// <para>
    /// Moved to 2026-10-23 by work item #353: the search-tools test asked the cheap model to call Grep, and from 2026-10-16
    /// and 2026-10-18 every new ordinary session's first turn carries the studio's opening note, after which the model
    /// answered the note in text and called no tool, which read like a missing tool. The test now reads the init line of
    /// an ordinary session started with the runner's own command line and requires Grep and Glob in its tool list, the
    /// way backlog 248's reading-conversation test already proves a session's tools. Driven by the supervising host
    /// session on the owner's Windows host against a throwaway 2.1.280 signed into a throwaway config folder: 14 of 14.
    /// The command lines, the parser surface and the hook contract did not move, so their records stand.
    /// </para>
    /// <para>
    /// Moved to 2026-10-29 by the internal rename (its phase 1 renamed the identifiers the file names, nothing it asserts):
    /// driven once, live tier included, on the owner's Windows host against the pinned 2.1.280 at its stable install,
    /// signed in with the owner's subscription in a config folder of its own: 14 of 14
    ///. The command lines, the parser surface and the hook
    /// contract did not move, so their records stand.
    /// </para>
    /// </summary>
    private const string VerifiedOn = "2026-10-29";

    /// <summary>
    /// A stable digest of a file's code: trimmed, comment-free, blank-free lines. Deliberately not a hash
    /// of the raw bytes, which would also move on a re-indent or a CRLF checkout.
    /// </summary>
    private static string Fingerprint(IEnumerable<string> lines) =>
        Sha256(string.Join(
            '\n',
            lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal))));

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ---- the production surface the contract file drives ---------------------------------
    //
    // The two tests above hold the TEST file. They do not hold the production code that file exercises,
    // and that is a hole a reviewer walked straight through: add a flag to ClaudeSessionRunner.BuildArguments
    // that the pinned CLI does not accept and nothing here moves. ClaudeCliContractTests derives its flag
    // list FROM BuildArguments and asserts events produced BY ClaudeStreamJsonParser, so the file can sit
    // byte identical while what it drives, and therefore what was verified, changes underneath it.
    //
    // What is held below is not those two files. It is the DERIVED thing the contract file actually checks:
    // the argument vector the runner emits, and the events the parser produces from captured CLI output.
    // Fingerprinting the files themselves would fire on a renamed private helper, a reordered field or a
    // reworded comment, and a gate that demands a billable live re-run for a refactor is a gate that gets
    // suppressed. These move only when the studio sends something different or reads something differently.

    /// <summary>A fixed id, so the recorded vector is about the flags and not about a new guid each run.</summary>
    private static readonly Guid StableSessionId = new("3f7d1c02-8f4a-4d21-9a6b-6c1f0a2e5b70");

    /// <summary>Any timestamp at all: the rendering drops them, and the parser only needs a clock.</summary>
    private static readonly DateTimeOffset Epoch = new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Exactly what the runner sent the binary that earned <see cref="AgentRuntimeDescriptor.ProtocolVerifiedVersion"/>.
    /// Written out rather than hashed on purpose: a failure then names the flag that appeared, which is the
    /// whole question, instead of two hex strings that differ.
    /// <para>
    /// All three vectors carry <c>--mcp-config</c> since 2026-09-15 (backlog 84): production always writes the
    /// tool-server config beside the hook script, so that is the command line a real session sends, and the
    /// contract file drives every live turn with it.
    /// </para>
    /// </summary>
    private static readonly string[] VerifiedOpeningCommandLine =
    [
        "-p",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose",
        "--include-partial-messages",
        "--include-hook-events",
        "--settings", "/tmp/settings.json",
        "--mcp-config", "/tmp/tools.mcp.json",
        "--allowedTools", "Grep,Glob",
        "--session-id", "3f7d1c02-8f4a-4d21-9a6b-6c1f0a2e5b70",
    ];

    /// <summary>
    /// The other branch of the same method: a resumed, typed, model-pinned turn.
    /// <para>
    /// It carries no spend ceiling, which is why a third vector exists below: backlog 62 added the
    /// conditional <c>--max-budget-usd</c> and left it outside the recorded verification for a day, and
    /// backlog 25 closed that on 2026-09-14 with the live run that earns the record.
    /// </para>
    /// </summary>
    private static readonly string[] VerifiedResumedTypedCommandLine =
    [
        "-p",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose",
        "--include-partial-messages",
        "--include-hook-events",
        "--settings", "/tmp/settings.json",
        "--mcp-config", "/tmp/tools.mcp.json",
        "--allowedTools", "Grep,Glob",
        "--json-schema", "<schema>",
        "--model", "claude-haiku-4-5-20251001",
        "--resume", "ea12436c-b6eb-455e-b670-687beea9c6d2",
    ];

    /// <summary>
    /// The opening command line of a studio that types its turns (the default since backlog 25, 2026-09-14)
    /// and has a per-session spend ceiling configured (backlog 62): <c>--json-schema</c> and
    /// <c>--max-budget-usd</c> together on a fresh <c>--session-id</c>. Driven live against the pinned CLI by
    /// <c>A_real_turn_under_a_schema_comes_back_as_a_document</c>, which sends exactly this shape with a
    /// one-dollar ceiling, so the flag is ACCEPTED and not only declared.
    /// </summary>
    private static readonly string[] VerifiedOpeningTypedCappedCommandLine =
    [
        "-p",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose",
        "--include-partial-messages",
        "--include-hook-events",
        "--settings", "/tmp/settings.json",
        "--mcp-config", "/tmp/tools.mcp.json",
        "--allowedTools", "Grep,Glob",
        "--json-schema", "<schema>",
        "--max-budget-usd", "1",
        "--model", "claude-haiku-4-5-20251001",
        "--session-id", "3f7d1c02-8f4a-4d21-9a6b-6c1f0a2e5b70",
    ];

    /// <summary>
    /// The opening command line of a conversation held to reading (backlog 243 and 300: the commit reviewer and the
    /// autopilot operator): typed, with its built-in tools narrowed by <c>--tools</c> to
    /// <see cref="AgentReviewTools.BuiltIn"/>. Backlog 248 brought this conditional branch inside the recorded
    /// verification: <c>A_reading_conversation_starts_with_only_its_read_tools</c> starts the pinned CLI with exactly
    /// this shape and reads its init line, and <c>A_reading_conversation_still_reaches_the_studio_tools_through_tool_search</c>
    /// drives a turn on it through the tool server.
    /// </summary>
    private static readonly string[] VerifiedReadingCommandLine =
    [
        "-p",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose",
        "--include-partial-messages",
        "--include-hook-events",
        "--settings", "/tmp/settings.json",
        "--mcp-config", "/tmp/tools.mcp.json",
        "--allowedTools", "Grep,Glob",
        "--tools", "Read,Grep,Glob,ToolSearch",
        "--json-schema", "<schema>",
        "--model", "claude-haiku-4-5-20251001",
        "--session-id", "3f7d1c02-8f4a-4d21-9a6b-6c1f0a2e5b70",
    ];

    [Fact]
    public void The_recorded_verification_still_covers_the_command_line_the_runner_sends()
    {
        // THE reviewer's failure, made red: a flag added here, a value changed from stream-json to
        // something else, a flag dropped. Every one of those changes what a real CLI would have to accept,
        // and every one of them leaves ClaudeCliContractTests.cs byte identical, because that file reads
        // its list out of this method.
        CommandLine(resumeSessionId: null, model: null, typed: false)
            .Should().Equal(VerifiedOpeningCommandLine, CommandLineDrift);

        CommandLine(
                resumeSessionId: "ea12436c-b6eb-455e-b670-687beea9c6d2",
                model: "claude-haiku-4-5-20251001",
                typed: true)
            .Should().Equal(VerifiedResumedTypedCommandLine, CommandLineDrift);

        CommandLine(
                resumeSessionId: null,
                model: "claude-haiku-4-5-20251001",
                typed: true,
                maxBudgetUsd: 1m)
            .Should().Equal(VerifiedOpeningTypedCappedCommandLine, CommandLineDrift);

        CommandLine(
                resumeSessionId: null,
                model: "claude-haiku-4-5-20251001",
                typed: true,
                builtInTools: AgentReviewTools.BuiltIn)
            .Should().Equal(VerifiedReadingCommandLine, CommandLineDrift);
    }

    [Fact]
    public void An_effort_is_passed_only_when_one_is_chosen_and_leaves_every_recorded_vector_alone()
    {
        // Requested 2026-10-22 (model and effort per work item). A request without an effort is every recorded
        // vector above, unchanged, which is what keeps ProtocolVerifiedVersion's claim true for every conversation
        // that names none. The --effort branch itself is outside the recorded live verification (ADR 0023), like
        // --tools was before backlog 248: it is held here as the one shape it can take, after --model.
        CommandLine(resumeSessionId: null, model: null, typed: false)
            .Should().NotContain("--effort");

        CommandLine(resumeSessionId: null, model: null, typed: false, effort: AgentEffort.Medium)
            .Should().Equal(
                [.. VerifiedOpeningCommandLine.SkipLast(2), "--effort", "medium", .. VerifiedOpeningCommandLine.TakeLast(2)]);

        CommandLine(
                resumeSessionId: "ea12436c-b6eb-455e-b670-687beea9c6d2",
                model: "claude-haiku-4-5-20251001",
                typed: true,
                effort: AgentEffort.XHigh)
            .Should().ContainInConsecutiveOrder("--model", "claude-haiku-4-5-20251001", "--effort", "xhigh", "--resume");
    }

    private const string CommandLineDrift =
        "the command line ClaudeSessionRunner builds has changed, and what "
        + nameof(AgentRuntimeDescriptor.ProtocolVerifiedVersion) + " claims is that a real CLI of that "
        + "version accepted exactly this one. Install the pinned binary, run ClaudeCliContractTests with "
        + "the live tier on (ADR 0023 step 4), and only then record the new vector here";

    /// <summary>
    /// The arguments the runner would launch the CLI with, with the schema TEXT replaced by a placeholder.
    /// That exclusion is deliberate and it is the one thing this record does not hold: the CLI enforces
    /// whatever JSON schema it is handed, so growing a document kind (ADR 0022) is a product change, not a
    /// protocol change, and charging it a live re-run would teach everyone to edit the constant instead.
    /// The FLAG is held; its payload is ADR 0022's own tests to guard.
    /// </summary>
    private static IReadOnlyList<string> CommandLine(
        string? resumeSessionId,
        string? model,
        bool typed,
        decimal? maxBudgetUsd = null,
        IReadOnlyList<string>? builtInTools = null,
        AgentEffort? effort = null)
    {
        var request = new AgentSessionRequest(
            new AgentSessionId(StableSessionId),
            "workspace",
            "/workspaces/workspace",
            "hello",
            resumeSessionId,
            model,
            typed ? AgentTurnDocumentSchema.DefaultKinds : null,
            maxBudgetUsd)
        {
            BuiltInTools = builtInTools,
            Effort = effort,
        };

        var arguments = ClaudeSessionRunner.BuildArguments(request, "/tmp/settings.json", "/tmp/tools.mcp.json").ToList();
        var schema = arguments.IndexOf("--json-schema");
        if (schema >= 0 && schema + 1 < arguments.Count)
        {
            arguments[schema + 1] = "<schema>";
        }

        return arguments;
    }

    [Fact]
    public void The_recorded_verification_still_covers_the_events_the_parser_produces()
    {
        // The other half of what the live tier asserts. A contract test says "a real turn parses into the
        // events the studio depends on"; the events are ClaudeStreamJsonParser's, and that file can be
        // rewritten without the contract file moving a byte. What is fingerprinted is the OUTCOME, every
        // event every captured recording produces, so a refactor that keeps the transcript identical is
        // free and a change the user would read is not.
        var surface = ParserSurface();
        var rendering = Path.Combine(AppContext.BaseDirectory, "parser-surface.txt");
        File.WriteAllText(rendering, surface);

        Sha256(surface).Should().Be(
            ParserSurfaceFingerprint,
            "the events ClaudeStreamJsonParser produces from the captured CLI output have changed, so the "
            + "transcript a user reads has too. That is what the live tier drove and what "
            + $"{AgentRuntimeCatalog.For(AgentTool.Claude).ProtocolVerifiedVersion} was recorded for, so run "
            + "ClaudeCliContractTests against that binary again (ADR 0023 step 4) before recording the new "
            + $"surface. The full rendering this hashed is at {rendering}: diff it against the previous one "
            + "to see which event moved");
    }

    /// <summary>
    /// Every event every captured recording yields, rendered so that the digest is about what the studio
    /// would show and nothing else: timestamps are dropped (they are the test's own clock), and values are
    /// formatted invariantly so the answer is the same on any machine.
    /// <para>
    /// One replay per recording, because there is only one parser now: it is no longer told whether a
    /// schema went out with the process, and the same bytes mean the same thing either way (every
    /// assistant text block commits where it arrived; only the document at the result line is compared
    /// against what was already committed).
    /// </para>
    /// </summary>
    private static string ParserSurface()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures");
        var recordings = Directory.GetFiles(directory, "*.jsonl")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
        recordings.Should().NotBeEmpty($"the captured CLI output should have been copied to {directory}");

        var surface = new StringBuilder();
        foreach (var recording in recordings)
        {
            surface.Append(Path.GetFileName(recording)).Append('\n');

            var parser = new ClaudeStreamJsonParser(() => Epoch);
            List<AgentEvent> events = [.. File.ReadAllLines(recording).SelectMany(parser.Parse)];

            // Drain is materialised separately: it reports what the turn was still holding, so it has
            // to run AFTER the lines, which a lazily concatenated query would not guarantee.
            events.AddRange(parser.Drain());
            foreach (var e in events)
            {
                surface.Append("  ").Append(Render(e)).Append('\n');
            }
        }

        return surface.ToString();
    }

    /// <summary>
    /// The parser's output over <c>Sessions/Fixtures/*.jsonl</c>, as it stood when this guard was written.
    /// <para>
    /// Honest about its own provenance: the command-line record above IS what was driven live against
    /// 2.1.222 on <see cref="VerifiedOn"/> (<c>BuildArguments</c> has not moved since), but this one is NOT.
    /// The parser has been rewritten several times since that run, most recently on 2026-08-07 when the
    /// held-prose design was removed outright, and those measurements were taken against 2.1.223. So this
    /// constant is a baseline for catching the NEXT drift, and the re-run it already owes is recorded in
    /// ADR 0023 rather than papered over here.
    /// </para>
    /// <para>
    /// Re-recorded 2026-08-07 with the held-prose removal, and the diff was read line by line before it
    /// was taken: every assistant text block now appears where the model wrote it (narration above its
    /// card, an answer attempt above the tool call that followed it), the document adds a bubble only on
    /// the two captures where it is a REVISION of the prose rather than a repeat, and the two multi-block
    /// turns render two bubbles instead of one joined message. The recording taken that day,
    /// <c>claude-typed-revised-answer-turn.jsonl</c>, is in the surface too.
    /// </para>
    /// <para>
    /// Re-recorded 2026-08-08 when <c>AgentStatsSnapshot</c> grew its fixed-prefix baseline (the context
    /// bar's legibility work), and this diff was verified MECHANICALLY rather than eyeballed: stripping
    /// exactly the three new properties (<c>BaselineFraction</c>, <c>BaselineTokens</c>,
    /// <c>ConversationTokens</c>) from the new rendering reproduces the previous fingerprint
    /// (135e5ab5...) byte for byte, so no other event moved. The same change gated context readings off
    /// sidechain lines (<c>parent_tool_use_id</c>), which alters nothing here: no captured recording
    /// contains one (grepped 2026-08-08).
    /// </para>
    /// <para>
    /// Re-recorded 2026-08-08 again for backlog 104 (D2 and D3), and this diff was verified MECHANICALLY
    /// by regenerating the previous surface from a clean checkout: it hashed to the constant this line
    /// replaced (<c>14f722c4...</c>), and the line-by-line diff against the new one is exactly, and only,
    /// the two fixes. D3: every <c>ToolCallCompleted</c> gained an <c>Error=null</c> (no captured recording
    /// carries a Bash <c>tool_use_result.stderr</c>), and three <c>Read</c> results whose content held a
    /// newline now show it (<c>...4471\n2\t</c>) instead of the space the one-line flattener left. D2: the
    /// five untyped turns whose <c>result</c> string repeated an assistant block already committed now
    /// carry <c>ResultText=null</c> on the turn row rather than the whole answer a second time. Nothing
    /// else moved. This surface is still a baseline for the NEXT drift, not itself a live-driven claim
    /// (see the paragraph above); the D3 stderr extraction in particular reads a shape no captured
    /// recording exercises, so <see cref="ClaudeCliContractTests"/> still owns proving it against a real
    /// Bash result (ADR 0023).
    /// </para>
    /// <para>
    /// Re-recorded 2026-09-14 for backlog 25 (increments 6 and 7), and this diff was verified MECHANICALLY:
    /// removing exactly the two changes from the new rendering reproduces the constant this line replaced
    /// (<c>18a341fd...</c>) byte for byte. The changes: every <c>TurnCompleted</c> gained <c>ErrorText</c>
    /// (null everywhere except the two error outcomes, which now carry the CLI's own
    /// <c>Reached maximum budget ($0.0001)</c> and <c>Failed to provide valid structured output after 5
    /// attempts</c>), and the budget-exhausted turn's <c>StatsUpdated</c> reads its tokens from
    /// <c>modelUsage</c> (544 in, 16 out) instead of the zeroed usage block (0, 0), an decision. The
    /// whole contract file was driven green against 2.1.222 the same day, after these parser changes.
    /// </para>
    /// <para>
    /// Re-recorded 2026-10-22 for work item #351, verified MECHANICALLY: deleting every <c>Arguments=null, </c> from
    /// the new rendering reproduces the constant this line replaced (<c>3a8ab734...</c>) byte for byte. The change:
    /// <c>ToolCallStarted</c> gained <c>Arguments</c>, which the parser fills only for a studio tool call
    /// (<c>mcp__fravashi__</c>), and no captured recording contains one (counted 2026-10-22: every one of the
    /// rendering's <c>Arguments</c> is null), so no event a person reads moved. The supervising host session also drove
    /// the live tier the same day against the pinned 2.1.280 on the Windows dev host with this change in the tree: 25 of
    /// 26 passed, and the one that failed (<c>The_search_tools_are_there_for_a_session_that_names_them</c>, no tool call
    /// at all) fails identically on the commit before this change, so it is not this record. (It was first put down to
    /// the Windows host; work item #353 found the real cause, the opening note every new session's first turn carries,
    /// and rewrote that test on 2026-10-23, after which the whole file passed 14 of 14 on the same host.)
    /// </para>
    /// </summary>
    private const string ParserSurfaceFingerprint =
        "2d02911513feaf227276f230efb64e6174624e2610cddadca48619589580bb4c";

    private static string Render(AgentEvent e) =>
        e.GetType().Name + "(" + string.Join(
            ", ",
            Properties(e.GetType())
                .Where(p => p.Name != nameof(AgentEvent.Timestamp))
                .Select(p => p.Name + "=" + Render(p.GetValue(e), 1))) + ")";

    private static string Render(object? value, int depth) => value switch
    {
        null => "null",
        string text => "\""
            + text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
            + "\"",
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),

        // Records nest at most a couple of levels here; the cap is what stops a surprise graph recursing.
        _ when depth >= 3 => value.GetType().Name,
        _ => value.GetType().Name + "(" + string.Join(
            ", ",
            Properties(value.GetType()).Select(p => p.Name + "=" + Render(p.GetValue(value), depth + 1))) + ")",
    };

    /// <summary>
    /// Read by name rather than by declaration order, which reflection does not promise, and taken from the
    /// runtime type so a property added to an event tomorrow is in the record without anyone remembering.
    /// </summary>
    private static IOrderedEnumerable<PropertyInfo> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal);

    // ---- the third production surface: the hook contract the studio writes ----------------
    //
    // The two records above cover what the studio SENDS the CLI and what it READS back. The contract
    // file drives a third thing between them, and a reviewer found it uncovered:
    // A_real_tool_call_is_gated_by_the_pre_tool_use_hook is not about the command line or the parser at
    // all. It is about the per-session settings file and approval script SessionHookWriter lays down,
    // and the decision document AgentApprovalEndpoint.Render answers the blocked script with. Narrow the
    // "*" matcher to a tool list, rename a field in that document, or reword the fail-closed refusal the
    // test matches on ("could not be reached"), and the contract test's subject changes underneath a
    // green build while ProtocolVerifiedVersion goes on asserting a verification that no longer covers
    // what the studio writes.
    //
    // Held the same way as the command line and for the same reason: the DERIVED artifacts rather than
    // the two files' bytes, written out as literals rather than hashed so a failure names the key that
    // moved. Both files are otherwise ordinary product code that will be refactored, and a gate that
    // demanded a billable live re-run for a renamed private helper is a gate that gets suppressed.

    /// <summary>
    /// The settings file the CLI is started with, exactly as it was written for the run that earned
    /// <see cref="AgentRuntimeDescriptor.ProtocolVerifiedVersion"/>: a PreToolUse entry with a catch-all
    /// matcher running the session's own script. The session folder is a placeholder (it is a temp path
    /// here and a data-volume path in production), and so is the timeout VALUE: the CLI honors whatever
    /// number of seconds it is handed, so moving the broker's own timeout is a product change and
    /// charging it a live re-run would teach everyone to edit this constant. The KEY is held; that the
    /// number stays above what the studio will hold a call for is SessionHookWriterTests' job.
    /// <para>
    /// The command gained its exit-2 fallback on 2026-09-15 (backlog 115): measured against 2.1.222, a hook
    /// that fails any other way is not a refusal, so the shell turns an unclean end of the script into the one
    /// failure the CLI blocks on. Recorded together with the contract file that drives it.
    /// </para>
    /// <para>
    /// The three account switches came on 2026-09-30 (backlog 174): signed in with a person's subscription, the
    /// pinned 2.1.280 loaded their claude.ai connectors and the plugins and skills synced from claude.ai into every
    /// session, and these hold them off for the invocation. Driven with the contract file's test that reads the
    /// session's init line.
    /// </para>
    /// <para>
    /// The empty attribution came the same evening (backlog 179): the pinned CLI's commit guidance put a model trailer
    /// on an agent's commit in a repository that forbids one, and an empty text hides it.
    /// </para>
    /// </summary>
    private const string VerifiedHookSettings = """
    {
      "disableClaudeAiConnectors": true,
      "syncClaudeAiPlugins": false,
      "syncClaudeAiSkills": false,
      "attribution": {
        "commit": "",
        "pr": ""
      },
      "hooks": {
        "PreToolUse": [
          {
            "matcher": "*",
            "hooks": [
              {
                "type": "command",
                "command": "sh \u0027<session>/approve.sh\u0027 || { echo \u0027Fravashi: the approval hook ended without answering, so this tool call is refused.\u0027 \u003E\u00262; exit 2; }",
                "timeout": <seconds>
              }
            ]
          }
        ]
      }
    }
    """;

    /// <summary>
    /// The script the CLI actually runs before every tool call, as driven. Every line of it is protocol
    /// with something: the header name and the POST body are what the studio's endpoint answers, the
    /// refusal text is what the contract test reads back out of the CLI's tool result, and the two
    /// fail-closed branches are the whole safety claim. The secret is a placeholder because a real one is
    /// generated per session; the URL is fixed by the test rather than taken from
    /// <see cref="AgentApprovalEndpoint.Route"/>, because which port and path the studio serves is ours
    /// on both ends and no CLI ever sees it.
    /// </summary>
    private const string VerifiedHookScript = """
    #!/bin/sh
    # Fravashi session approval hook (plan 2e). The CLI runs this before every tool call and
    # waits for what it prints; it asks the studio, which asks the user.
    #
    # It FAILS CLOSED: every failure path prints a refusal, and the only approval this script
    # can ever emit is one the studio itself answered with.
    DENY='{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"The studio could not be reached, so this tool call was refused."}}'

    RESPONSE=$(curl --silent --show-error --max-time <seconds> \
      --request POST \
      --header 'Content-Type: application/json' \
      --header 'X-Fravashi-Session: <secret>' \
      --data-binary @- \
      'http://127.0.0.1:8080/api/agent/approval' 2>/dev/null)

    if [ $? -ne 0 ] || [ -z "$RESPONSE" ]; then
      printf '%s\n' "$DENY"
      exit 0
    fi

    # Only a real decision document is forwarded. Anything else (a proxy error page, an
    # HTML login redirect, a truncated body) is refused: verified in the container, where a
    # 502 page was otherwise passed through to the CLI verbatim.
    case "$RESPONSE" in
      *'"hookSpecificOutput"'*'"permissionDecision"'*) printf '%s\n' "$RESPONSE" ;;
      *) printf '%s\n' "$DENY" ;;
    esac

    """;

    /// <summary>
    /// The decision document the CLI honored, in both of its answers. The contract file drives both: the
    /// gating test observes a deny stopping a real Bash call, and the two typed-turn tests only pass
    /// because an allow lets the terminating document through (ADR 0022).
    /// </summary>
    private const string VerifiedAllowDocument =
        """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","permissionDecisionReason":"reason"}}""";

    private const string VerifiedDenyDocument =
        """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"reason"}}""";

    [Fact]
    public void The_recorded_verification_still_covers_the_hook_contract_the_studio_writes()
    {
        var (settings, script) = HookSurface();

        // Both sides are compared as LF: the source file's own line endings depend on how the repository
        // was checked out, and a false alarm from a CRLF clone is exactly what gets a gate deleted. That
        // the SCRIPT ships LF (a CRLF shebang does not exec in the container) is a separate assertion,
        // and it lives in SessionHookWriterTests where it belongs.
        settings.Should().Be(VerifiedHookSettings.ReplaceLineEndings("\n"), HookDrift);
        script.Should().Be(VerifiedHookScript.ReplaceLineEndings("\n"), HookDrift);

        AgentApprovalEndpoint.Render(ToolApprovalDecision.Allow("reason"))
            .Should().Be(VerifiedAllowDocument, HookDrift);
        AgentApprovalEndpoint.Render(ToolApprovalDecision.Deny("reason"))
            .Should().Be(VerifiedDenyDocument, HookDrift);
    }

    // ---- the fourth: the tool server the studio hosts (backlog 84) --------------------------
    //
    // Since 2026-09-15 every session is also started with --mcp-config naming the studio's own tool server
    // (ADR 0019). That is a contract with the CLI in the same sense as the hook: a file the studio writes and
    // the CLI reads (the transport type, the URL key, the header carrying the secret), and a wire protocol the
    // studio answers (the MCP version it accepts and where it finds the call id it keys its ledger on). Any of
    // those can move with the contract file byte identical, so they are held here the same way, as literals.

    /// <summary>
    /// The MCP config the CLI read on the run that earned the record. The URL is the test's own and the secret
    /// is the placeholder, for the reasons <see cref="VerifiedHookScript"/> gives.
    /// </summary>
    private const string VerifiedToolServerConfig = """
    {
      "mcpServers": {
        "fravashi": {
          "type": "http",
          "url": "http://127.0.0.1:8080/api/agent/tools",
          "headers": {
            "X-Fravashi-Session": "\u003Csecret\u003E"
          }
        }
      }
    }
    """;

    /// <summary>
    /// What the tool server accepts from the CLI, as driven: the protocol version(s) it negotiates and the
    /// <c>_meta</c> key the pinned CLI put the model's tool-use id under (measured 2026-09-15 against 2.1.222).
    /// </summary>
    private const string VerifiedToolServerProtocol = "versions=2025-11-25; operation-key=claudecode/toolUseId";

    [Fact]
    public void The_recorded_verification_still_covers_the_tool_server_contract_the_studio_writes()
    {
        SessionHookWriter.ToolServerConfig("http://127.0.0.1:8080/api/agent/tools", "<secret>")
            .ReplaceLineEndings("\n")
            .Should().Be(VerifiedToolServerConfig.ReplaceLineEndings("\n"), ToolServerDrift);

        ("versions=" + string.Join(",", StudioToolServer.SupportedProtocolVersions)
                + "; operation-key=" + StudioToolServer.OperationKeyMeta)
            .Should().Be(VerifiedToolServerProtocol, ToolServerDrift);
    }

    private const string ToolServerDrift =
        "the tool-server contract the studio writes or answers has changed, and what "
        + nameof(AgentRuntimeDescriptor.ProtocolVerifiedVersion) + " claims is that a real CLI of that "
        + "version read this MCP config and was served under this protocol. Install the pinned binary, run "
        + "ClaudeCliContractTests with the live tier on (ADR 0023 step 4), and only then record the new shape here";

    private const string HookDrift =
        "the PreToolUse hook contract the studio writes has changed, and what "
        + nameof(AgentRuntimeDescriptor.ProtocolVerifiedVersion) + " claims is that a real CLI of that "
        + "version was driven against exactly this one: it read this settings file, ran this script, and "
        + "honored this decision document. Install the pinned binary, run ClaudeCliContractTests with the "
        + "live tier on (ADR 0023 step 4), and only then record the new shape here";

    /// <summary>
    /// The settings file and script the writer really produces, with the two things that legitimately
    /// differ between runs and machines replaced: the session folder (a temp path here) and the timeout
    /// values, which both come from the broker's own wait. The secret needs no such treatment, because
    /// the placeholder below IS what this session's secret is.
    /// </summary>
    private static (string Settings, string Script) HookSurface()
    {
        var root = Path.Combine(Path.GetTempPath(), "fravashi-hook-surface-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new SessionHookWriter(root, VerifiedApprovalUrl)
                .Write(new AgentSessionId(StableSessionId), "<secret>");

            // The settings file carries the folder JSON-ESCAPED, and on Windows that means doubled
            // backslashes, so the raw path never appears in it literally. Escaping the folder the same way
            // the writer did, separator included, is what makes this record identical on both platforms
            // while still holding the name of the script and the quoting around it.
            var folder = JsonText(files.Directory + Path.DirectorySeparatorChar);

            return (
                Normalise(File.ReadAllText(files.SettingsPath).Replace(folder, "<session>/", StringComparison.Ordinal)),
                Normalise(File.ReadAllText(files.ScriptPath)));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Where the hook was pointed for this record: the same dead loopback address the gating contract test
    /// uses. Written out rather than built from <see cref="AgentApprovalEndpoint.Route"/> on purpose, so
    /// moving the studio's own route does not fire a gate about the CLI's protocol.
    /// </summary>
    private const string VerifiedApprovalUrl = "http://127.0.0.1:8080/api/agent/approval";

    /// <summary>A string as JSON would spell it, without the quotes: the writer's own escaping.</summary>
    private static string JsonText(string value) => JsonSerializer.Serialize(value).Trim('"');

    /// <summary>
    /// Replaces the two numbers that come from the broker's wait rather than from the CLI's protocol. The
    /// key and the flag they sit behind stay, so dropping either, or turning the timeout into a string, is
    /// still a difference; only the value moves freely.
    /// </summary>

    private static string Normalise(string text) => Regex
        .Replace(
            Regex.Replace(text, @"""timeout"": \d+", @"""timeout"": <seconds>"),
            @"--max-time \d+",
            "--max-time <seconds>")
        .ReplaceLineEndings("\n");

    [Fact]
    public void A_runtime_whose_protocol_was_never_verified_claims_nothing()
    {
        // The rule, not an assertion about any one runtime: a version may be claimed as verified only
        // where a contract tier actually exists to have driven it. Every runtime this version offers has
        // one, so the check is that nothing claims a verification the repository cannot show. The two
        // runtimes this once named were pinned and driven as one-shot commands with no contract tier at
        // all, and were retired to v2 (docs/v2.md section 1) still claiming nothing.
        foreach (var descriptor in AgentRuntimeCatalog.All.Where(d => d.ProtocolVerifiedVersion is not null))
        {
            descriptor.Tool.Should().Be(
                AgentTool.Claude,
                "this file records the contract run that earns a verified version, and it records exactly "
                + "one; a runtime claiming one without a record here is the lie this whole file exists to "
                + "catch, in a new place");
        }
    }

    [Fact]
    public void The_image_records_exactly_the_pinned_versions()
    {
        // The failure this catches: someone bumps the catalog and forgets the Dockerfile (or the reverse). Since
        // 2026-10-29 the image does not carry the CLI (DependencyLicenseGateTests holds that); update-on-launch
        // installs the catalog's pin at the first start, and the ARG stays the one line a reader finds it on.
        var path = Path.Combine(RepositoryRoot(), "Dockerfile");
        File.Exists(path).Should().BeTrue($"the Dockerfile should exist at {path}");
        var dockerfile = File.ReadAllText(path);

        foreach (var descriptor in AgentRuntimeCatalog.All)
        {
            var arg = BuildArgs[descriptor.Tool];
            Regex.IsMatch(dockerfile, $@"(?m)^ARG {arg}={Regex.Escape(descriptor.PinnedVersion)}\s*$")
                .Should().BeTrue($"the Dockerfile should declare ARG {arg}={descriptor.PinnedVersion}");
        }

        dockerfile.Should().NotContain("@latest", "nothing the image installs from npm may float");
    }

    [Fact]
    public void IsOffPin_reports_drift_only_when_a_version_is_actually_known()
    {
        var descriptor = AgentRuntimeCatalog.For(AgentTool.Claude);

        // An unknown version is not drift: on a developer host nothing installs the CLI at all, and during
        // startup the probe has not answered yet. Warning in either case would be noise with no observation.
        descriptor.IsOffPin(null).Should().BeFalse();
        descriptor.IsOffPin(string.Empty).Should().BeFalse();
        descriptor.IsOffPin("   ").Should().BeFalse();

        descriptor.IsOffPin(descriptor.PinnedVersion).Should().BeFalse();
        descriptor.IsOffPin($"  {descriptor.PinnedVersion}  ").Should().BeFalse();

        descriptor.IsOffPin("9.9.9").Should().BeTrue();
    }
}
