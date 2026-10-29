using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.AgentRuntimes;

/// <summary>
/// How one coding-agent CLI is installed, updated, authenticated, and asked its version - the provider-specific
/// facts the neutral runtime services act on. Naming the vendor CLIs here is correct and unavoidable (like
/// "Jira" in the tracker adapters); the <b>ports</b> that consume this stay product-neutral. Every value was
/// verified against the CLI's own <c>--help</c> in the studio container (see docs/decision-log).
/// </summary>
/// <param name="Tool">The runtime this describes.</param>
/// <param name="DisplayName">The vendor's name for the CLI, for the UI.</param>
/// <param name="Command">The executable name on PATH.</param>
/// <param name="NpmPackage">The npm package the image installs and update-on-launch refreshes.</param>
/// <param name="PinnedVersion">
/// The EXACT version update-on-launch installs (the image does not carry the CLI). Never <c>latest</c>: the studio drives an
/// unpublished protocol (stream-json, the PreToolUse hook contract, ADR 0019's MCP integration), so a vendor
/// release must not reach a user's machine unannounced. Moving this number is the deliberate promotion act
/// described in ADR 0023, gated on the contract tests against the real CLI.
/// </param>
/// <param name="ProtocolVerifiedVersion">
/// The version whose protocol someone ACTUALLY drove the contract tests against. The rest of that record,
/// the date and a fingerprint of the contract file AS DRIVEN, lives in <c>AgentCliPinTests</c>, because a
/// verification is a claim about a file and a binary together: without the file half, the file grew two
/// live tests and this number went on asserting a run that had covered neither. This is a separate fact from
/// <see cref="PinnedVersion"/>: conflating the two is what let a pin bump silently rewrite a claim about
/// verification (ADR 0023), so the two numbers move in separate, deliberate edits and
/// <c>AgentCliPinTests</c> fails while they disagree. Null for a runtime the studio does not drive over a
/// protocol at all: pinned, but with nothing verified about its wire format, and the product must not say
/// otherwise. Every runtime this version offers is driven over a protocol, so nothing sets it null today.
/// </param>
/// <param name="VersionArguments">Arguments that print the version and exit (parsed for a version number).</param>
/// <param name="AuthStatusArguments">
/// The CLI's own auth-status command, when it has one that is scriptable and non-interactive; null when it does
/// not (then only a stored key can prove authentication - honest <see cref="AgentRuntimeAuthState.Unknown"/>
/// otherwise). Verified: <c>claude auth status</c> (JSON).
/// </param>
/// <param name="AuthStatusSignedInMarker">
/// A case-insensitive substring in the auth-status output that means "signed in", when the command exists.
/// </param>
/// <param name="AuthStatusSignedOutMarker">
/// A case-insensitive substring that unambiguously means "signed out", checked BEFORE the signed-in marker so a
/// negative message that happens to contain the positive substring (a CLI answering "Not logged in" contains
/// "logged in") is classified correctly. Null when not needed.
/// </param>
/// <param name="ApiKeyEnvVar">
/// The environment variable that authenticates the CLI headlessly. We store the user's key in the secret store
/// and inject it under this name on every non-interactive run (status probe, headless prompt, the session runner).
/// </param>
/// <param name="ApiKeyLabel">What to call the headless credential in the UI (an API key vs a token).</param>
/// <param name="ApiKeyHelp">A one-line hint on where the user gets that credential.</param>
/// <param name="BrokerBaseUrlEnvVar">
/// The environment variable that redirects the CLI's API traffic to a remote broker/gateway (ADR 0010), when the
/// runtime can be brokered. Null when brokering is not supported for this runtime (ADR 0010 D3 settled it as
/// Anthropic/<c>claude</c> only). For Claude this is <c>ANTHROPIC_BASE_URL</c>.
/// </param>
/// <param name="BrokerAuthTokenEnvVar">
/// The environment variable that carries the short-lived per-user <b>bearer</b> token to the broker as an
/// <c>Authorization: Bearer</c> header - distinct from <see cref="ApiKeyEnvVar"/> (the org key, sent as the
/// provider's own key header). For Claude this is <c>ANTHROPIC_AUTH_TOKEN</c> (verified against Claude Code's env
/// contract). Null when the runtime is not brokerable in v1.
/// </param>
/// <param name="InteractiveLoginArguments">
/// The CLI's interactive sign-in command, streamed through the login proxy. These print a URL / device code the
/// user completes in a browser. They typically require a TTY, so the proxy runs them under <c>script</c>. It
/// must be a flow that needs NO inbound connection to the CLI: the CLI is in the container, the browser is on
/// the host, and the launcher publishes the studio's port and nothing else, so a loopback OAuth callback can
/// never arrive.
/// </param>
/// <param name="SupportsInteractiveLogin">
/// False when the CLI has no sign-in command that can work in a headless container at all - then the UI shows
/// only the token path and says so, never a dead button.
/// </param>
/// <param name="SubscriptionTokenEnvVar">
/// When the interactive sign-in PRINTS a long-lived token instead of storing a refreshable one in the CLI's config
/// home: the variable that token authenticates the CLI through. The studio captures it from the sign-in's output,
/// never shows it, stores it encrypted and injects it into every child (backlog 183, decided 2026-10-01: a
/// refreshable sign-in copied into each conversation's config home went stale the moment one copy refreshed). Null
/// when the sign-in stores its own credential.
/// </param>
/// <param name="SeesImages">
/// True when a conversation on this runtime can be handed an image and the model reads it (backlog 52, ADR 0021's
/// consequence that turning a drawing into a mockup needs a model that can see). A recorded fact, never inferred:
/// it defaults to false, so a runtime added later claims it on purpose, and the feature that needs it says so
/// rather than producing a bad tree from words alone.
/// </param>
public sealed record AgentRuntimeDescriptor(
    AgentTool Tool,
    string DisplayName,
    string Command,
    string NpmPackage,
    string PinnedVersion,
    string? ProtocolVerifiedVersion,
    IReadOnlyList<string> VersionArguments,
    IReadOnlyList<string>? AuthStatusArguments,
    string? AuthStatusSignedInMarker,
    string? AuthStatusSignedOutMarker,
    string ApiKeyEnvVar,
    string ApiKeyLabel,
    string ApiKeyHelp,
    string? BrokerBaseUrlEnvVar,
    string? BrokerAuthTokenEnvVar,
    IReadOnlyList<string> InteractiveLoginArguments,
    bool SupportsInteractiveLogin,
    string? SubscriptionTokenEnvVar = null,
    bool SeesImages = false)
{
    /// <summary>
    /// The npm arguments that install EXACTLY the pinned version (backlog 61 / ADR 0023). This is the single
    /// seam every install path goes through (update-on-launch, the onboarding "Install now" button, the
    /// dashboard card), so no caller can reach an unpinned release.
    /// </summary>
    public IReadOnlyList<string> UpdateArguments => ["install", "-g", PinnedSpecifier];

    /// <summary>The npm specifier the product installs, package and pinned version together.</summary>
    public string PinnedSpecifier => $"{NpmPackage}@{PinnedVersion}";

    /// <summary>
    /// True when a reported version is known AND differs from the pin. An unknown version (null, empty, the
    /// probe not having run yet) is deliberately NOT drift: claiming it would be a warning with no mechanism
    /// behind it, and it would fire on every developer host where nothing installs the CLI at all.
    /// </summary>
    public bool IsOffPin(string? reportedVersion) =>
        !string.IsNullOrWhiteSpace(reportedVersion)
        && !string.Equals(reportedVersion.Trim(), PinnedVersion, StringComparison.Ordinal);

    /// <summary>
    /// True when this runtime can be pointed at a remote credential broker (ADR 0010) - i.e. it has both a
    /// base-URL and a bearer-token env var (ADR 0010 D3: Claude).
    /// </summary>
    public bool SupportsBrokering => BrokerBaseUrlEnvVar is not null && BrokerAuthTokenEnvVar is not null;
}

/// <summary>The catalog of the supported coding-agent runtimes, keyed by <see cref="AgentTool"/>.</summary>
public static class AgentRuntimeCatalog
{
    /// <summary>
    /// Every supported runtime, in display order. The <c>PinnedVersion</c> values here are the SAME numbers the
    /// Dockerfile's <c>CLAUDE_CODE_VERSION</c> build arg records (update-on-launch installs it at the first start; the
    /// image does not carry the CLI), held together by <c>AgentCliPinTests</c>, which reads the real Dockerfile. Promotion is the ordered
    /// procedure in ADR 0023, never an edit here alone.
    /// </summary>
    public static IReadOnlyList<AgentRuntimeDescriptor> All { get; } =
    [
        new(
            AgentTool.Claude,
            "Claude Code",
            "claude",
            "@anthropic-ai/claude-code",
            PinnedVersion: "2.1.280",
            // Promoted from 2.1.222 on 2026-09-30 (the vendor's "stable" tag; 2.1.251 was the oldest the provider
            // accepts Fable 5.1 from, backlog 168). The whole of ClaudeCliContractTests, live tier included, was
            // driven against 2.1.280 installed for the purpose, 11 of 11 twice (ADR 0023 has the run). Before
            // that, 2.1.222 was driven the same way on 2026-08-07 and after each change to the file since.
            ProtocolVerifiedVersion: "2.1.280",
            VersionArguments: ["--version"],
            AuthStatusArguments: ["auth", "status"],
            AuthStatusSignedInMarker: "\"loggedIn\": true",
            AuthStatusSignedOutMarker: "\"loggedIn\": false",
            ApiKeyEnvVar: "ANTHROPIC_API_KEY",
            ApiKeyLabel: "API key",
            // A subscription token is NOT an API key and does not work in this field: it is what the sign-in below
            // makes and stores for you.
            ApiKeyHelp: "A Claude Console API key (sk-ant-…). To use your Claude subscription instead, sign in.",
            // Brokerable in v1 (ADR 0010 D3): ANTHROPIC_BASE_URL redirects to the gateway, ANTHROPIC_AUTH_TOKEN
            // carries the per-user bearer as `Authorization: Bearer` (distinct from the org key's ANTHROPIC_API_KEY).
            BrokerBaseUrlEnvVar: "ANTHROPIC_BASE_URL",
            BrokerAuthTokenEnvVar: "ANTHROPIC_AUTH_TOKEN",
            // Backlog 183 (decided 2026-10-01): `setup-token` signs in with the person's subscription and
            // PRINTS a token that lasts about a year and never refreshes. `auth login` stored a refreshable one in
            // the CLI's config home instead, and each conversation's copy of it went stale when any other refreshed.
            InteractiveLoginArguments: ["setup-token"],
            SupportsInteractiveLogin: true,
            SubscriptionTokenEnvVar: "CLAUDE_CODE_OAUTH_TOKEN",
            // Backlog 26 measured the half that is the CLI's: the pinned CLI takes image content blocks on a
            // stream-json user turn (the shape is kept as a fixture). The other half is the model's, and every model
            // the Claude catalog offers reads images.
            SeesImages: true),

        // RETIRED to v2, decided 2026-09-22. See docs/v2.md section 1, "Codex and Copilot runtimes",
        // which carries the decision, everything that was measured, and the read-containment question this
        // parks. Kept rather than deleted because the pins, the auth-status markers and the device-login argv
        // below were each measured against the real binaries in the container, and several of them cannot be
        // re-derived without a signed-in CLI.
        //
        // new(
        //     AgentTool.Codex,
        //     "OpenAI Codex",
        //     "codex",
        //     "@openai/codex",
        //     PinnedVersion: "0.146.0",
        //     // Pinned, but nothing was ever verified about its wire format: the studio drives Codex as a
        //     // one-shot command, not over a protocol. Saying "verified" here would be a claim with nothing
        //     // behind it.
        //     ProtocolVerifiedVersion: null,
        //     VersionArguments: ["--version"],
        //     AuthStatusArguments: ["login", "status"],
        //     AuthStatusSignedInMarker: "logged in",
        //     AuthStatusSignedOutMarker: "not logged in",
        //     ApiKeyEnvVar: "OPENAI_API_KEY",
        //     ApiKeyLabel: "API key",
        //     ApiKeyHelp: "An OpenAI API key (sk-...). Codex also accepts it via `codex login --with-api-key`.",
        //     // Not brokered in v1 (D3): Codex has its own base-URL/proxy story and joins the broker pathway later.
        //     BrokerBaseUrlEnvVar: null,
        //     BrokerAuthTokenEnvVar: null,
        //     // The DEVICE-CODE flow, asked for by name. A bare `codex login` ends with the browser redirected to
        //     // a loopback callback, and this CLI runs inside the container while the browser is on the host, so
        //     // the owner's completed sign-in landed on a connection-refused page (2026-09-20). Device auth has no
        //     // callback: the CLI prints a code and polls. Flag read from 0.146.0's own help in the container.
        //     InteractiveLoginArguments: ["login", "--device-auth"],
        //     SupportsInteractiveLogin: true),
        // new(
        //     AgentTool.Copilot,
        //     "GitHub Copilot",
        //     "copilot",
        //     "@github/copilot",
        //     PinnedVersion: "1.0.78",
        //     // As for Codex: pinned, protocol never verified, so nothing is claimed.
        //     ProtocolVerifiedVersion: null,
        //     VersionArguments: ["--version"],
        //     // Copilot ships no scriptable auth-status/whoami command (verified via `copilot --help`); a stored
        //     // token is the only real proof of headless auth, so without one the state is honestly Unknown.
        //     AuthStatusArguments: null,
        //     AuthStatusSignedInMarker: null,
        //     AuthStatusSignedOutMarker: null,
        //     ApiKeyEnvVar: "COPILOT_GITHUB_TOKEN",
        //     ApiKeyLabel: "GitHub token",
        //     ApiKeyHelp: "A fine-grained PAT with the \"Copilot Requests\" permission, or a Copilot/gh OAuth token (classic ghp_ tokens are not supported).",
        //     // Not brokered in v1 (D3): Copilot joins the broker pathway later on its own proxy story.
        //     BrokerBaseUrlEnvVar: null,
        //     BrokerAuthTokenEnvVar: null,
        //     // As for Codex. 1.0.78 says it picks the device-code flow by itself in a headless environment, and
        //     // it did not recognise this container as one: the owner got the loopback web flow. So it is asked for.
        //     InteractiveLoginArguments: ["login", "--device-code"],
        //     SupportsInteractiveLogin: true),
    ];

    /// <summary>
    /// The descriptor for a tool. A tool the catalog does not carry is a PROGRAMMING error rather than a data
    /// one, so it throws and says which: <see cref="AgentTool"/> keeps its retired values so that a stored
    /// settings row or session row still parses, and everything that reads stored data filters through this
    /// catalog first (<see cref="AgentRuntimeSelection.Read"/> is the one that matters). Reaching here with a
    /// retired tool means something walked past that filter.
    /// </summary>
    public static AgentRuntimeDescriptor For(AgentTool tool) =>
        All.FirstOrDefault(d => d.Tool == tool)
        ?? throw new NotSupportedException(
            $"The runtime '{tool}' is not offered in this version. See docs/v2.md section 1 for the decision "
            + "that retired it. Read stored runtime choices through AgentRuntimeSelection, which filters them.");

    /// <summary>Every supported tool, in display order.</summary>
    public static IReadOnlyList<AgentTool> Tools { get; } = All.Select(d => d.Tool).ToList();
}
