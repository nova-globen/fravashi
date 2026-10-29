using Fravashi.Domain;

namespace Fravashi.Application.Sandbox;

/// <summary>Whether a container-hardening protection is actually applied on the running runtime (F4 / ADR 0007).</summary>
public enum SandboxProtectionStatus
{
    /// <summary>Applied and enforced by the runtime.</summary>
    Active,

    /// <summary>Not applied - the runtime cannot enforce it (shown honestly, never as a green "hardened").</summary>
    Unavailable,
}

/// <summary>One hardening protection's status, for the honest posture surface.</summary>
/// <param name="Id">Stable control id (from the launcher-written posture file).</param>
/// <param name="Title">Short human title.</param>
/// <param name="Description">What the control protects against.</param>
/// <param name="Status">Whether it is active on the detected runtime.</param>
/// <param name="Detail">The applied value, or why it is unavailable + the remedy.</param>
public sealed record SandboxControl(
    string Id,
    string Title,
    string Description,
    SandboxProtectionStatus Status,
    string? Detail);

/// <summary>
/// The app's view of the container sandbox it is running inside (F4 / ADR 0007). The <c>fravashi</c> launcher - the
/// only actor that runs the container and therefore the only one that knows which hardening flags the engine
/// actually accepted - writes this into the mounted data directory (<c>sandbox-posture.json</c>) from the same
/// plan its run-args come from. The app <b>reads</b> and DISPLAYS it; it never claims a protection the runtime
/// could not apply. Absent file (a host-dev run outside any container) =&gt; <see cref="OutsideContainer"/>.
/// </summary>
/// <param name="Runtime">The detected runtime name (e.g. <c>wslc</c>, <c>docker</c>).</param>
/// <param name="Controls">Each protection's status, in display order.</param>
/// <param name="StrongerRuntimeRecommended">True when a stronger runtime would enforce more.</param>
/// <param name="Recommendation">An honest recommendation when the runtime is weaker, else null.</param>
/// <param name="CheckedUtc">When the launcher recorded the posture, or null if unknown.</param>
/// <param name="Known">False when no posture file exists (running outside a launcher-run container).</param>
public sealed record SandboxPosture(
    string Runtime,
    IReadOnlyList<SandboxControl> Controls,
    bool StrongerRuntimeRecommended,
    string? Recommendation,
    DateTimeOffset? CheckedUtc,
    bool Known = true)
{
    /// <summary>The state when the app runs outside a launcher-managed container (no posture file to read).</summary>
    public static readonly SandboxPosture OutsideContainer =
        new("host", [], StrongerRuntimeRecommended: false,
            Recommendation: $"{ProductName.Short} is running outside a launcher-managed container, so container hardening does " +
                $"not apply here. In a normal install the {ProductName.Short} launcher runs the studio inside a hardened container.",
            CheckedUtc: null, Known: false);

    /// <summary>Count of applied protections.</summary>
    public int ActiveCount => Controls.Count(c => c.Status == SandboxProtectionStatus.Active);

    /// <summary>Count of protections the runtime could not apply.</summary>
    public int UnavailableCount => Controls.Count(c => c.Status == SandboxProtectionStatus.Unavailable);
}

/// <summary>
/// Reads the launcher-written sandbox posture (see <see cref="SandboxPosture"/>). The dashboard / settings posture
/// view consumes this; the value is cached with a short TTL so a request-time read is cheap while still picking up
/// a fresh <c>fravashi</c> run within seconds.
/// </summary>
public interface ISandboxPosture
{
    /// <summary>The current sandbox posture from the launcher-written file (or <see cref="SandboxPosture.OutsideContainer"/>).</summary>
    SandboxPosture Current { get; }
}
