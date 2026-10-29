namespace Fravashi.Application.Onboarding.Auth;

/// <summary>
/// How a provider connection's credential is obtained. The <b>PAT path is unchanged</b> and remains the
/// always-available fallback; the two SSO strategies are the preferred path per provider family (owner
/// feedback F6). Product-neutral: these name the OAuth grant shape, not a vendor.
/// </summary>
public enum AuthStrategy
{
    /// <summary>A personal access token / API token typed by the user (today's working path).</summary>
    Pat,

    /// <summary>OAuth 2.0 device authorization grant (GitHub, Microsoft Entra for Azure DevOps).</summary>
    DeviceCode,

    /// <summary>OAuth 2.0 authorization-code grant / Atlassian 3LO (redirect + callback + refresh token).</summary>
    AuthCode,
}

/// <summary>
/// Stable string tags for <see cref="AuthStrategy"/>, stored in the credential envelope and the
/// <c>Onboarding:{area}:AuthMethod</c> setting so the bridge and the UI can tell a PAT from an OAuth token
/// without re-deriving it.
/// </summary>
public static class AuthMethods
{
    public const string Pat = "pat";
    public const string Device = "device";
    public const string AuthCode = "authcode";

    public static string For(AuthStrategy strategy) => strategy switch
    {
        AuthStrategy.DeviceCode => Device,
        AuthStrategy.AuthCode => AuthCode,
        _ => Pat,
    };
}
