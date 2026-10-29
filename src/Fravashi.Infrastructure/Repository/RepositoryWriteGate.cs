using Fravashi.Application.Repository;

namespace Fravashi.Infrastructure.Repository;

/// <summary>Singleton <see cref="IRepositoryWriteGate"/> over a one-slot semaphore (ADR 0016).</summary>
public sealed class RepositoryWriteGate : IRepositoryWriteGate, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
