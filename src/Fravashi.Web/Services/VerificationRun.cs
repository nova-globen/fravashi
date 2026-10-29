using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Fravashi.Application.Persistence;

namespace Fravashi.Web.Services;

/// <summary>
/// Backlog 226 (the ruling of 2026-10-07, ADR 0030 decision 8, plan @69): whether THIS studio process is a
/// disposable verification run the app runner started, which Fravashi's own design check then drives past the sign-in
/// gate and onboarding.
/// <para>
/// The app runner gives such a run ONE named field, <see cref="EnvironmentVariable"/>, whose value is the window token
/// it minted for that run, and sets <c>FRAVASHI_WINDOW_TOKEN</c> to the same value in the same start. The flag is
/// honoured only when both are in this PROCESS's environment, have the minted shape, are equal, and the token the
/// window-only gate enforces is that same value. It is read from the process environment and never from configuration,
/// because configuration includes the settings table, which outlives a run and which a person could write on a real
/// install. Without the matching token the flag is refused, the log says so (never with a value), and nothing changes.
/// </para>
/// <para>
/// What it does when honoured, for this process only: the sign-in gate does not require a session
/// (<see cref="SignInRequired"/>) and onboarding reads as complete (<see cref="ApplyTo"/>). Nothing is written. The
/// window-only gate and its exemptions, and the integrity, critical-update and VPN gates, do not consult it.
/// </para>
/// </summary>
public sealed partial class VerificationRun
{
    /// <summary>The field's name. The app runner's <c>VerificationRunField.Name</c> is the same string.</summary>
    public const string EnvironmentVariable = "FRAVASHI_VERIFICATION_RUN";

    private VerificationRun(bool present, bool honoured, string? refusal)
    {
        Present = present;
        Honoured = honoured;
        Refusal = refusal;
    }

    /// <summary>True when the field was set at all.</summary>
    public bool Present { get; }

    /// <summary>True when this process is a verification run whose flag matched its window token.</summary>
    public bool Honoured { get; }

    /// <summary>Why a set flag was refused, or null. Never carries a value.</summary>
    public string? Refusal { get; }

    /// <summary>
    /// Decides from the field, this start's window token, and the token the window-only gate enforces.
    /// </summary>
    public static VerificationRun Decide(string? flag, string? startWindowToken, string? enforcedWindowToken)
    {
        if (string.IsNullOrEmpty(flag))
        {
            return new VerificationRun(false, false, null);
        }

        if (string.IsNullOrEmpty(startWindowToken))
        {
            return Refused($"this start carries no {WindowOnlyGate.TokenConfigKey} to match it against");
        }

        if (!MintedShape().IsMatch(flag))
        {
            return Refused("its value is not a token the app runner mints (64 lower-case hex digits)");
        }

        if (!SameToken(flag, startWindowToken))
        {
            return Refused($"it does not match this start's {WindowOnlyGate.TokenConfigKey}");
        }

        if (!SameToken(startWindowToken, enforcedWindowToken))
        {
            return Refused($"the window token the window-only gate enforces is not this start's {WindowOnlyGate.TokenConfigKey}");
        }

        return new VerificationRun(true, true, null);

        static VerificationRun Refused(string why) => new(true, false, why);
    }

    /// <summary>Decides for this process: the field and the window token from its environment, the enforced token from configuration.</summary>
    public static VerificationRun FromProcess(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Decide(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            Environment.GetEnvironmentVariable(WindowOnlyGate.TokenConfigKey),
            configuration[WindowOnlyGate.TokenConfigKey]);
    }

    /// <summary>Whether the sign-in gate requires a session, given what it would require without this run.</summary>
    public bool SignInRequired(bool required) => required && !Honoured;

    /// <summary>
    /// When honoured, and only then, replaces <see cref="IOnboardingState"/> with one that reads as complete over the
    /// registered one. Otherwise the collection is left exactly as it was.
    /// </summary>
    public void ApplyTo(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!Honoured)
        {
            return;
        }

        var registered = services.LastOrDefault(d => d.ServiceType == typeof(IOnboardingState))
            ?? throw new InvalidOperationException("No onboarding state is registered to read as complete.");
        services.Remove(registered);
        services.AddSingleton<IOnboardingState>(provider => new VerificationRunOnboardingState(Resolve(provider, registered)));

        static IOnboardingState Resolve(IServiceProvider provider, ServiceDescriptor descriptor) =>
            descriptor.ImplementationInstance as IOnboardingState
            ?? (descriptor.ImplementationFactory is { } factory
                ? (IOnboardingState)factory(provider)
                : (IOnboardingState)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));
    }

    /// <summary>Says what was decided, when the field was set at all. No value is ever logged.</summary>
    public void Log(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (Honoured)
        {
            logger.LogWarning(
                "{Variable} matches this start's window token: this studio process is a verification run (backlog 226). The sign-in gate is off and onboarding reads as complete for this process only; every other gate is unchanged.",
                EnvironmentVariable);
        }
        else if (Present)
        {
            logger.LogWarning(
                "{Variable} is set and refused: {Reason}. The sign-in gate and onboarding are unchanged.", EnvironmentVariable, Refusal);
        }
    }

    public override string ToString() =>
        Honoured ? "a verification run" : Present ? "a refused verification-run flag: " + Refusal : "not a verification run";

    private static bool SameToken(string? a, string? b) =>
        a is not null && b is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    [GeneratedRegex("^[0-9a-f]{64}\\z")]
    private static partial Regex MintedShape();
}

/// <summary>
/// Backlog 226: onboarding as a verification run reads it, complete, for the process's life. This class writes nothing;
/// a write the wizard makes passes through to the run's own store, which lives in the disposable container and dies with it.
/// </summary>
public sealed class VerificationRunOnboardingState(IOnboardingState inner) : IOnboardingState
{
    public bool IsComplete => true;

    public OnboardingStatus Get() => inner.Get() with { IsComplete = true };

    public void SetCurrentStep(string step) => inner.SetCurrentStep(step);

    public void MarkComplete() => inner.MarkComplete();

    public void Reset() => inner.Reset();
}
