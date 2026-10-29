namespace Fravashi.Application.Autopilot;

/// <summary>Whether branch + pull request delivery can actually reach the workspace's origin.</summary>
/// <param name="HasToken">True when a stored credential resolves for the origin's host.</param>
/// <param name="Host">The host the origin resolved to, or null when the origin itself could not be parsed.</param>
public sealed record AutopilotDeliveryReadiness(bool HasToken, string? Host);

/// <summary>
/// Answers whether this workspace's origin has a Git host token (ADR 0031 decision 3's consequence: "a
/// project without one cannot run with branch + pull request delivery and says so at dispatch"). An
/// Application-layer port because the thing that actually knows (<c>GitHostAccess</c>) is
/// Infrastructure-layer and the dispatcher must not depend on it directly.
/// </summary>
public interface IAutopilotDeliveryReadiness
{
    Task<AutopilotDeliveryReadiness> CheckAsync(CancellationToken ct = default);
}
