using System.Collections.Concurrent;
using Fravashi.Application.Onboarding.Auth;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// Process-scoped, single-use CSRF ledger for authorization-code sign-ins. States expire after
/// <see cref="Lifetime"/>; <see cref="Consume"/> removes the entry so a replay fails. A stale-entry sweep runs
/// opportunistically on each write so abandoned sign-ins do not accumulate.
/// </summary>
public sealed class InMemoryAuthCodeStateStore : IAuthCodeStateStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, AuthCodeStateEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public InMemoryAuthCodeStateStore(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public void Put(AuthCodeStateEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Sweep();
        _entries[entry.State] = entry;
    }

    public AuthCodeStateEntry? Consume(string state)
    {
        if (string.IsNullOrEmpty(state) || !_entries.TryRemove(state, out var entry))
        {
            return null;
        }

        return _time.GetUtcNow() - entry.CreatedUtc <= Lifetime ? entry : null;
    }

    private void Sweep()
    {
        var cutoff = _time.GetUtcNow() - Lifetime;
        foreach (var pair in _entries)
        {
            if (pair.Value.CreatedUtc < cutoff)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }
}
