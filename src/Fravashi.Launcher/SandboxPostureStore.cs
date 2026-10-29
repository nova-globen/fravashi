using System.Text.Json;

namespace Fravashi.Launcher;

/// <summary>
/// Writes the applied container-hardening posture into the mounted data directory
/// (<c>&lt;dataDir&gt;/sandbox-posture.json</c>) - the SINGLE SOURCE OF TRUTH the app reads to DISPLAY the honest
/// sandbox posture (F4 / ADR 0007, assertion-gap rule). The launcher is the only actor that runs the container,
/// so it is the only actor that knows which hardening flags were actually passed to the engine; it derives the
/// posture from the very same <see cref="HardeningPlan"/> the run-args come from, so the displayed protections can
/// never diverge from the enforced ones. The app must never render a green "hardened" for a control the runtime
/// could not apply - hence per-control Active/Unavailable, written here from the measured capability matrix.
/// Field names are camelCase to match the app-side web-JSON reader and are intentionally stable across versions.
/// </summary>
public sealed class SandboxPostureStore
{
    /// <summary>The file name inside the data directory (matches the app-side reader).</summary>
    public const string FileName = "sandbox-posture.json";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;

    public SandboxPostureStore(string dataDir) => _path = Path.Combine(dataDir, FileName);

    /// <summary>Writes the posture derived from <paramref name="plan"/> (whole-file write).</summary>
    public void Write(HardeningPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var file = new SandboxPostureFile(
            plan.RuntimeName,
            plan.StrongerRuntimeRecommended,
            plan.Recommendation,
            DateTimeOffset.UtcNow,
            [.. plan.Controls.Select(c => new SandboxControlEntry(c.Id, c.Title, c.Description, c.Status.ToString(), c.Detail))]);

        Directory.CreateDirectory(_path[.._path.LastIndexOf(Path.DirectorySeparatorChar)]);
        File.WriteAllText(_path, JsonSerializer.Serialize(file, Json));
    }

    /// <summary>The on-disk shape shared with the app-side reader (<c>FileSandboxPosture</c>).</summary>
    public sealed record SandboxPostureFile(
        string Runtime,
        bool StrongerRuntimeRecommended,
        string? Recommendation,
        DateTimeOffset CheckedUtc,
        IReadOnlyList<SandboxControlEntry> Controls);

    /// <summary>One control's on-disk entry.</summary>
    public sealed record SandboxControlEntry(
        string Id,
        string Title,
        string Description,
        string Status,
        string? Detail);
}
