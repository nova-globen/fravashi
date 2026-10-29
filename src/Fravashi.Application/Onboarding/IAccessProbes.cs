namespace Fravashi.Application.Onboarding;

/// <summary>Verifies access to a documentation/wiki provider (read, and where honestly determinable, write/delete).
/// Probes run ONLY on explicit user action in the onboarding wizard: never automatically, never against a live
/// system from a test. (The source-repository probe retired with the wizard's source step, plan 2c.)</summary>
public interface IWikiAccessProbe
{
    Task<AccessProbeResult> ProbeAsync(WikiAccessRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Verifies access to a work board / tracker (read, query, and where honestly determinable, write/modify/status-change).</summary>
public interface IBoardAccessProbe
{
    Task<AccessProbeResult> ProbeAsync(BoardAccessRequest request, CancellationToken cancellationToken = default);
}
