namespace Fravashi.Application.Persistence;

/// <summary>A snapshot of onboarding progress, read from the singleton onboarding-state row.</summary>
/// <param name="IsComplete">True once onboarding has been marked complete.</param>
/// <param name="CurrentStep">The step onboarding last recorded, or null before it starts.</param>
/// <param name="StartedUtc">When onboarding first advanced past its initial step, or null.</param>
/// <param name="CompletedUtc">When onboarding was marked complete, or null.</param>
public sealed record OnboardingStatus(
    bool IsComplete,
    string? CurrentStep,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc);

/// <summary>
/// First-run detection and onboarding progress, persisted in SQLite. The app answers "is onboarding
/// complete?" from here with no startup ceremony (ADR 0003 V4): a fresh database reports incomplete, so
/// an unconfigured app goes straight to onboarding. The onboarding wizard (S5) flips it via
/// <see cref="MarkComplete"/> when the flow finishes.
/// </summary>
public interface IOnboardingState
{
    /// <summary>True when onboarding has been completed; false on a fresh database (first run).</summary>
    bool IsComplete { get; }

    OnboardingStatus Get();

    /// <summary>Records the step onboarding is on (and stamps the start time on first advance).</summary>
    void SetCurrentStep(string step);

    /// <summary>Marks onboarding complete and stamps the completion time.</summary>
    void MarkComplete();

    /// <summary>Clears completion (for re-running onboarding); leaves collected data intact.</summary>
    void Reset();
}
