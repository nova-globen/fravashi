using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Persistence;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>
/// <see cref="IAgentRuntimeCredentialStore"/> over <see cref="ISecretStore"/>. The headless credential for each
/// runtime is stored encrypted under <c>agent-runtime:{tool}:apikey</c> and surfaced as the CLI's own env var,
/// so a stored key both authenticates every non-interactive run and serves as the real proof of headless auth.
/// <para>
/// When the remote broker is active (ADR 0010, <see cref="BrokerOptions.IsActive"/>) and the runtime supports
/// brokering (D3 - Claude only in v1), the environment is redirected to the gateway instead: the runtime's
/// base-URL var points at the broker and the runtime's bearer var carries the short-lived per-user token - and
/// the organization's key is <b>never</b> read or emitted (not the key, not a fallback). The single-user default
/// (broker disabled) is byte-for-byte unchanged: the org key is injected locally exactly as before.
/// </para>
/// </summary>
public sealed class AgentRuntimeCredentialStore : IAgentRuntimeCredentialStore
{
    private readonly ISecretStore _secrets;
    private readonly BrokerOptions _broker;
    private readonly Func<string, string?> _env;

    /// <summary>Single-user default: broker disabled, org key injected locally (used by tests and non-brokered hosts).</summary>
    public AgentRuntimeCredentialStore(ISecretStore secrets)
        : this(secrets, BrokerOptions.Disabled)
    {
    }

    public AgentRuntimeCredentialStore(ISecretStore secrets, BrokerOptions broker, Func<string, string?>? env = null)
    {
        _secrets = secrets;
        _broker = broker ?? BrokerOptions.Disabled;
        _env = env ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>The secret-store reference for a runtime's headless credential.</summary>
    public static string SecretRef(AgentTool tool) => $"agent-runtime:{tool.ToString().ToLowerInvariant()}:apikey";

    public void SaveApiKey(AgentTool tool, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        // Backlog 108, third clause. A key saved while nothing would use it is pure exposure: it sits in a
        // container the agents share, and removing it from there is the broker's whole purpose. Refused rather
        // than stored and ignored, so the state backlog 108 asks for cannot be undone by a paste.
        if (!UsesStoredKey(tool))
        {
            throw new InvalidOperationException(
                $"{AgentRuntimeCatalog.For(tool).DisplayName} is authenticated through the credential broker here, "
                + "so a provider key is not stored in this container.");
        }

        _secrets.Set(SecretRef(tool), apiKey.Trim());
    }

    public bool UsesStoredKey(AgentTool tool) =>
        !_broker.LocalKeyForbidden
        && !(_broker.IsActive && !_broker.OwnCredentialFirst && AgentRuntimeCatalog.For(tool).SupportsBrokering);

    /// <summary>
    /// The broker carries a runtime that supports it while it is active, except under the free tier (decision
    /// D52) once the person has stored a key or a subscription token of their own: that credential is used instead.
    /// </summary>
    public bool BrokerCarries(AgentTool tool) =>
        _broker.IsActive
        && AgentRuntimeCatalog.For(tool).SupportsBrokering
        && !(_broker.OwnCredentialFirst && (HasApiKey(tool) || HasSubscriptionToken(tool)));

    public void ClearApiKey(AgentTool tool) => _secrets.Remove(SecretRef(tool));

    public bool HasApiKey(AgentTool tool) => !string.IsNullOrWhiteSpace(_secrets.Get(SecretRef(tool)));

    /// <summary>The secret-store reference for a runtime's long-lived subscription token (backlog 183).</summary>
    public static string SubscriptionTokenRef(AgentTool tool) =>
        $"agent-runtime:{tool.ToString().ToLowerInvariant()}:subscription-token";

    public void SaveSubscriptionToken(AgentTool tool, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var descriptor = AgentRuntimeCatalog.For(tool);
        if (descriptor.SubscriptionTokenEnvVar is null)
        {
            throw new InvalidOperationException($"{descriptor.DisplayName} has no subscription token to store here.");
        }

        // The same rule as a key (backlog 108): where the broker carries the runtime, a credential stored here would
        // authenticate nothing and only sit in a container the agents share.
        if (!UsesStoredKey(tool))
        {
            throw new InvalidOperationException(
                $"{descriptor.DisplayName} is authenticated through the credential broker here, so a subscription token "
                + "is not stored in this container.");
        }

        _secrets.Set(SubscriptionTokenRef(tool), token.Trim());
    }

    public bool HasSubscriptionToken(AgentTool tool) => !string.IsNullOrWhiteSpace(_secrets.Get(SubscriptionTokenRef(tool)));

    public void ClearSubscriptionToken(AgentTool tool) => _secrets.Remove(SubscriptionTokenRef(tool));

    public IReadOnlyDictionary<string, string> EnvironmentFor(AgentTool tool)
    {
        var descriptor = AgentRuntimeCatalog.For(tool);

        // Broker mode (ADR 0010, tier-gated): for a brokerable runtime, point the CLI at the gateway with a
        // short-lived per-user bearer and NEVER inject the org key - not the key, not a fallback. A brokerable
        // runtime with no token supplied fails CLOSED (inject nothing) rather than reverting to the org key.
        if (BrokerCarries(tool))
        {
            var token = ResolveBrokerToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                return Empty();
            }

            // Infra D46: the CLI's own model choices (its side model, the aliases a subagent can name, its background
            // classifiers) are pointed at the model the broker serves, or each would ask for one it refuses.
            return new Dictionary<string, string>(
                BrokerModelPolicy.CliModelEnvironment(BrokerModelPolicy.ServedModel(AllowedModels())), StringComparer.Ordinal)
            {
                [descriptor.BrokerBaseUrlEnvVar!] = _broker.BaseUrl!.Trim(),
                [descriptor.BrokerAuthTokenEnvVar!] = token.Trim(),
            };
        }

        // corp-broker variant (B4): the local org-key path is compiled/configured OFF. Never read or emit the org
        // key - not for a brokerable runtime that reached here inert (broker misconfigured / no base URL), not for a
        // non-brokerable runtime (one with no broker pair on its descriptor). Fail CLOSED: inject nothing rather than revert to
        // the local key. This is what makes the "the org key never reaches this device" guarantee hold in the tier.
        if (_broker.LocalKeyForbidden)
        {
            return Empty();
        }

        // Default (single-user) path: inject the stored key under the runtime's own key var.
        var key = _secrets.Get(SecretRef(tool));
        if (!string.IsNullOrWhiteSpace(key))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [descriptor.ApiKeyEnvVar] = key,
            };
        }

        // Backlog 183: else the subscription token the sign-in printed, under its own variable. Every child gets
        // the same value from here, so no conversation can hold a copy that another one's refresh made stale.
        var subscription = descriptor.SubscriptionTokenEnvVar is null ? null : _secrets.Get(SubscriptionTokenRef(tool));
        return string.IsNullOrWhiteSpace(subscription)
            ? Empty()
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [descriptor.SubscriptionTokenEnvVar!] = subscription,
            };
    }

    /// <summary>
    /// Resolves the short-lived per-user bearer token by NAME: the encrypted secret store first (where B3 mints
    /// it), then an environment variable if the options name one (B1's supply mechanism). Never the org key.
    /// </summary>
    private string? ResolveBrokerToken()
    {
        var token = _secrets.Get(_broker.UserTokenSecretRef);
        if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(_broker.UserTokenEnvVar))
        {
            token = _env(_broker.UserTokenEnvVar);
        }

        return token;
    }

    public AgentModelChoice ModelFor(AgentTool tool, string? requested) =>
        BrokerCarries(tool)
            ? BrokerModelPolicy.Choose(AllowedModels(), requested)
            : AgentModelChoice.AsAsked(requested);

    /// <summary>The models the stored broker token covers, as its mint answer named them (null when none was named).</summary>
    private IReadOnlyList<string> AllowedModels() =>
        BrokerModelPolicy.PatternsOrFallback(
            BrokerTokenMetadata.Parse(_secrets.Get(BrokerReferences.TokenMetadataSecretRef))?.AllowedModels);

    private static Dictionary<string, string> Empty() => new(StringComparer.Ordinal);
}
