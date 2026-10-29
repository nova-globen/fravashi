using Fravashi.Application.Integrity;

namespace Fravashi.Infrastructure.Integrity;

/// <summary>
/// The process-wide holder of the latest integrity evaluation (F3). The <see cref="IntegrityCheckService"/> writes
/// it after each run; the lock middleware and the Settings surface read it. Before the first evaluation it reports
/// <see cref="IntegrityStatus.NotEvaluated"/>, so nothing locks during the brief window between app start and the
/// first check.
/// </summary>
public sealed class IntegrityStatusProvider : IIntegrityStatusProvider
{
    private volatile IntegrityStatus _current = IntegrityStatus.NotEvaluated;

    public IntegrityStatus Current => _current;

    /// <summary>Records a fresh evaluation.</summary>
    public void Set(IntegrityStatus status) => _current = status ?? IntegrityStatus.NotEvaluated;
}
