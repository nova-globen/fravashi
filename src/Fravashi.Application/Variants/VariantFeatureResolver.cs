namespace Fravashi.Application.Variants;

/// <summary>
/// Pure resolution of the running app's <see cref="FeatureFlags"/> (I1): start from the variant's compiled-in
/// defaults (baked at publish time), then let an explicit image environment variable override each switch. Kept
/// pure + injectable so the precedence is unit-testable without touching the process environment.
/// <para>
/// Precedence per switch: a recognised, non-empty env value wins; otherwise the compiled default stands. An empty
/// or unset env var is NOT an override (so an image that ships only <c>FravashiVariant</c> at compile time, with no
/// runtime env, still gets the right behaviour from the baked default).
/// </para>
/// </summary>
public static class VariantFeatureResolver
{
    /// <summary>Env var overriding <see cref="FeatureFlags.GitHubLogin"/> (Dockerfile build-arg → env).</summary>
    public const string GitHubLoginEnv = "FRAVASHI_GITHUB_LOGIN";

    /// <summary>Env var overriding <see cref="FeatureFlags.VpnRequired"/> (Dockerfile build-arg → env).</summary>
    public const string VpnRequiredEnv = "FRAVASHI_VPN_REQUIRED";

    /// <summary>Env var overriding <see cref="FeatureFlags.BrokerRequired"/> (Dockerfile build-arg → env).</summary>
    public const string BrokerRequiredEnv = "FRAVASHI_BROKER_REQUIRED";

    /// <summary>Env var overriding <see cref="FeatureFlags.AuthRequired"/> (Dockerfile build-arg → env).</summary>
    public const string AuthRequiredEnv = "FRAVASHI_AUTH_REQUIRED";

    /// <summary>Resolves the effective flags from the compiled defaults and an environment lookup.</summary>
    public static FeatureFlags Resolve(FeatureFlags compiledDefaults, Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(compiledDefaults);
        ArgumentNullException.ThrowIfNull(env);

        return new FeatureFlags(
            GitHubLogin: Override(env(GitHubLoginEnv)) ?? compiledDefaults.GitHubLogin,
            VpnRequired: Override(env(VpnRequiredEnv)) ?? compiledDefaults.VpnRequired,
            BrokerRequired: Override(env(BrokerRequiredEnv)) ?? compiledDefaults.BrokerRequired,
            AuthRequired: Override(env(AuthRequiredEnv)) ?? compiledDefaults.AuthRequired);
    }

    /// <summary>Parses a boolean-ish env override; null when the value is empty/unset/unrecognised (no override).</summary>
    public static bool? Override(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v switch
        {
            "1" or "true" or "on" or "yes" or "enabled" => true,
            "0" or "false" or "off" or "no" or "disabled" => false,
            _ => null,
        };
    }
}
