using System.Text.RegularExpressions;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;

namespace Fravashi.Application.Projects;

/// <summary>One variable a project hands its agents. A secret's <see cref="Value"/> is null everywhere it is listed.</summary>
public sealed record ProjectVariable(string Name, string? Value, bool IsSecret, DateTimeOffset UpdatedUtc);

/// <summary>
/// The environment variables a project hands the agents that work in its folders (backlog 154, decision
/// 2026-09-26). An agent child starts from a default-deny allowlist (backlog 72), so nothing reaches it by
/// accident; these reach it ON PURPOSE, because a person wrote them for this project. A variable is plain or a
/// SECRET: a secret is kept in the protected secret store, is never shown again once saved, and joins the list of
/// values the two guards look for (a call carrying it asks loudly, a file carrying it never reaches the host).
/// </summary>
public interface IProjectEnvironment
{
    /// <summary>Raised with the project when its variables change.</summary>
    event Action<ProjectId>? Changed;

    /// <summary>The project's variables by name, secrets without their values.</summary>
    IReadOnlyList<ProjectVariable> List(ProjectId project);

    /// <summary>
    /// Sets a variable. Answers the refusal sentence when the name is not one a project may set
    /// (<see cref="ProjectVariableNames.Refusal"/>), null when it was stored. Setting an existing name replaces it,
    /// including from plain to secret and back.
    /// </summary>
    string? Set(ProjectId project, string name, string value, bool secret);

    /// <summary>Removes a variable, and a secret's stored value with it.</summary>
    void Remove(ProjectId project, string name);

    /// <summary>Every variable with its value, secrets included: what an agent child of this project is given.</summary>
    IReadOnlyDictionary<string, string> Resolve(ProjectId project);

    /// <summary>
    /// Every SECRET value across every project, for the guards. Global on purpose: a secret is a secret whichever
    /// project it belongs to, and refusing to let one leave for another project's folder costs nothing.
    /// </summary>
    IReadOnlyCollection<AgentHeldCredential> Held();
}

/// <summary>
/// Which names a project may set. A variable that would change what the STUDIO handed the child, or what the
/// agent CLI or its runtime does with itself, is refused by name: the credential and broker variables the
/// catalogue owns, the studio's own <c>FRAVASHI_</c> and the CLI's <c>ANTHROPIC_</c> and <c>CLAUDE_</c> families,
/// the process basics the allowlist passes through, and the loader hooks that make a runtime execute code.
/// </summary>
public static partial class ProjectVariableNames
{
    private static readonly HashSet<string> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "SHELL", "USER", "LOGNAME", "TMPDIR", "TERM",
        "NODE_OPTIONS", "NODE_PATH", "LD_PRELOAD", "LD_LIBRARY_PATH", "DYLD_INSERT_LIBRARIES",
        "DOTNET_STARTUP_HOOKS", "COREHOST_TRACE",

        // Backlog 174: the studio switches the signed-in account's claude.ai connectors off for every agent child.
        "ENABLE_CLAUDEAI_MCP_SERVERS",

        // Backlog 189: the studio sets how long a Bash call may run in the foreground (CLAUDE_CODE_DISABLE_BACKGROUND_TASKS,
        // its partner, is under the refused CLAUDE_ prefix already). The cap is refused too, since it bounds the default.
        "BASH_DEFAULT_TIMEOUT_MS", "BASH_MAX_TIMEOUT_MS",

        // Backlog 293: the studio sets how long a call to its own tool server may take (CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT,
        // its partner, is under the refused CLAUDE_ prefix already).
        "MCP_TOOL_TIMEOUT",
    };

    private static readonly string[] Prefixes = ["FRAVASHI_", "ANTHROPIC_", "CLAUDE_"];

    /// <summary>Why this name may not be set, or null when it may.</summary>
    public static string? Refusal(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "A variable needs a name.";
        }

        var trimmed = name.Trim();
        if (!Shape().IsMatch(trimmed))
        {
            return "A variable name is letters, digits and underscores, and does not start with a digit.";
        }

        if (Exact.Contains(trimmed)
            || Prefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || CatalogueOwned().Any(owned => string.Equals(owned, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{trimmed} is set by the studio or read by the agent runtime itself, so a project may not set it.";
        }

        return null;
    }

    /// <summary>The names the runtime catalogue hands out itself: the provider key and the broker's address and token.</summary>
    private static IEnumerable<string> CatalogueOwned()
    {
        foreach (var tool in AgentRuntimeCatalog.Tools)
        {
            var descriptor = AgentRuntimeCatalog.For(tool);
            yield return descriptor.ApiKeyEnvVar;
            if (descriptor.BrokerBaseUrlEnvVar is { } url)
            {
                yield return url;
            }

            if (descriptor.BrokerAuthTokenEnvVar is { } token)
            {
                yield return token;
            }

            if (descriptor.SubscriptionTokenEnvVar is { } subscription)
            {
                yield return subscription;
            }
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Shape();
}
