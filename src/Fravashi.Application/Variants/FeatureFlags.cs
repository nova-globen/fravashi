namespace Fravashi.Application.Variants;

/// <summary>
/// The compile-time-and-image feature switches a release variant toggles (I1 / ADR 0006 ruling 3). One
/// version tag fans out to several named variant asset sets; each variant flips these switches both in the
/// <b>compiled</b> app (an MSBuild <c>FravashiVariant</c> property sets <c>DefineConstants</c>, so a switched-off
/// feature's default is baked in) and as <b>image</b> features (Dockerfile build-args become environment
/// variables the app reads). Product-neutral by construction - the switches name capabilities, never a customer.
/// </summary>
/// <param name="GitHubLogin">
/// When false, the GitHub single-sign-on button is hidden and GitHub connections are token-only (reusing the
/// ADR-0004 vendor-default disabled-state pattern). When true, "Sign in with GitHub" is offered as usual.
/// </param>
/// <param name="VpnRequired">
/// When true, the app will not serve until a secure VPN tunnel is confirmed (the startup gate; the tunnel itself
/// is the T9 sidecar). When false, there is no VPN gate.
/// </param>
/// <param name="BrokerRequired">
/// When true (the <c>corp-broker</c> variant, ADR 0010 / plan 04 B4), the remote credential broker pathway is
/// switched on and the local org-key injection path is <b>compiled off</b>: the agent CLIs go through the broker
/// with a per-user token and the organization key is never read or emitted on this device - even if the broker is
/// misconfigured, the credential layer fails closed rather than reverting to the local key. Mandatory SSO sign-in
/// applies (the broker needs a per-user token; there is no anonymous use). When false (<c>full</c>/<c>corp-vpn</c>),
/// the single-user default stands: the org key is injected locally exactly as before (ADR 0003, keyless-of-nobody).
/// </param>
/// <param name="AuthRequired">
/// When true (the DEFAULT for every variant, plan Phase 3 decision 9), the auth-first sign-in
/// gate enforces: nothing serves until the user signs in against the configured IdP, and an
/// unconfigured install offers the one-time IdP setup ON the gate page. When false (a publish
/// with <c>AUTH_REQUIRED=false</c>), the gate enforces only when IdP coordinates are configured.
/// Baked IdP coordinates (<c>FRAVASHI_IDP_ISSUER</c>/<c>FRAVASHI_IDP_CLIENT_ID</c> image env, the
/// BYOD releases) always win over and lock out the settings rows.
/// </param>
public sealed record FeatureFlags(bool GitHubLogin, bool VpnRequired, bool BrokerRequired = false, bool AuthRequired = true)
{
    /// <summary>The baseline: every feature on, no VPN requirement, broker off, sign-in required (the <c>full</c> variant).</summary>
    public static readonly FeatureFlags Full = new(GitHubLogin: true, VpnRequired: false, BrokerRequired: false, AuthRequired: true);
}

/// <summary>
/// The running app's variant identity + its resolved feature switches (I1). This is read at runtime to gate the
/// GitHub SSO button and the VPN startup gate. The value is honest about what is actually compiled/configured:
/// the compiled default (from the variant baked at publish time) unless an image environment variable overrides
/// it.
/// </summary>
public interface IFeatureFlags
{
    /// <summary>The variant name (e.g. <c>full</c>, <c>corp-vpn</c>) - the same string surfaced by IProductInfo.</summary>
    string Variant { get; }

    /// <summary>The resolved feature switches for this build.</summary>
    FeatureFlags Flags { get; }
}
