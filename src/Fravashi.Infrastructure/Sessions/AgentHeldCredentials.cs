using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Projects;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// The credentials the studio itself puts in an agent child's environment (backlog 108), read from the same
/// place the session runners read them, so what is looked for is exactly what was handed out.
/// <para>
/// Asked for at each use and never cached: a key can be saved, rotated or cleared while a session runs, and
/// a stale copy here would be both a second place a credential lives and a check against the wrong value.
/// An address is not a credential, so a broker base URL is left out; everything else the store injects is
/// either a provider key or a bearer token.
/// </para>
/// </summary>
public static class AgentHeldCredentials
{
    // Every runtime the catalog OFFERS, read from it rather than listed here, so a runtime added later cannot
    // forget itself and a retired one (docs/v2.md section 1) is never asked for. What stands in for the
    // retired ones is stronger than this check, not weaker: their prefixes came off the inheritance allowlist
    // in AgentProcessEnvironment, so nothing under OPENAI_, CODEX_ or COPILOT_ reaches a child at all.
    private static readonly AgentTool[] Tools = [.. AgentRuntimeCatalog.Tools];

    /// <summary>
    /// What every agent runtime is currently being given, and (backlog 154) every secret variable a project hands
    /// its agents, so a value a person marked secret is looked for exactly like the provider key is.
    /// </summary>
    public static IReadOnlyCollection<AgentHeldCredential> From(
        IAgentRuntimeCredentialStore? store, IProjectEnvironment? projects = null)
    {
        var held = new List<AgentHeldCredential>();
        if (projects is not null)
        {
            held.AddRange(projects.Held());
        }

        if (store is null)
        {
            return held;
        }

        foreach (var tool in Tools)
        {
            foreach (var (name, value) in store.EnvironmentFor(tool))
            {
                if (string.IsNullOrWhiteSpace(value) || IsAnAddress(value))
                {
                    continue;
                }

                held.Add(new AgentHeldCredential(name, value));
            }
        }

        return held;
    }

    private static bool IsAnAddress(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
