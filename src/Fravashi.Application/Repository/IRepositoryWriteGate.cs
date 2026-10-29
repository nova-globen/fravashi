namespace Fravashi.Application.Repository;

/// <summary>
/// The process-wide repository write gate (ADR 0016). Every mutation of the working tree, editor
/// saves, replace-in-files, and mutating git commands, acquires it first, so a save can never
/// interleave with a checkout or merge touching the same tree. Reads do not take the gate. Dispose
/// the returned handle to release.
/// </summary>
public interface IRepositoryWriteGate
{
    Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}
