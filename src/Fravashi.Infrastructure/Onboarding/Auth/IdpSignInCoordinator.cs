using System.Security.Cryptography;
using System.Text;
using Fravashi.Application.Onboarding.Auth;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>A sign-in some window started and that has not ended yet (#372): which page started it, and when.</summary>
/// <param name="Origin">The starting page's own id; a page compares it with its own to tell "mine" from "another window's".</param>
/// <param name="StartedAt">When the authorize URL was built.</param>
public sealed record IdpSignInAttempt(string Origin, DateTimeOffset StartedAt);

/// <summary>
/// The studio's ONE sign-in (#372). The session is the studio's, not a window's, so with several studio windows open a
/// sign-in started in one belongs to all of them. One per process (a singleton; <see cref="IdpAuthService"/> is a typed
/// HTTP client and so transient, which is why this state cannot live there). Two jobs:
/// <list type="number">
/// <item>Say which sign-in is in progress and when one completed, through <see cref="Changed"/>, so every sign-in page
/// says "Signing in in another window" while another window's attempt runs and follows by itself when it completes.</item>
/// <item>Handle each callback state ONCE (<see cref="HandleOnceAsync"/>). Seen on 2026-10-25: in a workspace window the
/// window shell re-imposed its <c>?ws=</c> on the identity provider's redirect to the callback, and the cancelled first
/// request still reached the studio. It consumed the one-shot state, its token exchange was cancelled with the aborted
/// request, and the re-imposed second request found no state and answered "possible CSRF". The window no longer does
/// that; this is the studio's own guard against a callback that arrives twice for any other reason.</item>
/// </list>
/// <para>
/// What a second arrival may have is deliberately narrow. Only the SAME state, with the SAME code, from the SAME caller
/// (the window's user agent, which carries the launcher's window token), inside <see cref="ReplayWindow"/> of the first,
/// gets the first one's OUTCOME: the same success or the same refusal, never a second token exchange. Anything else for a
/// state already handled (another code, another caller, too late) is refused exactly as an unknown state is. A state that
/// was never issued, or has expired, is refused before anything else happens. Every answer is looked up by its own
/// state, so one state's result can never answer another. Replaying a success hands out nothing: the session lives in the
/// studio, not in the response, and the response to a callback is only a redirect.
/// </para>
/// </summary>
public sealed class IdpSignInCoordinator(TimeProvider? clock = null)
{
    /// <summary>What a callback whose state is unknown, expired, already used or used differently is told.</summary>
    public const string UnknownStateWords = "The sign-in state was unknown or expired (possible CSRF); start again.";

    /// <summary>How long after the first arrival of a callback the same request may get its outcome again.</summary>
    public static readonly TimeSpan ReplayWindow = TimeSpan.FromSeconds(60);

    /// <summary>How long a started sign-in is shown to the other windows as in progress: as long as its state is valid.</summary>
    public static readonly TimeSpan AttemptLifetime = InMemoryAuthCodeStateStore.Lifetime;

    /// <summary>
    /// The longest a token exchange may take. It runs on its own budget and not on the request's cancellation, so a
    /// request the window aborted cannot throw away a state it already consumed.
    /// </summary>
    public static readonly TimeSpan ExchangeBudget = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Handled> _handled = new(StringComparer.Ordinal);
    private Started? _attempt;
    private long _completions;

    /// <summary>Raised when a sign-in starts, ends without signing in, or completes. Not on the renderer's thread: a page marshals.</summary>
    public event Action? Changed;

    /// <summary>The sign-in in progress, or null when none is (or the last one outlived its state).</summary>
    public IdpSignInAttempt? InProgress
    {
        get
        {
            lock (_gate)
            {
                return _attempt is { } started && _clock.GetUtcNow() - started.Attempt.StartedAt <= AttemptLifetime
                    ? started.Attempt
                    : null;
            }
        }
    }

    /// <summary>How many sign-ins have completed in this process.</summary>
    public long Completions
    {
        get
        {
            lock (_gate)
            {
                return _completions;
            }
        }
    }

    /// <summary>A page built an authorize URL for <paramref name="state"/>: it is now the studio's sign-in in progress.</summary>
    public void Begin(string state, string origin)
    {
        ArgumentException.ThrowIfNullOrEmpty(state);
        ArgumentNullException.ThrowIfNull(origin);
        lock (_gate)
        {
            _attempt = new Started(state, new IdpSignInAttempt(origin, _clock.GetUtcNow()));
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Handles the callback for <paramref name="state"/> once. The first arrival consumes the state (through
    /// <paramref name="consume"/>, which answers null for a state that is unknown, expired or not this flow's) and runs
    /// <paramref name="exchange"/> on its own budget; a matching second arrival inside <see cref="ReplayWindow"/> gets the
    /// same outcome; anything else is refused with <see cref="UnknownStateWords"/>.
    /// </summary>
    public Task<AuthCodeExchange> HandleOnceAsync(
        string state,
        string? code,
        string? caller,
        Func<string, AuthCodeStateEntry?> consume,
        Func<AuthCodeStateEntry, CancellationToken, Task<AuthCodeExchange>> exchange)
    {
        ArgumentNullException.ThrowIfNull(consume);
        ArgumentNullException.ThrowIfNull(exchange);
        if (string.IsNullOrEmpty(state))
        {
            return Task.FromResult(AuthCodeExchange.Fail(UnknownStateWords));
        }

        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code ?? string.Empty));
        var callerText = caller ?? string.Empty;
        AuthCodeStateEntry? entry;
        TaskCompletionSource<AuthCodeExchange> outcome;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Sweep(now);
            if (_handled.TryGetValue(state, out var seen))
            {
                return now - seen.At <= ReplayWindow
                    && CryptographicOperations.FixedTimeEquals(seen.CodeHash, codeHash)
                    && string.Equals(seen.Caller, callerText, StringComparison.Ordinal)
                        ? seen.Outcome
                        : Task.FromResult(AuthCodeExchange.Fail(UnknownStateWords));
            }

            // Consumed under the lock, together with recording that it was, so two arrivals at once cannot both miss the
            // record and one of them find the state gone.
            entry = consume(state);
            if (entry is null)
            {
                return Task.FromResult(AuthCodeExchange.Fail(UnknownStateWords));
            }

            outcome = new TaskCompletionSource<AuthCodeExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
            _handled[state] = new Handled(codeHash, callerText, now, outcome.Task);
        }

        _ = RunAsync(state, entry, exchange, outcome);
        return outcome.Task;
    }

    private async Task RunAsync(
        string state,
        AuthCodeStateEntry entry,
        Func<AuthCodeStateEntry, CancellationToken, Task<AuthCodeExchange>> exchange,
        TaskCompletionSource<AuthCodeExchange> outcome)
    {
        AuthCodeExchange result;
        try
        {
            using var budget = new CancellationTokenSource(ExchangeBudget, _clock);
            result = await exchange(entry, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = AuthCodeExchange.Fail("The identity provider did not complete the sign-in in time; start again.");
        }
        catch (Exception ex)
        {
            result = AuthCodeExchange.Fail($"The sign-in could not be completed ({ex.GetType().Name}); start again.");
        }

        Ended(state, result.Success);
        outcome.SetResult(result);
    }

    private void Ended(string state, bool signedIn)
    {
        bool changed;
        lock (_gate)
        {
            if (signedIn)
            {
                // The studio is signed in, whichever window's attempt this was: nothing is in progress any more.
                _completions++;
                _attempt = null;
                changed = true;
            }
            else if (_attempt is { } started && string.Equals(started.State, state, StringComparison.Ordinal))
            {
                _attempt = null;
                changed = true;
            }
            else
            {
                changed = false;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var pair in _handled)
        {
            if (now - pair.Value.At > ReplayWindow && pair.Value.Outcome.IsCompleted)
            {
                _handled.Remove(pair.Key);
            }
        }
    }

    private sealed record Started(string State, IdpSignInAttempt Attempt);

    private sealed record Handled(byte[] CodeHash, string Caller, DateTimeOffset At, Task<AuthCodeExchange> Outcome);
}
