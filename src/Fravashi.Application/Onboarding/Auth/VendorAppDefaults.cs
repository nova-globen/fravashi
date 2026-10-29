namespace Fravashi.Application.Onboarding.Auth;

/// <summary>
/// The OAuth <b>client ids that ship baked into the product</b> - Nova Globen (the vendor) registers one app
/// per identity provider for the whole product, and the resulting client ids are <b>not secrets</b> (a
/// public client id is designed to be embedded and visible), so they live here in source, not in an install's
/// settings. An end user never registers an app; they only click "Sign in" and grant the Fravashi app access.
/// See <see cref="AuthSettings"/> for the per-install override keys and ADR 0004 for the decision.
///
/// <para>
/// ── OWNER ACTION: paste the registered client ids below ──────────────────────────────────────────────────
/// This file (this class, these two constants) is <b>the one place</b> to paste the client ids once the apps
/// are registered. Leave a value empty and that provider's "Sign in" button honestly reports it is not
/// available in this build (never "register an app" - that is not the user's job); the access-token path
/// still works. Registration steps are in <c>docs/sso-setup.md</c>.
/// </para>
/// </summary>
public static class VendorAppDefaults
{
    /// <summary>
    /// The GitHub OAuth App client id (device flow enabled), registered by Nova Globen (the app is still listed under the product's former name until the owner renames it, org
    /// nova-globen). Public by design - a client id is not a secret. Device-code issuance was live-verified
    /// against real GitHub on 2026-07-16. See <c>docs/sso-setup.md §GitHub</c>.
    /// </summary>
    public const string GitHubClientId = "Ov23liP39sWUgunmLTRh";

    /// <summary>
    /// The Microsoft Entra application (client) id - a multi-tenant public client (device-code enabled) with the
    /// Azure DevOps delegated scope, registered by Nova Globen (an app still listed under a former name until the owner renames it). Public by design - a
    /// client id is not a secret. Device-code issuance was live-verified against real Entra (the
    /// <c>organizations</c> authority) on 2026-07-16. See <c>docs/sso-setup.md §Entra</c>.
    /// </summary>
    public const string AzureDevOpsClientId = "26cbdb33-ce44-4b17-a1a1-0aff1f05e94a";

    // Atlassian is deliberately absent. 3LO requires a client SECRET, and a secret shipped in a self-hosted
    // product is extractable, so no vendor Atlassian app is shipped: Atlassian sign-in is disabled by default
    // and token auth remains its primary path. An installation that registers its own Atlassian app (and
    // accepts holding its own secret) can enable 3LO through the advanced override. See ADR 0004.

    /// <summary>
    /// The owner IdP for the auth-first sign-in gate (plan Phase 3; directive 2026-08-03:
    /// shipped at compile time so no user ever types it). A PUBLIC client: PKCE protects the
    /// flow and no secret exists anywhere. Precedence at runtime: BYOD image-baked env wins and
    /// locks; a settings-row override (advanced) comes next; this shipped default fills the gap.
    /// <para>
    /// The build's service host (<see cref="Fravashi.Domain.ServiceHost"/>): production unless the build named a test host,
    /// so no test host is written in the repository.
    /// </para>
    /// </summary>
    public static string IdpIssuer => Fravashi.Domain.ServiceHost.Issuer;

    /// <summary>
    /// The registered public client id at the owner IdP, the same on every host (registered on both issuers
    /// with the redirect URIs <c>http://localhost:9090..9093/auth/callback/idp</c>).
    /// </summary>
    public const string IdpClientId = "fravashi-studio";

    /// <summary>
    /// The baked-in defaults as configuration key/value pairs, for the configuration source that sits below
    /// the SQLite settings source. Only non-empty defaults are projected, so an unregistered provider leaves
    /// no key behind (its "not available in this build" state stays honest and a later DB value is not masked).
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string?>> AsConfiguration()
    {
        if (!string.IsNullOrWhiteSpace(GitHubClientId))
        {
            yield return new(AuthSettings.GitHubClientId, GitHubClientId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(AzureDevOpsClientId))
        {
            yield return new(AuthSettings.AzureDevOpsClientId, AzureDevOpsClientId.Trim());
        }
    }

    /// <summary>
    /// The shipped default for a client-id settings key, or null when none ships for it. Callers that read a
    /// client id from the settings table use this as the fallback when the install has set nothing - so a DB
    /// (advanced) override always wins and the vendor default fills the gap.
    /// </summary>
    public static string? ClientIdDefaultFor(string settingsKey) => settingsKey switch
    {
        AuthSettings.GitHubClientId => NullIfEmpty(GitHubClientId),
        AuthSettings.AzureDevOpsClientId => NullIfEmpty(AzureDevOpsClientId),
        _ => null,
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
