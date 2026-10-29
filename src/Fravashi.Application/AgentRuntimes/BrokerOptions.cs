namespace Fravashi.Application.AgentRuntimes;

/// <summary>
/// The client-side switch for the remote credential broker (ADR 0010, corporate / untrusted-client tier). When
/// active, the agent-runtime credential layer points a brokerable CLI at a hosted <b>gateway</b>
/// (<c>ANTHROPIC_BASE_URL</c> + a short-lived per-user <b>bearer token</b>) <b>instead of</b> injecting the
/// organization's LLM key - so the org key never reaches the container the user is root over. The default
/// (single-user) posture is <see cref="Disabled"/>: brokering off, org key injected locally exactly as before
/// (ADR 0003, keyless-of-nobody).
/// <para>
/// Product-neutral by construction - <c>broker</c> / <c>gateway</c> / <c>token</c>, never a vendor product name.
/// Bound from the <c>Broker</c> configuration section (B1 gates on this flag); the publish-time
/// <c>corp-broker</c> variant that flips it on for a release is wired later (B4). The per-user token is supplied
/// here (B1) by <b>name</b> - from the encrypted secret store, or an environment variable - and is <b>minted</b>
/// from the SSO/OAuth identity later (B3). The token value itself is never held in this options object or in
/// plaintext configuration.
/// </para>
/// </summary>
public sealed record BrokerOptions
{
    /// <summary>The default secret-store reference the per-user bearer token is read from (where B3 mints it).</summary>
    public const string DefaultUserTokenSecretRef = "broker:user-token";

    /// <summary>The disabled (single-user default) posture: brokering off, org key injected locally as today.</summary>
    public static readonly BrokerOptions Disabled = new();

    /// <summary>True when the deployment declares the brokered posture (D4; a config flag in B1, the variant in B4).</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// True in the <c>corp-broker</c> variant (B4): the local org-key injection path is switched OFF. The credential
    /// layer must never read or emit the organization key on this device - and it fails <b>closed</b> (injects
    /// nothing) if the broker is inert (no <see cref="BaseUrl"/>, or a non-brokerable runtime) rather than reverting
    /// to the local key. Set only by the compiled/image variant (<see cref="ForVariant"/>), never by loose config, so
    /// the guarantee tracks the baked-in variant identity rather than a mutable setting.
    /// </summary>
    public bool LocalKeyForbidden { get; init; }

    /// <summary>The gateway base URL the CLI is pointed at (becomes <c>ANTHROPIC_BASE_URL</c>). Required when enabled.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// The secret-store reference the short-lived per-user bearer token is read from. B3 mints the token into the
    /// encrypted secret store under this reference; B1 may instead supply it via <see cref="UserTokenEnvVar"/>.
    /// This is a <b>reference</b>, never the token value.
    /// </summary>
    public string UserTokenSecretRef { get; init; } = DefaultUserTokenSecretRef;

    /// <summary>
    /// Optional: the <b>name</b> of an environment variable that supplies the per-user bearer token, used when the
    /// secret store holds none (B1's supply mechanism, superseded by minting in B3). This is a variable name,
    /// never the token value; the value stays out of configuration.
    /// </summary>
    public string? UserTokenEnvVar { get; init; }

    /// <summary>
    /// The IdP the user signs in with to prove identity for minting (B3): <c>github</c> or <c>entra</c>, matching
    /// the ADR 0004 device-flow authenticators. The broker independently verifies the proof against the provider
    /// it trusts; this only tells the client which sign-in to offer. Defaults to <c>github</c>.
    /// </summary>
    public string IdentityProvider { get; init; } = "github";

    /// <summary>
    /// True only when brokering is on <b>and</b> a gateway base URL is configured - an enabled broker with no base
    /// URL is inert (never a silent half-configured state that would fall back to the org key).
    /// </summary>
    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>
    /// True for the free tier: the broker serves a runtime only while the person has no credential
    /// of their own for it. A stored API key or subscription token is used instead, and stays allowed. False for a
    /// broker an install was pointed at on purpose (<c>fravashi broker &lt;url&gt;</c>) and for the corp-broker variant,
    /// where the broker carries the runtime whatever is stored.
    /// </summary>
    public bool OwnCredentialFirst { get; init; }

    /// <summary>
    /// Turns the free tier off for this install (<c>Broker:FreeTierDisabled</c>): with no broker configured, the agent
    /// CLI then runs only on the person's own credential, as before the free tier.
    /// </summary>
    public bool FreeTierDisabled { get; init; }

    /// <summary>
    /// The free tier: an install with no broker of its own uses the broker at its build's service
    /// host (<paramref name="freeTierBaseUrl"/>, <c>ServiceHost.BrokerBaseUrl</c>), behind the person's own credential.
    /// A configured broker, a variant that forces one, or <see cref="FreeTierDisabled"/> leave the options unchanged.
    /// </summary>
    public BrokerOptions WithFreeTier(string freeTierBaseUrl) =>
        IsActive || FreeTierDisabled || string.IsNullOrWhiteSpace(freeTierBaseUrl)
            ? this
            : this with { Enabled = true, BaseUrl = freeTierBaseUrl, OwnCredentialFirst = !LocalKeyForbidden };

    /// <summary>
    /// Folds the publish-time variant switch (B4) into these config-bound options: when the running build is the
    /// <c>corp-broker</c> variant (<paramref name="brokerRequired"/> is the compiled/image
    /// <see cref="Variants.FeatureFlags.BrokerRequired"/>), brokering is forced <b>on</b> and the local org-key path
    /// is forbidden - so a <c>corp-broker</c> image brokers even if the operator never set <c>Broker:Enabled</c>, and
    /// never injects the org key even if the broker is misconfigured. Off-variant, the config-bound options stand
    /// unchanged (the single-user default). The <see cref="BaseUrl"/> still comes from deploy config either way.
    /// </summary>
    public BrokerOptions ForVariant(bool brokerRequired) =>
        brokerRequired ? this with { Enabled = true, LocalKeyForbidden = true } : this;

    /// <summary>
    /// The broker's <c>host[:port]</c>, derived from <see cref="BaseUrl"/> - the value the ADR 0007 egress allowlist
    /// proxy must permit so the container can reach the in-perimeter broker (D5). Returns null when no base URL is
    /// configured. This only <b>expresses</b> the allowlist entry; the filtering proxy that would <b>enforce</b> it is
    /// the deferred ADR 0007 increment (see the backlog) - configuring the endpoint here does not by itself lock down
    /// egress.
    /// </summary>
    public string? EgressAllowlistEntry =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
            ? (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}")
            : null;
}
