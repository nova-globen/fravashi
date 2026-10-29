using System.Diagnostics;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>
/// The environment an agent CLI child process is allowed to see (backlog 72).
/// <para>
/// The studio's own process environment carries live credentials: the host bridge URL and token
/// (<c>FRAVASHI_BRIDGE_URL</c> / <c>FRAVASHI_BRIDGE_TOKEN</c>, which read and write the user's REAL
/// folders), the window token, and the tamper-evidence HMAC key. A child started with
/// <c>UseShellExecute = false</c> inherits every one of them, because .NET seeds
/// <see cref="ProcessStartInfo.Environment"/> from the current process the first time it is read. That
/// inheritance quietly undoes the containment invariant (plan 2d decision 2) the working directory
/// enforces: an agent confined to <c>/workspaces/{id}</c> would still be holding a working credential
/// for everything outside it.
/// </para>
/// <para>
/// This list is an ALLOWLIST, so it is default-deny: a secret someone adds to the container tomorrow is
/// withheld from every agent with no code change. What is on it is what a CLI genuinely needs to run,
/// find itself, authenticate, and reach the network.
/// </para>
/// <para>
/// CALL <see cref="Restrict(ProcessStartInfo)"/> FIRST, immediately after the start info is created and
/// before the caller adds anything on purpose. It removes what is not on the list, so anything added
/// afterwards (the stored provider credential, the project Node PATH prefix, a command's own
/// environment) survives, and anything added before it would not.
/// </para>
/// </summary>
public static class AgentProcessEnvironment
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        // POSIX process basics. PATH is how the CLI finds node and its own siblings; HOME is where the
        // vendor sign-in writes and reads its credentials.
        "PATH",
        "HOME",
        "SHELL",
        "USER",
        "LOGNAME",
        "TERM",
        "TZ",
        "LANG",
        "LANGUAGE",
        "TMPDIR",

        // The .NET SDK in the image (backlog 151): the three that keep an agent's first `dotnet` from printing a
        // welcome banner and phoning home. Named one by one rather than as a DOTNET_ prefix, because a prefix is
        // allowed wholesale and nothing under this one should ever be assumed harmless in advance.
        "DOTNET_CLI_TELEMETRY_OPTOUT",
        "DOTNET_NOLOGO",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE",

        // XDG locations the CLIs honor for config, cache and state.
        "XDG_CONFIG_HOME",
        "XDG_CACHE_HOME",
        "XDG_DATA_HOME",
        "XDG_STATE_HOME",
        "XDG_RUNTIME_DIR",

        // Windows process essentials (a host `dotnet run` and the whole test suite). A Windows child
        // without these fails in ways that look like a broken CLI rather than a missing variable.
        "SystemRoot",
        "SystemDrive",
        "windir",
        "COMSPEC",
        "PATHEXT",
        "TEMP",
        "TMP",
        "USERPROFILE",
        "HOMEDRIVE",
        "HOMEPATH",
        "APPDATA",
        "LOCALAPPDATA",
        "ALLUSERSPROFILE",
        "ProgramData",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "ProgramW6432",
        "CommonProgramFiles",
        "CommonProgramFiles(x86)",
        "CommonProgramW6432",
        "PUBLIC",
        "USERNAME",
        "USERDOMAIN",
        "COMPUTERNAME",
        "NUMBER_OF_PROCESSORS",
        "OS",
        "PROCESSOR_ARCHITECTURE",
        "PROCESSOR_ARCHITEW6432",

        // Node's extra trust anchors. NODE_OPTIONS is deliberately NOT here: it injects code into node
        // and nothing the product does needs it.
        "NODE_EXTRA_CA_CERTS",

        // Egress through a corporate proxy (the lowercase spellings match too, the comparer ignores case).
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",

        // TLS trust on a host with its own certificate authority.
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
        "CURL_CA_BUNDLE",
        "REQUESTS_CA_BUNDLE",
    };

    /// <summary>
    /// Name prefixes that are allowed wholesale: locale, npm's own settings (including the global prefix
    /// that lives on the data volume), and the vendor CLIs' own configuration. None of these is ever a
    /// Fravashi credential. The CLIs' own key and token variables live under these prefixes too, and those
    /// are the exception: <see cref="NeverInherited"/> takes them back out.
    /// </summary>
    private static readonly string[] AllowedPrefixes =
    [
        "LC_",
        "NPM_CONFIG_",
        "ANTHROPIC_",
        "CLAUDE_",

        // RETIRED to v2 with the runtimes that needed them, decided 2026-09-22 (docs/v2.md section 1).
        // Leaving them allowed would have been a real regression rather than dead weight: NeverInherited is
        // derived from the catalog, so dropping the two descriptors took OPENAI_API_KEY and
        // COPILOT_GITHUB_TOKEN off it, and a prefix allowed wholesale would then have let a key sitting in the
        // studio's own environment through to a child. That is exactly the defect backlog 108 fixed. Removing
        // the prefixes is strictly more restrictive than before, so nothing under them is inherited at all.
        // "OPENAI_",
        // "CODEX_",
        // "COPILOT_",
    ];

    /// <summary>
    /// The variables that CARRY a provider credential, for every runtime in the catalog: the key variable and
    /// the broker's bearer variable. They are never INHERITED, although each sits under a prefix that is
    /// allowed wholesale (backlog 108, third clause). A credential reaches a child only when the credential
    /// store puts it there on purpose, after <see cref="Restrict"/>, which is what every runner does. So with
    /// the broker carrying a runtime the child holds a short-lived token and no key, whatever the studio's
    /// own environment happens to hold, and nothing here needs <c>ANTHROPIC_API_KEY</c> to be allowed.
    /// Read from the catalog so a runtime added later cannot forget itself.
    /// </summary>
    private static readonly HashSet<string> NeverInherited = new(
        AgentRuntimeCatalog.All
            .SelectMany(descriptor => new[] { descriptor.ApiKeyEnvVar, descriptor.BrokerAuthTokenEnvVar, descriptor.SubscriptionTokenEnvVar })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a child process may INHERIT the variable with this name.</summary>
    public static bool IsAllowed(string name)
    {
        if (string.IsNullOrEmpty(name) || NeverInherited.Contains(name))
        {
            return false;
        }

        return Allowed.Contains(name)
            || AllowedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The pinned CLI's switch for the claude.ai connectors of the account it is signed in with; 0, false, no or
    /// off turns them off (read from the pinned binary, backlog 174). Refused as a project variable.
    /// </summary>
    public const string ClaudeAiConnectorsVariable = "ENABLE_CLAUDEAI_MCP_SERVERS";

    /// <summary>
    /// Backlog 189: the pinned CLI's switch that stops it moving a command to the background. Read from the pinned
    /// 2.1.280 binary: a Bash call may be auto-backgrounded only when this is unset (and the settings do not disable
    /// background tasks), and only then does a call that reaches its timeout get moved to the background instead of
    /// being stopped; a message arriving mid-command no longer backgrounds it either. Nothing in the studio can wait on
    /// a background task, so a backgrounded build is a build the agent never sees finish. Refused as a project variable.
    /// The name lives in the Domain (backlog 192), because the approval card describes calls as the CLI treats them
    /// with this set, and two copies of the name could drift.
    /// </summary>
    public const string DisableBackgroundTasksVariable = AgentCliSwitches.DisableBackgroundTasksVariable;

    /// <summary>
    /// Backlog 189: the timeout a Bash call gets when it names none. The pinned CLI defaults to 120000 (two minutes)
    /// and caps a call at 600000 unless <c>BASH_MAX_TIMEOUT_MS</c> says otherwise; with background tasks off, a call
    /// past its timeout is stopped, so the default has to cover a whole solution build plus a test project in the
    /// container (about three minutes each). Ten minutes, the cap the CLI already allows. Refused as a project variable.
    /// </summary>
    public const string BashDefaultTimeoutVariable = "BASH_DEFAULT_TIMEOUT_MS";

    /// <summary>The value <see cref="BashDefaultTimeoutVariable"/> is set to: ten minutes.</summary>
    public const string BashDefaultTimeoutMilliseconds = "600000";

    /// <summary>
    /// Backlog 293: the pinned CLI's tool-call limit for MCP servers. Read from the pinned 2.1.280 binary: for an http
    /// server (the studio's tool server is one) every request is aborted after this many milliseconds, floored at 60000,
    /// and after 60000 when it is unset, which is how a five-minute scenario run came back to the agent as "The operation
    /// timed out" while it went on in the studio. It also bounds the whole call. Refused as a project variable.
    /// </summary>
    public const string McpToolTimeoutVariable = "MCP_TOOL_TIMEOUT";

    /// <summary>
    /// Backlog 293: the pinned CLI's idle limit for an MCP call, five minutes for an http server when unset, which
    /// <see cref="McpToolTimeoutVariable"/> does NOT raise; a silent build past five minutes would still be cut off.
    /// Refused as a project variable under the CLAUDE_ prefix.
    /// </summary>
    public const string McpToolIdleTimeoutVariable = "CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT";

    /// <summary>
    /// Backlog 293: what both MCP limits are set to. It bounds a scenario run whose build, start, ONE step and design check
    /// each reach their own ceiling, derived from those ceilings so it moves with them. It is not a bound on every run: a
    /// scenario with several slow steps runs each under its own ceiling and can still exceed it, and then the CLI still
    /// reports a timeout while the run goes on (a second start of it is refused, <see cref="Application.WorkItems.ScenarioRunner"/>).
    /// </summary>
    public static readonly TimeSpan McpToolLimit =
        AppRuns.AppRunnerClient.BuildWait
        + AppRuns.AppRunnerClient.ReadyWait
        + Application.WorkItems.ScenarioRunner.StepTimeout
        + Application.Verification.DesignCheckRequest.DefaultBudget;

    /// <summary>The value both MCP limits are set to: <see cref="McpToolLimit"/> in milliseconds.</summary>
    public static string McpToolTimeoutMilliseconds =>
        ((long)McpToolLimit.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Takes everything that is not on the allowlist off the child's environment, undoing the inheritance
    /// .NET seeded it with. Call this BEFORE adding anything the child is meant to have.
    /// </summary>
    public static void Restrict(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        // Reading Environment is what materialises the inherited copy; the ToList is required because
        // removing while enumerating the live key collection throws.
        var environment = startInfo.Environment;
        foreach (var name in environment.Keys.Where(name => !IsAllowed(name)).ToList())
        {
            environment.Remove(name);
        }

        // Backlog 174: signed in with a person's subscription, the CLI loaded their claude.ai connectors (Gmail,
        // Drive, Calendar) CONNECTED into every child. Set here because every agent child's environment is made
        // here, the one-shot calls with no settings file included; a session's settings say the same.
        environment[ClaudeAiConnectorsVariable] = "false";

        // Backlog 189: a long build stays in the foreground and returns its result to the agent. Both are set rather
        // than inherited, so a CLAUDE_ value in the studio's own environment (allowed by prefix) cannot undo them.
        environment[DisableBackgroundTasksVariable] = AgentCliSwitches.DisableBackgroundTasksValue;
        environment[BashDefaultTimeoutVariable] = BashDefaultTimeoutMilliseconds;

        // Backlog 293: the two limits were read from the pinned 2.1.280 binary and are set here so a scenario run that builds
        // the application is not cut off at the CLI's defaults. A run outlasting those defaults has not yet been observed in
        // a window; item 293 holds that live proof.
        environment[McpToolTimeoutVariable] = McpToolTimeoutMilliseconds;
        environment[McpToolIdleTimeoutVariable] = McpToolTimeoutMilliseconds;
    }
}
