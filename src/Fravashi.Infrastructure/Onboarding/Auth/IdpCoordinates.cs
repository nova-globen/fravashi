using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// Where the IdP's issuer + client id come from (plan Phase 3 follow-up, directive
/// 2026-08-03): image-baked env ALWAYS wins over the settings rows and locks them. A BYOD
/// release bakes <c>FRAVASHI_IDP_ISSUER</c>/<c>FRAVASHI_IDP_CLIENT_ID</c> into the image
/// (variants.json build-args), so users cannot repoint the gate at their own IdP: the settings
/// UI renders read-only and DB edits are simply never read. The baked env itself is protected
/// transitively by the image digest pin (changing the image changes the digest and the launcher
/// refuses to start it).
/// </summary>
public static class IdpCoordinates
{
    public const string IssuerEnv = "FRAVASHI_IDP_ISSUER";
    public const string ClientIdEnv = "FRAVASHI_IDP_CLIENT_ID";

    /// <summary>True when the build bakes the coordinates: the settings rows are ignored and locked.</summary>
    public static bool Locked(Func<string, string?> env) =>
        !string.IsNullOrWhiteSpace(env(IssuerEnv)) && !string.IsNullOrWhiteSpace(env(ClientIdEnv));

    public static string? Issuer(IConfigurationStore settings, Func<string, string?> env) =>
        Locked(env)
            ? env(IssuerEnv)
            : NullIfEmpty(settings.Get(AuthSettings.IdpIssuer)) ?? NullIfEmpty(VendorAppDefaults.IdpIssuer);

    public static string? ClientId(IConfigurationStore settings, Func<string, string?> env) =>
        Locked(env)
            ? env(ClientIdEnv)
            : NullIfEmpty(settings.Get(AuthSettings.IdpClientId)) ?? NullIfEmpty(VendorAppDefaults.IdpClientId);

    public static bool Configured(IConfigurationStore settings, Func<string, string?> env) =>
        !string.IsNullOrWhiteSpace(Issuer(settings, env)) && !string.IsNullOrWhiteSpace(ClientId(settings, env));

    /// <summary>
    /// A DELIBERATE per-install opt-in: baked env or settings rows. The shipped vendor default
    /// alone is not "explicit", so a publish with AUTH_REQUIRED=false genuinely turns the gate
    /// off even though every build carries default coordinates.
    /// </summary>
    public static bool ExplicitlyConfigured(IConfigurationStore settings, Func<string, string?> env) =>
        Locked(env)
        || (!string.IsNullOrWhiteSpace(settings.Get(AuthSettings.IdpIssuer))
            && !string.IsNullOrWhiteSpace(settings.Get(AuthSettings.IdpClientId)));

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
