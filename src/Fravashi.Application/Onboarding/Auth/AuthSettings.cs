namespace Fravashi.Application.Onboarding.Auth;

/// <summary>
/// The settings-table keys and secret references for per-installation sign-in configuration. Client ids and
/// endpoint overrides are non-secret settings; the Atlassian client secret and the acquired OAuth tokens are
/// secrets (stored via <see cref="Persistence.ISecretStore"/>). The endpoint-override keys default to the real
/// IdP URLs and exist so a test (or a self-hosted IdP) can point the flow at a different authority - this is
/// what makes the SSO feature end-to-end testable without a live GitHub/Entra/Atlassian registration.
/// </summary>
public static class AuthSettings
{
    // ── Client ids (non-secret settings) ──────────────────────────────────────────────────────────────────
    public const string GitHubClientId = "Auth:GitHub:ClientId";
    public const string AzureDevOpsClientId = "Auth:AzureDevOps:ClientId";
    public const string AtlassianClientId = "Auth:Atlassian:ClientId";

    // ── Client secret (secret store reference; auth-code only) ─────────────────────────────────────────────
    public const string AtlassianClientSecretRef = "auth:atlassian:client-secret";

    // ── The owner's IdP (plan Phase 3): the auth-first sign-in gate's provider ────────────────────────────
    /// <summary>The OIDC issuer, e.g. https://nova-globen.se; discovery is {issuer}/.well-known/openid-configuration.</summary>
    public const string IdpIssuer = "Auth:Idp:Issuer";
    public const string IdpClientId = "Auth:Idp:ClientId";
    /// <summary>Optional: only when the IdP registered the studio as a confidential client.</summary>
    public const string IdpClientSecretRef = "auth:idp:client-secret";
    /// <summary>The signed-in session envelope (access + refresh + expiry), CredentialEnvelope-serialized.</summary>
    public const string IdpSessionSecretRef = "auth:idp:session";
    public const string IdpScope = "Auth:Idp:Scope";
    public const string IdpScopeDefault = "openid profile email offline_access";
    public const string IdpSignedInSubject = "Auth:Idp:SignedInSubject";
    public const string IdpSignedInName = "Auth:Idp:SignedInName";
    public const string IdpSignedInEmail = "Auth:Idp:SignedInEmail";

    // ── Endpoint / scope overrides (non-secret; default to the real IdP values) ────────────────────────────
    public const string GitHubDeviceCodeUrl = "Auth:GitHub:DeviceCodeUrl";
    public const string GitHubTokenUrl = "Auth:GitHub:TokenUrl";
    public const string GitHubScope = "Auth:GitHub:Scope";

    public const string AzureDevOpsAuthority = "Auth:AzureDevOps:Authority";
    public const string AzureDevOpsTenant = "Auth:AzureDevOps:Tenant";
    public const string AzureDevOpsDeviceCodeUrl = "Auth:AzureDevOps:DeviceCodeUrl";
    public const string AzureDevOpsTokenUrl = "Auth:AzureDevOps:TokenUrl";
    public const string AzureDevOpsScope = "Auth:AzureDevOps:Scope";

    public const string AtlassianAuthorizeUrl = "Auth:Atlassian:AuthorizeUrl";
    public const string AtlassianTokenUrl = "Auth:Atlassian:TokenUrl";
    public const string AtlassianResourcesUrl = "Auth:Atlassian:ResourcesUrl";
    public const string AtlassianScope = "Auth:Atlassian:Scope";
    public const string AtlassianAudience = "Auth:Atlassian:Audience";

    // ── Defaults (verified against the providers' current docs, July 2026) ─────────────────────────────────
    public const string GitHubDeviceCodeUrlDefault = "https://github.com/login/device/code";
    public const string GitHubTokenUrlDefault = "https://github.com/login/oauth/access_token";
    public const string GitHubScopeDefault = "repo";

    public const string AzureDevOpsAuthorityDefault = "https://login.microsoftonline.com";
    public const string AzureDevOpsTenantDefault = "organizations";

    // The Azure DevOps resource application id + the v2.0 /.default form, plus offline_access for a refresh token.
    public const string AzureDevOpsScopeDefault = "499b84ac-1321-427f-aa17-267ca6975798/.default offline_access";

    public const string AtlassianAuthorizeUrlDefault = "https://auth.atlassian.com/authorize";
    public const string AtlassianTokenUrlDefault = "https://auth.atlassian.com/oauth/token";
    public const string AtlassianResourcesUrlDefault = "https://api.atlassian.com/oauth/token/accessible-resources";
    public const string AtlassianAudienceDefault = "api.atlassian.com";

    public const string AtlassianScopeDefault =
        "read:jira-work write:jira-work read:jira-user read:confluence-content.all write:confluence-content offline_access";

    /// <summary>The <c>Onboarding:{area}:AuthMethod</c> key recording which method a connection used.</summary>
    public static string AuthMethodKey(string area) => $"Onboarding:{area}:AuthMethod";

    /// <summary>The <c>AzureDevOps:AuthScheme</c> key the bridge sets so the ADO adapters pick Basic vs Bearer.</summary>
    public const string AzureDevOpsAuthScheme = "AzureDevOps:AuthScheme";

    /// <summary>
    /// The <c>Atlassian:AuthScheme</c> key the secret bridge sets so the Jira/Confluence adapters pick Basic
    /// (PAT → site URL) vs Bearer (3LO → the api.atlassian.com gateway). Set from the credential's method
    /// alongside the token, exactly as <see cref="AzureDevOpsAuthScheme"/> is, so scheme and token always agree.
    /// </summary>
    public const string AtlassianAuthScheme = "Atlassian:AuthScheme";

    /// <summary>
    /// The <c>Atlassian:CloudId</c> key the coordinate bridge projects (from <c>Onboarding:{area}:CloudId</c>)
    /// so the Atlassian adapters can address the 3LO gateway <c>api.atlassian.com/ex/{jira|confluence}/{cloudId}</c>.
    /// Non-secret - it travels through the coordinate bridge, not the secret bridge.
    /// </summary>
    public const string AtlassianCloudId = "Atlassian:CloudId";
}
