using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.AgentRuntimes;

/// <summary>
/// Stores the headless credential (API key / token) for each coding-agent runtime and surfaces it as the
/// environment the CLI expects. The key is held in the encrypted <see cref="Persistence.ISecretStore"/> (never
/// in the settings table, never plaintext), and injected under the runtime's own env var
/// (<c>ANTHROPIC_API_KEY</c>, <c>OPENAI_API_KEY</c>, <c>COPILOT_GITHUB_TOKEN</c>) on every non-interactive run -
/// so a stored key is a <b>real</b> proof of headless authentication, not an assumed one.
/// </summary>
public interface IAgentRuntimeCredentialStore
{
    /// <summary>Saves (or overwrites) the headless credential for a runtime.</summary>
    void SaveApiKey(AgentTool tool, string apiKey);

    /// <summary>Removes the stored credential for a runtime.</summary>
    void ClearApiKey(AgentTool tool);

    /// <summary>True when a headless credential is stored for a runtime.</summary>
    bool HasApiKey(AgentTool tool);

    /// <summary>
    /// True when a stored key is what authenticates this runtime. False when the credential broker carries it,
    /// or when this build forbids a local key altogether: then a stored key authenticates nothing, saving one
    /// is refused, and one left over from before is something to report and remove (backlog 108: the target
    /// state is no provider key stored here at all). Defaults to true, which is the single-user posture.
    /// </summary>
    bool UsesStoredKey(AgentTool tool) => true;

    /// <summary>
    /// Saves the long-lived subscription token the runtime's sign-in printed (backlog 183), encrypted like a key.
    /// Throws <see cref="InvalidOperationException"/> where a stored credential would authenticate nothing (the
    /// broker carries the runtime) or the runtime has no such token.
    /// </summary>
    void SaveSubscriptionToken(AgentTool tool, string token) =>
        throw new InvalidOperationException($"{AgentRuntimeCatalog.For(tool).DisplayName} has no subscription token to store here.");

    /// <summary>True when a subscription token is stored for a runtime.</summary>
    bool HasSubscriptionToken(AgentTool tool) => false;

    /// <summary>Removes the stored subscription token for a runtime.</summary>
    void ClearSubscriptionToken(AgentTool tool)
    {
    }

    /// <summary>
    /// The environment variables to inject when running the CLI non-interactively, resolved from the stored
    /// credential. Empty when nothing is stored. The interactive login proxy deliberately does NOT apply these,
    /// so a token never short-circuits the sign-in flow the user is trying to run.
    /// </summary>
    IReadOnlyDictionary<string, string> EnvironmentFor(AgentTool tool);

    /// <summary>
    /// True when the credential broker carries this runtime right now: its runs go through the broker with the person's
    /// minted token. Under the free tier that is only while no credential of the person's own is
    /// stored. Defaults to false, the single-user posture.
    /// </summary>
    bool BrokerCarries(AgentTool tool) => false;

    /// <summary>
    /// The model a run of this runtime uses for <paramref name="requested"/>: what was asked for, unless the
    /// credential broker carries the runtime and does not cover it (<see cref="Broker.BrokerModelPolicy"/>). Defaults to
    /// what was asked for, which is the single-user posture.
    /// </summary>
    Broker.AgentModelChoice ModelFor(AgentTool tool, string? requested) => Broker.AgentModelChoice.AsAsked(requested);
}
