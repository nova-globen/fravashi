using Fravashi.Application.Variants;

namespace Fravashi.Infrastructure.Variants;

/// <summary>
/// The variant baked into THIS build at compile time (I1). The release pipeline publishes the app with
/// <c>-p:FravashiVariant=&lt;name&gt;</c>; Directory.Build.props turns that MSBuild property into a
/// <c>DefineConstants</c> symbol, and the <c>#if</c> below selects the compiled-in variant name + its default
/// feature switches. Because the switched-off default is chosen by the preprocessor, a variant that ships with a
/// feature off <b>genuinely omits</b> that default from the compiled binary - the runtime env var can only
/// re-affirm or (for image features) override it, never resurrect a default the build did not bake.
/// <para>
/// A plain local/dev/test build defines no variant symbol, so it compiles as <c>full</c> - every feature on. The
/// <c>corp-vpn</c> example locks down: GitHub SSO off (token-only) and VPN required. The <c>corp-broker</c> variant
/// (ADR 0010 / plan 04 B4) bakes <c>BrokerRequired</c> on - brokering switched on, the local org-key default
/// genuinely absent from the binary - while keeping SSO available (the broker needs a per-user token to mint).
/// </para>
/// </summary>
public static class CompiledVariant
{
#if VARIANT_CORP_VPN
    /// <summary>The variant name baked into this build.</summary>
    public const string Name = "corp-vpn";

    /// <summary>The compile-time default feature switches for this variant.</summary>
    public static readonly FeatureFlags Defaults = new(GitHubLogin: false, VpnRequired: true);
#elif VARIANT_CORP_BROKER
    /// <summary>The variant name baked into this build.</summary>
    public const string Name = "corp-broker";

    /// <summary>
    /// The compile-time default feature switches for this variant: brokering required (so the local-key default is
    /// baked out), SSO kept available (GitHub login on: sign-in is mandatory here to mint the per-user token), no
    /// VPN gate by default (the tunnel is a separately composable concern, see the variant docs). AuthRequired is
    /// on like everywhere; the BYOD release additionally BAKES the IdP coordinates as image env, which locks the
    /// settings fields read-only (plan Phase 3 follow-up).
    /// </summary>
    public static readonly FeatureFlags Defaults = new(GitHubLogin: true, VpnRequired: false, BrokerRequired: true, AuthRequired: true);
#else
    /// <summary>The variant name baked into this build.</summary>
    public const string Name = "full";

    /// <summary>The compile-time default feature switches for this variant.</summary>
    public static readonly FeatureFlags Defaults = FeatureFlags.Full;
#endif
}
