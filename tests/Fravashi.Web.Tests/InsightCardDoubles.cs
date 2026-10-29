using Fravashi.Application.Git;
using Fravashi.Application.Telemetry;
using Fravashi.Domain.Git;
using Fravashi.Domain.Repository;
using Fravashi.Domain.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace Fravashi.Web.Tests;

/// <summary>
/// The seams the home surface's insight cards read (backlog 36): a clock the test owns, a product-event
/// store and its opt-in switch, and a git service. Every one of them is a stand-in for something the
/// studio really stores, so the doubles here answer from data a test SEEDS rather than from a canned
/// figure: a card that passed against a hard-coded answer would prove nothing about the card.
/// </summary>
internal static class InsightCardDoubles
{
    public static void AddInsightCards(
        this IServiceCollection services,
        DateTimeOffset? now = null,
        IProductEventRecorder? telemetry = null,
        IProductEventStore? events = null,
        IGitService? git = null)
    {
        services.AddSingleton<TimeProvider>(new FixedClock(
            now ?? new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton(telemetry ?? IProductEventRecorder.Off);
        services.AddSingleton(events ?? new StubProductEventStore());
        services.AddSingleton<IGitService>(git ?? new FakeGitService { Available = false });
    }
}

/// <summary>
/// A clock stuck at one instant. The cards key everything off UTC days, so a test that could not pin
/// "now" would be asserting against whatever day the suite happened to run on.
/// </summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>An <see cref="IProductEventRecorder"/> whose only interesting property is the opt-in switch.</summary>
internal sealed class StubProductEventRecorder(bool enabled) : IProductEventRecorder
{
    public bool IsEnabled => enabled;

    public void Record(ProductEvent productEvent)
    {
    }
}

/// <summary>
/// An <see cref="IProductEventStore"/> over a seeded list, filtered exactly as the SQLite store filters:
/// by kind and by the stamp, newest first.
/// </summary>
internal sealed class StubProductEventStore(params ProductEvent[] seeded) : IProductEventStore
{
    private readonly List<ProductEvent> _events = [.. seeded];

    public void Append(ProductEvent productEvent) => _events.Add(productEvent);

    public IReadOnlyList<ProductEvent> Recent(int max = 2000) =>
        [.. _events.OrderByDescending(e => e.OccurredUtc).Take(max)];

    public IReadOnlyList<ProductEvent> Since(ProductEventKind kind, DateTimeOffset fromUtc, int max = 20000) =>
        [.. _events
            .Where(e => e.Kind == kind && e.OccurredUtc >= fromUtc)
            .OrderByDescending(e => e.OccurredUtc)
            .Take(max)];

    public long Count() => _events.Count;
}

/// <summary>
/// An <see cref="IGitService"/> that answers the two calls the repository-health card makes and throws
/// on everything else, so a card that quietly grew a third git call fails loudly rather than silently
/// reporting something nobody checked.
/// </summary>
internal sealed class FakeGitService : IGitService
{
    public bool Available { get; init; } = true;

    public string Branch { get; init; } = "main";

    public IReadOnlyList<GitCommit> Commits { get; init; } = [];

    /// <summary>When set, the log read fails the way an unreachable bridge fails.</summary>
    public Exception? LogFailure { get; init; }

    /// <summary>Every max the card asked the log for, so a test can hold it to the documented cap.</summary>
    public List<int> RequestedMax { get; } = [];

    public bool IsAvailable => Available;

    public Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default) =>
        LogFailure is null ? Task.FromResult(Branch) : Task.FromException<string>(LogFailure);

    public Task<IReadOnlyList<GitCommit>> GetCommitsAsync(
        string reference, int max = 50, string? path = null, CancellationToken cancellationToken = default)
    {
        RequestedMax.Add(max);
        return LogFailure is null
            ? Task.FromResult(Commits)
            : Task.FromException<IReadOnlyList<GitCommit>>(LogFailure);
    }

    private static NotSupportedException Unused([System.Runtime.CompilerServices.CallerMemberName] string member = "") =>
        new($"the repository health card does not call {member}");

    public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default) => throw Unused();

    public Task CheckoutAsync(string branch, CancellationToken cancellationToken = default) => throw Unused();

    public Task CreateBranchAsync(string name, string baseBranch, bool checkout, CancellationToken cancellationToken = default) => throw Unused();

    public Task<IReadOnlyList<RepositoryNode>> ListTreeAsync(string reference, string path, CancellationToken cancellationToken = default) => throw Unused();

    public Task<RepositoryFile?> ReadFileAsync(string reference, string path, CancellationToken cancellationToken = default) => throw Unused();

    public Task<GitCommitDetail?> GetCommitAsync(string sha, CancellationToken cancellationToken = default) => throw Unused();

    public Task<string> GetDiffAsync(string sha, string? path = null, CancellationToken cancellationToken = default) => throw Unused();

    public Task<IReadOnlyList<GitFileChange>> GetDiffFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default) => throw Unused();

    public Task<string?> GetMergeBaseAsync(string first, string second, CancellationToken cancellationToken = default) => throw Unused();

    public Task<IReadOnlyList<GitCommit>> GetFileHistoryAsync(string path, string reference, int max = 50, CancellationToken cancellationToken = default) => throw Unused();

    public Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default) => throw Unused();

    public Task<IReadOnlyList<GitRemoteBranch>> GetRemoteBranchesAsync(CancellationToken cancellationToken = default) => throw Unused();

    public Task CheckoutTrackingAsync(GitRemoteBranch branch, CancellationToken cancellationToken = default) => throw Unused();

    public Task<IReadOnlyList<GitGraphCommit>> GetGraphAsync(int max = 150, CancellationToken cancellationToken = default) => throw Unused();

    public Task<GitLineBlame?> GetLineBlameAsync(string path, int line, CancellationToken cancellationToken = default) => throw Unused();
}
