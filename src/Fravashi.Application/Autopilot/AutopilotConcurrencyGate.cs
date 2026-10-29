using Fravashi.Domain.Autopilot;

namespace Fravashi.Application.Autopilot;

/// <summary>
/// Where the concurrency cap's Settings value comes from (ADR 0031 decision 16, #307), shaped after
/// <see cref="Sessions.IAgentSpendLimitSource"/>: read on every call, so saving a lower cap in Settings bites
/// the next start rather than the next restart.
/// </summary>
public interface IAutopilotConcurrencySettings
{
    /// <summary>The cap as it stands right now.</summary>
    int Cap();

    /// <summary>A source with no wiring at all, for a caller nobody has given one (the shipped default cap).</summary>
    public static IAutopilotConcurrencySettings Default { get; } = new DefaultSettings();

    private sealed class DefaultSettings : IAutopilotConcurrencySettings
    {
        public int Cap() => AutopilotConcurrency.DefaultCap;
    }
}

/// <summary>
/// How much memory is free right now, for the concurrency gate's own check (ADR 0031 decision 16, #307). Null
/// when it cannot be read at all, which the gate treats as "skip the memory check", never as a refusal.
/// </summary>
public interface IAutopilotMemoryAvailability
{
    long? AvailableBytes();

    /// <summary>
    /// Names which source actually answered the most recent <see cref="AvailableBytes"/> call, for a
    /// refusal's own words (#308 host note point 5). Defaulted to <c>"estimate"</c> so every existing
    /// implementation (and every fake) compiles unchanged; only a reader that can answer from more than one
    /// source (<c>CgroupMemoryAvailability</c>) needs to override it.
    /// </summary>
    string Source => "estimate";
}

/// <summary>
/// Whether the autopilot may start another session right now (ADR 0031 decision 16, #307): the cap, counted
/// per REPOSITORY rather than per workspace (a worktree and the folder it came from are one repository,
/// <see cref="AutopilotRepositoryIdentity"/>), and the memory floor. Consulted only before a FRESH autopilot
/// session starts, never for a person's own conversation and never for a follow-up turn to one already running.
/// </summary>
public interface IAutopilotConcurrencyGate
{
    /// <summary>Refusal words, or null when the workspace's repository has room to start another session.</summary>
    string? Refuse(string workspaceId, int? dispatchCapOverride);

    /// <summary>
    /// Holds this repository's own slot exclusively while the caller checks the cap (<see cref="Refuse"/>) and,
    /// when it allows one, actually starts the fresh session (#308 host note point 3): two concurrent calls
    /// for the SAME repository serialize here, so the second one's own <see cref="Refuse"/> call always sees
    /// the first one's session already counted by the live registry. Dispose the returned token once the
    /// attempt, successful or refused, is over. A gate with nothing to serialize (<see cref="None"/>) returns
    /// a token that locks nothing.
    /// </summary>
    Task<IDisposable> LockAsync(string workspaceId, CancellationToken ct = default);

    /// <summary>A gate that never refuses, for a wiring that does not check concurrency at all (tests).</summary>
    public static IAutopilotConcurrencyGate None { get; } = new AlwaysAllows();

    private sealed class AlwaysAllows : IAutopilotConcurrencyGate
    {
        public string? Refuse(string workspaceId, int? dispatchCapOverride) => null;

        public Task<IDisposable> LockAsync(string workspaceId, CancellationToken ct = default) =>
            Task.FromResult<IDisposable>(NoopLock.Instance);
    }

    /// <summary>A lock token that holds nothing, for a gate with no repository-keyed state to serialize.</summary>
    private sealed class NoopLock : IDisposable
    {
        public static readonly NoopLock Instance = new();

        public void Dispose()
        {
        }
    }
}
