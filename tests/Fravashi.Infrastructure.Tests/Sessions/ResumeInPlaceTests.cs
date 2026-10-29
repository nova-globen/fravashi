using AwesomeAssertions;
using AwesomeAssertions.Execution;
using Fravashi.Application.Framework;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Framework;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// The owner's report of 2026-09-15: Resume showed an empty chat, added a SECOND row for the same
/// conversation, and the resumed turn failed with "No conversation found with session ID". Driven end to
/// end here, the registry over the real runner over a real child process, because every half of the bug
/// lived in the seam between them: the registry minted a new session id, the runner gave that id a new
/// config home, and the runner's cleanup deleted the old config home, conversation file and all, the
/// moment the first session ended.
/// <para>
/// The stand-in CLI keeps conversations the way the real one does (FAKE_CLI_CONVERSATIONS): a resume
/// fails with the real sentence unless the conversation file is under the config home it was started with.
/// A framework skill is stored so the config home is the PER-SESSION one, which is the owner's case.
/// </para>
/// </summary>
public class ResumeInPlaceTests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fravashi-resume-" + Guid.NewGuid().ToString("N"));

    private readonly string _copy;
    private readonly Dictionary<string, string> _cliEnvironment;
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly SqliteSessionStore _store;
    private readonly SessionFrameworkProjector _framework;
    private readonly LiveSessionRegistry _registry;

    public ResumeInPlaceTests()
    {
        _copy = Path.Combine(_scratch, "workspaces", "w1");
        Directory.CreateDirectory(_copy);

        var factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_scratch, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(factory);
        _store = new SqliteSessionStore(factory);

        var artifacts = new InMemoryFrameworkStore();
        artifacts.Put(FrameworkArtifactKind.Skill, "test-skill", FrameworkPayload<SkillDefinition>.Serialize(
            new SkillDefinition { Name = "test-skill", Description = "A stored skill.", Content = "Body." }));
        _framework = new SessionFrameworkProjector(_scratch, artifacts, sourceConfigHome: () => null);

        // Read by the runner at each child's start, so a test can change how the NEXT child behaves.
        _cliEnvironment = new Dictionary<string, string>
        {
            ["FAKE_CLI_CONVERSATIONS"] = "1",
            ["FAKE_CLI_MODE"] = "reply",
            ["FAKE_CLI_EXIT_AFTER"] = "1",
            ["FAKE_CLI_REPORT"] = Path.Combine(_scratch, "cli-report.json"),
        };

        var runner = new ClaudeSessionRunner(
            _broker,
            new SessionHookWriter(_scratch, "http://127.0.0.1:9/api/agent/approval"),
            launcher: new AgentCliLauncher("node", [FakeCliScript()]),
            environment: _cliEnvironment,
            framework: _framework);

        _registry = new LiveSessionRegistry(
            runner, _store, _broker, conversations: new AgentConversationFiles(_scratch, () => null));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _registry.Dispose();
        _broker.Dispose();
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string FakeCliScript() =>
        Path.Combine(AppContext.BaseDirectory, "Sessions", "FakeCli", "fake-agent-cli.js");

    /// <summary>
    /// Work item #323, extended by #333: a brand new session's opening turn carries the studio's note (the git
    /// fact, then the work gate's steps and the commit reviewer's failure classes) ahead of the user's own
    /// words, so the fake CLI's echo (joining every content block's text with no separator) echoes the note
    /// immediately before whatever the test actually sent on that first turn.
    /// </summary>
    private static string FirstTurnEcho(string text) =>
        "echo: " + AgentGitInBash.OpeningNote + " " + AgentFirstTurnBriefing.Note + text;

    private static async Task Eventually(Func<bool> condition, string because = "")
    {
        // A DEADLINE, not a fixed number of attempts. The budgets here used to be 2, 5, 10 and 15
        // seconds with no principle behind the numbers, and the 2 second ones failed on CI: run 103
        // and run 107 on 2026-09-19 went red on commits that changed only markdown, because the
        // runner was a shared host that also ran production, and a contended machine needs longer to
        // reach a turn boundary than this dev host does. A generous ceiling costs nothing when the
        // condition is met quickly, since the loop exits the moment it is true; it only matters on a
        // machine slow enough that the old limit was measuring the machine rather than the behaviour.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10); // poll interval inside a wait helper (backlog 107)
        }

        condition().Should().BeTrue(because);
    }

    /// <summary>The CLI's own file for this conversation, wherever under the session's config home it landed.</summary>
    private string? ConversationFile(AgentSessionId id)
    {
        var projects = Path.Combine(_framework.ConfigDirectoryFor(id), "projects");
        return Directory.Exists(projects)
            ? Directory.EnumerateFiles(projects, id.Value.ToString("D") + ".jsonl", SearchOption.AllDirectories)
                .FirstOrDefault()
            : null;
    }

    private async Task<LiveAgentSession> EndedConversationAsync(string prompt = "the first thing")
    {
        var first = _registry.Start(new SessionStartRequest("w1", _copy, prompt));
        await Eventually(() => !first.IsRunning, "the stand-in exits after answering one turn");
        await Eventually(() => _store.Find(first.Id)?.State == AgentSessionState.Completed);
        return first;
    }

    private IEnumerable<string> Messages(AgentSessionId id) =>
        _store.Transcript(id).OfType<MessageEmitted>()
            .Where(m => m.Role is TranscriptRole.User or TranscriptRole.Assistant)
            .Select(m => m.Text);

    [Fact]
    public async Task A_resumed_conversation_continues_in_place_where_the_cli_can_find_it()
    {
        var first = await EndedConversationAsync();
        var fileAfterTheFirstEnded = ConversationFile(first.Id);

        var resumed = _registry.Start(new SessionStartRequest("w1", _copy, "the second thing", ResumeOf: first.Id));
        await Eventually(() => !resumed.IsRunning);
        await Eventually(() => _store.Find(resumed.Id)?.State is not AgentSessionState.Running);

        using (new AssertionScope())
        {
            fileAfterTheFirstEnded.Should().NotBeNull("the CLI's conversation file must survive its session ending");
            resumed.FailureReason.Should().BeNull("the resumed child must find the conversation it resumes");
            resumed.State.Should().Be(AgentSessionState.Completed);
            resumed.Id.Should().Be(first.Id, "a resumed conversation is the same conversation");
            _registry.History("w1").Should().ContainSingle("one conversation is one row");
            Messages(first.Id).Should().Equal(
                "the first thing", FirstTurnEcho("the first thing"), "the second thing", "echo: the second thing");
            ConversationFile(first.Id).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Resume_shows_the_conversation_at_once_and_the_next_message_continues_it()
    {
        var first = await EndedConversationAsync();

        var resumed = _registry.Resume(first.Id, _copy);

        resumed.Id.Should().Be(first.Id);
        resumed.IsRunning.Should().BeTrue("the composer is armed before anything is typed");
        resumed.Transcript.OfType<MessageEmitted>().Select(m => m.Text)
            .Should().ContainInOrder("the first thing", FirstTurnEcho("the first thing"));

        await Eventually(() => resumed.Transcript.OfType<SessionStarted>().Count() == 2, "the resumed child is up");
        (await _registry.SendAsync(first.Id, "the second thing")).Should().BeTrue();
        await Eventually(() => !resumed.IsRunning);

        resumed.FailureReason.Should().BeNull();
        _registry.History("w1").Should().ContainSingle();
        Messages(first.Id).Should().Equal(
            "the first thing", FirstTurnEcho("the first thing"), "the second thing", "echo: the second thing");
    }

    [Fact]
    public async Task Resuming_carries_the_conversations_earlier_spending_rather_than_restarting_it_at_zero()
    {
        // The usage the CLI reports is cumulative per PROCESS, and the store folds the newest reading into
        // the session's one summary row. Resumed under the same id, a fresh process would overwrite that
        // row with its own smaller figure, and a spend ceiling would stop counting the first half.
        var first = await EndedConversationAsync();
        var before = first.Stats.CostUsd;
        before.Should().BeGreaterThan(0m);

        var resumed = _registry.Start(new SessionStartRequest("w1", _copy, "the second thing", ResumeOf: first.Id));
        await Eventually(() => !resumed.IsRunning);

        resumed.Stats.CostUsd.Should().Be(before * 2, "each answered turn costs the same in the stand-in");
        _registry.UsageTotals().AllTime.CostUsd.Should().Be(before * 2);
    }

    [Fact]
    public async Task Continue_after_an_exhausted_turn_takes_the_same_path()
    {
        // Backlog 25 increment 6's Continue is a resume too, so it must find the conversation as well.
        _cliEnvironment["FAKE_CLI_MODE"] = "exhaust";
        var first = _registry.Start(new SessionStartRequest("w1", _copy, "the first thing"));
        await Eventually(() => first.IsResumable);

        _cliEnvironment["FAKE_CLI_MODE"] = "reply";
        var continued = _registry.Continue(first.Id, _copy);
        await Eventually(() => continued.Transcript.OfType<SessionStarted>().Count() == 2, "the resumed child is up");
        (await _registry.SendAsync(first.Id, "try again")).Should().BeTrue();
        await Eventually(() => !continued.IsRunning);

        continued.Id.Should().Be(first.Id);
        continued.FailureReason.Should().BeNull();
        continued.State.Should().Be(AgentSessionState.Completed);
        _registry.History("w1").Should().ContainSingle();
        Messages(first.Id).Should().ContainInOrder("the first thing", "try again", "echo: try again");
    }

    [Fact]
    public async Task Deleting_a_conversation_the_cli_really_wrote_removes_its_conversation_file()
    {
        var first = await EndedConversationAsync();
        var file = ConversationFile(first.Id);
        file.Should().NotBeNull("kept while the conversation exists, so it can be resumed");

        _registry.Delete(first.Id).Should().BeTrue();

        File.Exists(file).Should().BeFalse();
        Directory.Exists(Path.Combine(_scratch, "agent-sessions", first.Id.ToString())).Should().BeFalse();
    }
}
