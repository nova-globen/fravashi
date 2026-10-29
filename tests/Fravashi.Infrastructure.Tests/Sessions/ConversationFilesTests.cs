using System.Threading.Channels;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Insights;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// The agent CLI's conversation files after resume-in-place (adversarial review of 2026-09-15): they are kept
/// while the conversation exists and removed with it, a conversation whose file an older build deleted is not
/// offered a Resume that could only fail, and correcting a stale Running row cannot end a resume that started
/// under it.
/// </summary>
public sealed class ConversationFilesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fravashi-conversation-files-" + Guid.NewGuid().ToString("N"));

    private readonly string _defaultHome;
    private readonly SqliteSessionStore _sqlite;
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly AgentConversationFiles _files;

    public ConversationFilesTests()
    {
        _defaultHome = Path.Combine(_scratch, "default-home");
        Directory.CreateDirectory(_scratch);
        var factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_scratch, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(factory);
        _sqlite = new SqliteSessionStore(factory);
        _files = new AgentConversationFiles(_scratch, () => _defaultHome);
    }

    public void Dispose()
    {
        _broker.Dispose();
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class IdleRunner : IAgentSessionRunner
    {
        public int Runs;

        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Runs);
            return Channel.CreateUnbounded<AgentEvent>().Reader.ReadAllAsync(cancellationToken);
        }

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public bool Stop(AgentSessionId sessionId) => true;

        public bool ChangeModel(AgentSessionId sessionId, string? model) => true;
    }

    private LiveSessionRegistry Registry(ISessionStore? store = null) =>
        new(new IdleRunner(), store ?? _sqlite, _broker, clock: () => Now, conversations: _files);

    private AgentSessionId StoredConversation(AgentSessionState state = AgentSessionState.Completed, string? provider = null)
    {
        var id = AgentSessionId.New();
        _sqlite.Save(new SessionRecord(
            id, "w1", AgentTool.Claude, "a conversation", provider ?? id.Value.ToString("D"), state,
            Now.AddDays(-1), state.IsTerminal() ? Now.AddDays(-1) : null));
        return id;
    }

    private static string PutConversation(string configHome, string provider)
    {
        var file = Path.Combine(configHome, "projects", "-workspaces-w1", provider + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{}\n");
        return file;
    }

    private string SessionHome(AgentSessionId id) => Path.Combine(_files.SessionFolderFor(id), "claude");

    // ---- blocker 2: deleting a conversation removes its CLI files -----------------------------

    [Fact]
    public void Deleting_a_conversation_removes_its_session_folder_with_the_cli_conversation_in_it()
    {
        var id = StoredConversation();
        var file = PutConversation(SessionHome(id), id.Value.ToString("D"));
        using var registry = Registry();

        registry.Delete(id).Should().BeTrue();

        File.Exists(file).Should().BeFalse("the unredacted CLI transcript must not outlive the conversation");
        Directory.Exists(_files.SessionFolderFor(id)).Should().BeFalse();
    }

    [Fact]
    public void Deleting_a_conversation_that_lived_in_the_default_home_removes_that_file_and_only_that_one()
    {
        var id = StoredConversation();
        var file = PutConversation(_defaultHome, id.Value.ToString("D"));
        var someoneElses = PutConversation(_defaultHome, Guid.NewGuid().ToString("D"));
        using var registry = Registry();

        registry.Delete(id).Should().BeTrue();

        File.Exists(file).Should().BeFalse();
        File.Exists(someoneElses).Should().BeTrue("the default home is the CLI's own; only this conversation goes");
    }

    [Fact]
    public void A_provider_id_that_is_not_a_plain_file_name_never_reaches_a_path()
    {
        var id = StoredConversation(provider: "../../default-home/projects/-workspaces-w1/victim");
        var victim = PutConversation(_defaultHome, "victim");
        using var registry = Registry();

        registry.Delete(id).Should().BeTrue();

        File.Exists(victim).Should().BeTrue();
    }

    // ---- blocker 3: no Resume for a conversation whose file is gone ---------------------------

    [Fact]
    public void A_conversation_whose_cli_file_was_removed_is_not_offered_a_resume_and_says_why()
    {
        var id = StoredConversation();
        using var registry = Registry();

        registry.HistoryRemoved(id).Should().BeTrue();
        registry.CanResume(id).Should().BeFalse();
        var resume = () => registry.Resume(id, "/workspaces/w1");
        resume.Should().Throw<InvalidOperationException>();
        var viaStart = () => registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "carry on", ResumeOf: id));
        viaStart.Should().Throw<InvalidOperationException>().WithMessage(LiveSessionRegistry.HistoryRemovedNotice);
    }

    [Fact]
    public void A_conversation_is_resumable_while_its_file_is_in_the_session_home_or_the_default_home()
    {
        var inSessionHome = StoredConversation();
        PutConversation(SessionHome(inSessionHome), inSessionHome.Value.ToString("D"));
        var inDefaultHome = StoredConversation();
        PutConversation(_defaultHome, inDefaultHome.Value.ToString("D"));
        using var registry = Registry();

        registry.HistoryRemoved(inSessionHome).Should().BeFalse();
        registry.CanResume(inSessionHome).Should().BeTrue();
        registry.HistoryRemoved(inDefaultHome).Should().BeFalse();
        registry.CanResume(inDefaultHome).Should().BeTrue();
    }

    // ---- item 4: settling a stale row cannot end a resume that started under it ---------------

    /// <summary>
    /// Runs <see cref="OnFirstCancelledSave"/> on another thread at the exact moment a stale row is about to be
    /// saved as Cancelled, and gives it a bounded time to finish before the save goes through.
    /// </summary>
    private sealed class InterleavingStore(ISessionStore inner) : ISessionStore
    {
        private int _fired;

        public Action? OnFirstCancelledSave { get; set; }

        public void Save(SessionRecord record)
        {
            if (record.State == AgentSessionState.Cancelled && OnFirstCancelledSave is { } interleave
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                Task.Run(interleave).Wait(TimeSpan.FromMilliseconds(500));
            }

            inner.Save(record);
        }

        public SessionRecord? Find(AgentSessionId id) => inner.Find(id);

        public IReadOnlyList<SessionRecord> ForWorkspace(string workspaceId, int max = 50) => inner.ForWorkspace(workspaceId, max);

        public IReadOnlyList<SessionRecord> Recent(int max = 10) => inner.Recent(max);

        public void Append(AgentSessionId id, IEnumerable<AgentEvent> events) => inner.Append(id, events);

        public IReadOnlyList<AgentEvent> Transcript(AgentSessionId id) => inner.Transcript(id);

        public void Delete(AgentSessionId id) => inner.Delete(id);

        public SessionUsageTotals ReadUsageTotals(DateTimeOffset dayStartUtc, DateTimeOffset monthStartUtc) =>
            inner.ReadUsageTotals(dayStartUtc, monthStartUtc);

        public IReadOnlyList<SessionDay> ReadSessionDays(DateOnly fromDayUtc, DateOnly toDayUtc) =>
            inner.ReadSessionDays(fromDayUtc, toDayUtc);

        public IReadOnlyList<DayCount> ReadTurnDays(DateOnly fromDayUtc, DateOnly toDayUtc) =>
            inner.ReadTurnDays(fromDayUtc, toDayUtc);

        public DurationStats ReadSessionDurations(DateTimeOffset fromUtc) => inner.ReadSessionDurations(fromUtc);
    }

    [Fact]
    public void Settling_a_stale_running_row_cannot_overwrite_a_resume_that_started_under_it()
    {
        // The list reads a row still marked Running from before a restart, and between deciding it is an
        // orphan and saving it as Cancelled, a resume of that very conversation starts and saves Running.
        var id = StoredConversation(AgentSessionState.Running);
        PutConversation(SessionHome(id), id.Value.ToString("D"));
        var store = new InterleavingStore(_sqlite);
        using var registry = Registry(store);
        LiveAgentSession? resumed = null;
        Exception? refused = null;
        store.OnFirstCancelledSave = () =>
        {
            try
            {
                resumed = registry.Resume(id, "/workspaces/w1");
            }
            catch (InvalidOperationException ex)
            {
                refused = ex;
            }
        };

        registry.History("w1");
        SpinWait.SpinUntil(() => resumed is not null || refused is not null, TimeSpan.FromSeconds(10));

        refused.Should().BeNull();
        resumed.Should().NotBeNull();
        resumed!.IsRunning.Should().BeTrue();
        _sqlite.Find(id)!.State.Should().Be(AgentSessionState.Running, "the live resume's own save must be the last word");
        _sqlite.Transcript(id).OfType<SessionEnded>().Should().ContainSingle(
            "the one ending the studio restart earned, and no spurious second one after the resume");
    }
}
