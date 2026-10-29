using System.Linq;
using System.Threading.Channels;
using AwesomeAssertions;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Autopilot;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Autopilot;

/// <summary>
/// ADR 0031 decision 16, #307: the real <see cref="AutopilotConcurrencyGate"/>, the only code that actually
/// counts running sessions against a repository. Over a real <see cref="LiveSessionRegistry"/> (the pure
/// <see cref="AutopilotConcurrencyGuard"/> and <see cref="AutopilotRepositoryIdentity"/> have their own tests
/// in Domain.Tests; this is the one that proves the real counting and workspace resolution).
/// </summary>
public sealed class AutopilotConcurrencyGateTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-concurrencygate-" + Guid.NewGuid().ToString("N"));

    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly NeverEndingRunner _runner = new();
    private readonly LiveSessionRegistry _registry;

    public AutopilotConcurrencyGateTests()
    {
        Directory.CreateDirectory(_directory);
        var factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(factory);
        _registry = new LiveSessionRegistry(_runner, new SqliteSessionStore(factory), _broker);
    }

    public void Dispose()
    {
        _registry.Dispose();
        _broker.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void StartRunning(string workspaceId) =>
        _registry.Start(new SessionStartRequest(workspaceId, "/workspaces/" + workspaceId, "build the item"));

    private AutopilotConcurrencyGate Gate(
        FakeWorkspaces workspaces, int cap = 2, IAutopilotMemoryAvailability? memory = null) =>
        new(_registry, workspaces, new FakeConcurrencySettings(cap), memory ?? new FakeMemoryAvailability(null));

    [Fact]
    public void A_running_session_in_a_worktree_counts_toward_a_start_in_its_own_repository()
    {
        var workspaces = new FakeWorkspaces();
        workspaces.Add("w-repo", @"C:\repos\myrepo");
        workspaces.Add("w-worktree", @"C:\repos\myrepo.fravashi-worktrees\307");
        StartRunning("w-worktree");

        var refusal = Gate(workspaces, cap: 1).Refuse("w-repo", null);

        refusal.Should().NotBeNull();
        refusal.Should().Contain("1 of 1 session");
    }

    [Fact]
    public void A_running_session_in_an_unrelated_repository_does_not_count()
    {
        var workspaces = new FakeWorkspaces();
        workspaces.Add("w-repo", @"C:\repos\myrepo");
        workspaces.Add("w-other", @"C:\repos\otherrepo");
        StartRunning("w-other");

        Gate(workspaces, cap: 1).Refuse("w-repo", null).Should().BeNull();
    }

    [Fact]
    public void An_unregistered_workspace_groups_only_with_itself_not_with_another_unregistered_one()
    {
        var workspaces = new FakeWorkspaces(); // neither "w-x" nor "w-y" has a record
        StartRunning("w-x");

        Gate(workspaces, cap: 1).Refuse("w-y", null).Should().BeNull("different unregistered ids never collide");
        Gate(workspaces, cap: 1).Refuse("w-x", null).Should().NotBeNull("the same unregistered id groups with itself");
    }

    [Fact]
    public void A_dispatch_override_refuses_a_sibling_the_settings_cap_alone_would_allow()
    {
        var workspaces = new FakeWorkspaces();
        workspaces.Add("w-repo", @"C:\repos\myrepo");
        workspaces.Add("w-worktree", @"C:\repos\myrepo.fravashi-worktrees\307");
        StartRunning("w-worktree");

        var gate = Gate(workspaces, cap: 2); // the Settings cap alone would allow a second session
        gate.Refuse("w-repo", dispatchCapOverride: null).Should().BeNull("the Settings cap alone has room");
        gate.Refuse("w-repo", dispatchCapOverride: 1).Should().NotBeNull("the dispatch lowered the cap to 1");
    }

    [Fact]
    public void A_memory_source_that_throws_still_lets_the_cap_check_run_and_does_not_refuse()
    {
        var workspaces = new FakeWorkspaces();
        workspaces.Add("w-repo", @"C:\repos\myrepo");

        var gate = Gate(workspaces, cap: 2, memory: new ThrowingMemoryAvailability());

        gate.Refuse("w-repo", null).Should().BeNull("zero running sessions is below any cap, whatever memory says");
    }

    /// <summary>
    /// Proves the memory refusal through the REAL gate, not only the pure guard: a gate that silently dropped
    /// the reading (never passed it to AutopilotConcurrencyGuard) would pass every other test here but still
    /// never refuse for memory, which is exactly what this closes.
    /// </summary>
    [Fact]
    public void A_memory_reading_below_the_floor_refuses_through_the_real_gate()
    {
        var workspaces = new FakeWorkspaces();
        workspaces.Add("w-repo", @"C:\repos\myrepo");

        var gate = Gate(workspaces, cap: 2, memory: new FakeMemoryAvailability(512L * 1024 * 1024));

        var refusal = gate.Refuse("w-repo", null);

        refusal.Should().NotBeNull();
        refusal.Should().Contain("512 MB free");
    }

    [Fact]
    public void A_memory_reading_above_the_floor_is_not_refused_through_the_real_gate()
    {
        var workspaces = new FakeWorkspaces();
        workspaces.Add("w-repo", @"C:\repos\myrepo");

        var gate = Gate(workspaces, cap: 2, memory: new FakeMemoryAvailability(4L * 1024 * 1024 * 1024));

        gate.Refuse("w-repo", null).Should().BeNull();
    }

    /// <summary>
    /// #308 host note point 5, through the REAL gate: the container's own cgroup figures, when they parse,
    /// decide the refusal AND name "cgroup" in its own words, not only the pure guard's own Source parameter.
    /// </summary>
    [Fact]
    public void A_refusal_through_the_real_gate_names_the_cgroup_source_when_cgroup_figures_answered()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-cgroup-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var maxPath = Path.Combine(directory, "memory.max");
            var currentPath = Path.Combine(directory, "memory.current");
            File.WriteAllText(maxPath, "1000");
            File.WriteAllText(currentPath, "999"); // 1 byte free: below any realistic floor

            var workspaces = new FakeWorkspaces();
            workspaces.Add("w-repo", @"C:\repos\myrepo");
            var gate = Gate(workspaces, cap: 2, memory: new CgroupMemoryAvailability(maxPath: maxPath, currentPath: currentPath));

            var refusal = gate.Refuse("w-repo", null);

            refusal.Should().NotBeNull();
            refusal.Should().Contain("cgroup");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- #308 host note point 3: the cap's check and the session's start are one atomic step --------------------

    [Fact]
    public async Task LockAsync_serializes_two_concurrent_calls_for_the_same_repository()
    {
        var workspaces = new FakeWorkspaces();
        workspaces.Add("w-repo", @"C:\repos\myrepo");
        var gate = Gate(workspaces, cap: 2);

        var first = await gate.LockAsync("w-repo");
        var secondTask = gate.LockAsync("w-repo");

        await Task.Delay(50);
        secondTask.IsCompleted.Should().BeFalse("the second caller must wait for the first to release");

        first.Dispose();
        var second = await secondTask;
        second.Dispose();
    }

    /// <summary>
    /// Without the lock, two fresh starts could both read the cap as "not yet reached" before either one's
    /// session was counted by the registry: the acceptance's own "two starts racing for the last free slot".
    /// </summary>
    [Fact]
    public async Task The_cap_holds_when_two_starts_race_for_the_last_free_slot()
    {
        var workspaces = new FakeWorkspaces();
        workspaces.Add("w-repo", @"C:\repos\myrepo");
        var gate = Gate(workspaces, cap: 1);

        async Task<bool> AttemptAsync()
        {
            using var repositoryLock = await gate.LockAsync("w-repo");
            if (gate.Refuse("w-repo", null) is not null)
            {
                return false;
            }

            StartRunning("w-repo");
            return true;
        }

        var results = await Task.WhenAll(AttemptAsync(), AttemptAsync());

        results.Count(ok => ok).Should().Be(1, "only one of two concurrent starts may take the repository's last free slot");
    }

    private sealed class FakeWorkspaces : IWorkspaceStore
    {
        private readonly Dictionary<string, WorkspaceRecord> _records = new(StringComparer.Ordinal);

        public void Add(string id, string rootPath) =>
            _records[id] = new WorkspaceRecord(id, id, rootPath, DateTimeOffset.UtcNow);

        public IReadOnlyList<WorkspaceRecord> Recent(int max = 10) => [.. _records.Values];

        public WorkspaceRecord? Get(string id) => _records.TryGetValue(id, out var record) ? record : null;

        public void Touch(string id, string name, string rootPath) { }

        public void Remove(string id) { }
    }

    private sealed class FakeConcurrencySettings(int cap) : IAutopilotConcurrencySettings
    {
        public int Cap() => cap;
    }

    private sealed class FakeMemoryAvailability(long? bytes) : IAutopilotMemoryAvailability
    {
        public long? AvailableBytes() => bytes;
    }

    private sealed class ThrowingMemoryAvailability : IAutopilotMemoryAvailability
    {
        public long? AvailableBytes() => throw new InvalidOperationException("the cgroup controller is unreadable");
    }

    /// <summary>A runner whose session never ends, so <see cref="LiveAgentSession.IsRunning"/> stays true for the test's duration.</summary>
    private sealed class NeverEndingRunner : IAgentSessionRunner
    {
        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default) =>
            Channel.CreateUnbounded<AgentEvent>().Reader.ReadAllAsync(cancellationToken);

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public bool Stop(AgentSessionId sessionId) => true;

        public bool ChangeModel(AgentSessionId sessionId, string? model) => false;
    }
}
