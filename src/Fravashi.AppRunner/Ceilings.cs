namespace Fravashi.AppRunner;

/// <summary>
/// What every run is held to (ADR 0027, "The ceilings"). A spec may LOWER any of them and raise none; the defaults
/// are the ADR's starting guesses, written down as guesses, to be moved deliberately once real projects have been
/// measured. Nothing here comes from a spec without passing <see cref="Lower"/>.
/// </summary>
/// <param name="Cpus">CPU cores.</param>
/// <param name="MemoryMb">Memory in MiB, with swap off, so a leak fails fast instead of dragging the host.</param>
/// <param name="Processes">The process limit: a fork bomb is the cheapest denial of service a spec can express.</param>
/// <param name="WritableMb">
/// The writable layer, in MiB. The engine on the dev host silently IGNORES <c>--storage-opt size</c> (measured
/// 2026-09-23: 100 MB written under a 64 MB cap on podman 6.1 overlay), so this one is enforced by the app runner's
/// own watchdog, which reads each run's size and stops a run that passes it, rather than by a flag that would look
/// like protection and be none.
/// </param>
/// <param name="RunMinutes">Wall clock per run; also what makes an orphan findable.</param>
/// <param name="BuildMinutes">Wall clock per build.</param>
/// <param name="LogBytes">Output kept per run before it is truncated with a line saying so.</param>
public sealed record Ceilings(
    double Cpus,
    int MemoryMb,
    int Processes,
    int WritableMb,
    int RunMinutes,
    int BuildMinutes,
    long LogBytes)
{
    /// <summary>The ADR's defaults.</summary>
    public static readonly Ceilings Default = new(
        Cpus: 2, MemoryMb: 2048, Processes: 512, WritableMb: 2048, RunMinutes: 10, BuildMinutes: 15, LogBytes: 8L * 1024 * 1024);

    /// <summary>
    /// These ceilings lowered by what a spec or a caller asked for. A request above the current value, or one that
    /// is not a positive number, is REFUSED rather than clamped, because clamping silently is how somebody concludes
    /// that their setting applied.
    /// </summary>
    public (Ceilings? Lowered, string? Refusal) Lower(
        double? cpus = null, int? memoryMb = null, int? processes = null, int? writableMb = null, int? runMinutes = null)
    {
        var refusal = Check("cpus", cpus, Cpus) ?? Check("memoryMb", memoryMb, MemoryMb) ?? Check("processes", processes, Processes)
            ?? Check("writableMb", writableMb, WritableMb) ?? Check("runMinutes", runMinutes, RunMinutes);
        if (refusal is not null)
        {
            return (null, refusal);
        }

        return (this with
        {
            Cpus = cpus ?? Cpus,
            MemoryMb = memoryMb ?? MemoryMb,
            Processes = processes ?? Processes,
            WritableMb = writableMb ?? WritableMb,
            RunMinutes = runMinutes ?? RunMinutes,
        }, null);
    }

    private static string? Check(string name, double? asked, double ceiling) =>
        asked is not { } value ? null
        : !(value > 0) || double.IsNaN(value) ? $"{name} must be a positive number"
        : value > ceiling ? $"{name} may only lower the ceiling of {ceiling}, not raise it to {value}"
        : null;
}
