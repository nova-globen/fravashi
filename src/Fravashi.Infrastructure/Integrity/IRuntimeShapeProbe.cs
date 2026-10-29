namespace Fravashi.Infrastructure.Integrity;

/// <summary>What the app can actually observe about one mount point from inside the container.</summary>
/// <param name="Exists">The path exists.</param>
/// <param name="Writable">A file could be created + deleted under it (the app genuinely needs it writable).</param>
/// <param name="FsType">The filesystem type from <c>/proc/self/mountinfo</c> (e.g. <c>tmpfs</c>), or null if unknown.</param>
public sealed record MountObservation(bool Exists, bool Writable, string? FsType);

/// <summary>
/// The app's window onto its OWN running shape (F3 / ADR 0007) - the environment variables it can read, the
/// effective uid it runs as, and what its mounts actually are. Abstracted behind an interface so the
/// <see cref="IntegrityEvaluator"/> is unit-tested against a fake shape (both a legitimate shape that stays quiet
/// and a tampered one that fails) without a real container.
/// </summary>
public interface IRuntimeShapeProbe
{
    /// <summary>The value of an environment variable the app observes, or null if unset.</summary>
    string? GetEnv(string name);

    /// <summary>The effective uid the process runs as (from <c>/proc/self/status</c>), or null if it cannot be read.</summary>
    int? TryGetEffectiveUid();

    /// <summary>Observes a mount point: existence, writability, and (best-effort) its filesystem type.</summary>
    MountObservation ObserveMount(string path);
}
