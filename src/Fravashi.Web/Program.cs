using Fravashi.Application.Demo;
using Fravashi.Application.Projects;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Infrastructure.AgentRuntimes;
using Fravashi.Infrastructure.Autopilot;
using Fravashi.Infrastructure.Backup;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Framework;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.PullRequests;
using Fravashi.Infrastructure.Repository;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Integrity;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Infrastructure.Onboarding;
using Fravashi.Infrastructure.Onboarding.Auth;
using Fravashi.Infrastructure.PostOnboarding;
using Fravashi.Infrastructure.Evidence;
using Fravashi.Infrastructure.Telemetry;
using Fravashi.Infrastructure.Tracker;
using Fravashi.Infrastructure.Updates;
using Fravashi.Infrastructure.Variants;
using Fravashi.Application.Updates;
using Fravashi.Application.Variants;
using Fravashi.Application.Vpn;
using Fravashi.Application.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Persistence.Configuration;
using Fravashi.Web;
using Fravashi.Web.Components;
using Fravashi.Web.Services;
using Microsoft.FluentUI.AspNetCore.Components;

// Harden against a same-uid /proc read of the studio's own environment BEFORE anything else runs
// (backlog 90). The agent CLIs run at this container's uid and could otherwise read the bridge token,
// window token and integrity key out of /proc/1/environ. No-op off Linux.
Fravashi.Infrastructure.Integrity.ProcessHardening.MakeNonDumpable();

var builder = WebApplication.CreateBuilder(args);

// --- Persistence + config-in-DB (ADR 0003 V6). ---
// The data directory (FRAVASHI_DATA_DIR, the mounted volume in Docker) holds the SQLite database and the
// Data Protection key ring. Adding the database as a configuration source AFTER appsettings/user-secrets
// lets onboarding-written settings override the file defaults, while every existing Configure<T> /
// configuration["..."] binding keeps working unchanged - the adapters never learn their values now come
// from SQLite. The provider bootstraps the schema on first read, so a fresh volume needs no ceremony.
var dataDirectory = DataDirectory.FromEnvironment(builder.Configuration);

// Every app's log stream lands in <data dir>/logs (directive 2026-07-26); the studio writes
// studio-yyyy-MM-dd.log there alongside the launcher's and window's files.
builder.Logging.AddProvider(new Fravashi.Infrastructure.Logging.FileLoggerProvider(
    Path.Combine(dataDirectory.Path, "logs"), "studio"));

// Vendor-registered OAuth client ids (ADR 0004): Nova Globen registers one app per identity provider for the
// whole product and bakes the (non-secret) client ids into VendorAppDefaults. This source sits BELOW the
// SQLite settings source so a per-install advanced override still wins; an unregistered provider contributes
// no key, keeping the wizard's "sign-in not available in this build yet" state honest.
builder.Configuration.AddFravashiVendorDefaults();
builder.Configuration.AddFravashiDatabase(dataDirectory.DatabasePath);

// Secret→adapter bridge (S6): onboarding stores provider tokens only in the secret store, but the inherited
// adapters read their token from IConfiguration (Atlassian:JiraKey, the only key anything binds today). This
// source projects each stored credential into the pipeline under the adapter's key. Its
// stores are wired after the container is built (below), because the secret store needs Data Protection.
var secretBridge = builder.Configuration.AddFravashiSecretBridge();

// Non-secret coordinate→adapter bridge (coordinate projection): the token bridge above makes a connection
// credentialled, but the adapters also need the connection's NON-secret coordinates - Jira
// Atlassian:JiraDomain/JiraEmail/JiraProject, Azure DevOps AzureDevOps:Organization/Project. This source
// derives those from the connection row + the committed Onboarding:{area}:* settings and projects them under
// the adapter keys, so a verified tracker is fully live after onboarding, not merely credentialled. Its
// stores are wired after the container is built (below), paired with the secret bridge.
var coordinateBridge = builder.Configuration.AddFravashiCoordinateBridge();

// Credentials at rest (PROVISIONAL - see decision-log): persist Data Protection keys beside the database
// on the data volume, so token encryption works inside a Linux container without DPAPI / an OS keychain.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataDirectory.KeysPath))
    .SetApplicationName("Fravashi");

// Backlog 71: a DEMO install runs on a stopped clock, so every timestamp and every rendered age is the same
// on two fresh installs and a screenshot is stable. This must be registered BEFORE the extensions below,
// because each of those adds the system clock with TryAddSingleton, which keeps whatever is already there.
if (DemoMode.IsEnabled)
{
    builder.Services.AddSingleton(DemoMode.Clock);
}

builder.Services.AddFravashiPersistence(dataDirectory, builder.Configuration["Persistence:VectorExtensionPath"]);

// Backlog 226: a disposable verification run the app runner started, honoured only when its flag matches this start's
// window token (VerificationRun says exactly when). When honoured, onboarding reads as complete for this process and
// the sign-in gate below does not require a session; nothing is written and no other gate consults it.
var verificationRun = VerificationRun.FromProcess(builder.Configuration);
verificationRun.ApplyTo(builder.Services);
// Backlog 282: the sign-in gate's condition (Services/StudioGates.cs) reads it from services, because the circuit-side
// guard asks that same condition and has no access to this local.
builder.Services.AddSingleton(verificationRun);
// Backlog 283: what is a static asset, asked by the gates (middleware and circuit alike), the Not found re-execution and
// the Error page, from the endpoints MapStaticAssets maps below, never from the shape of a path.
builder.Services.AddSingleton(services => new StaticAssetRoutes(services.GetRequiredService<EndpointDataSource>()));
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddSingleton<IApplicationRestarter, ApplicationRestarter>();

// Onboarding access probes (S5): real, honest read/query checks against the user's chosen providers, run
// only on explicit action inside the wizard. No automated test hits a live external service.
builder.Services.AddOnboardingProbes();

// Provider sign-in (SSO/OIDC, owner feedback F6): device-code (GitHub, Entra/Azure DevOps) and
// authorization-code / 3LO (Atlassian) flows, plus the token refresher. The PAT path is unchanged and
// remains the always-available fallback. Client ids / secrets are per-installation settings (see Sign-in
// configuration in Settings); without them the wizard's sign-in buttons are honestly disabled.
builder.Services.AddProviderAuth();

// AI coding-agent runtimes (N1 - N3, S10 - S16): CLI status/version/auth checks, the headless-credential store,
// update-on-launch (container-only), the interactive login proxy, and the board-mapping suggester.
builder.Services.AddAgentRuntimes(builder.Configuration);

// Framework artifacts (ADR 0005): the model-list probe + probe-then-catalog resolver (D03-12). The DB-backed
// IFrameworkArtifactStore is registered with the persistence layer above.
builder.Services.AddFrameworkArtifacts();

// Blazor Server with interactive server rendering - the studio shell streams agent events over SignalR. A message
// from the browser may be as large as CircuitLimits says (backlog 176): a long description's every keystroke is one.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options => options.MaximumReceiveMessageSize = CircuitLimits.MaximumReceiveMessageBytes);

builder.Services.AddFluentUIComponents();

// The FluentUI icon catalog for the skill icon picker - enumerated once from the installed Icons package.
builder.Services.AddSingleton<FluentIconCatalog>();

// Per-circuit theme state (light/dark/system); bound by FluentDesignTheme, persisted to localStorage.
builder.Services.AddScoped<ThemeState>();

// Backlog 246: the person's time zone for this circuit, read from the browser once; every time on a page uses it.
builder.Services.AddScoped<Fravashi.Web.Services.PersonTimeZone>();

// Per-circuit agent-panel state (backlog 23): the Code page draws the panel, the window title bar
// carries the control that toggles it, and neither contains the other.
builder.Services.AddScoped<AgentPanelState>();
// Backlog 67: whether the scratchpad is showing, shared by its toggle (title bar) and its panel (layout).
builder.Services.AddScoped<ScratchpadState>();
// Work item #350: whether the start dialog is open over a window that has a folder, shared by its opener (the title
// bar's workspace button) and its host (the layout). A window with no folder shows the dialog without asking this.
builder.Services.AddScoped<StartDialogState>();
// Item #373: the setup's import scan for this window; it runs off the circuit and outlives a step, not the window.
builder.Services.AddScoped<Fravashi.Web.Services.SetupScan>();

// Wizard-scoped onboarding draft (F13): outlives the per-step routes so forms survive Back/Forward within a
// circuit; non-secret fields are written through to the settings table, tokens stay in the circuit only.
builder.Services.AddScoped<OnboardingDraft>();


// Confluence reader for wiki sync (binds the shared Atlassian config section).
builder.Services.AddTrackerIntegration(builder.Configuration);

// Out-of-band notifications (Telegram).
builder.Services.AddNotifications(builder.Configuration);

// Update notifications (F1 / ADR 0006): the in-container CLI-update card (Type A) + the app-side reader of the
// launcher-written product-update state (Type B) + the single source of truth for the running version/variant.
builder.Services.AddUpdateNotifications();

// Container hardening posture + release-variant feature switches + the VPN-required gate seam (F4 / I1 / F5,
// ADR 0007/0008). Reads the launcher-written sandbox-posture.json to display the honest applied hardening, resolves
// the compiled/image variant switches (GitHub SSO on/off, VPN required), and exposes the VPN connectivity seam the
// startup gate consults. After AddUpdateNotifications (needs IProductInfo) and persistence (needs DataDirectory).
builder.Services.AddSandboxHardening(builder.Configuration);

// Local product events + the diagnostic bundle (backlog 74). Opt-in, off by default, recorded only in
// this install's own database; there is NO remote destination anywhere behind this. Registered after
// AddUpdateNotifications (IProductInfo) and AddSandboxHardening (ISandboxPosture), because the bundle
// reports what both of them know. It replaces the removed StudioTelemetry OpenTelemetry meter, whose
// counters pointed at an Aspire dashboard this product no longer has.
builder.Services.AddProductTelemetry();

// Backup and export (backlog 79). The studio owns backup because it owns the OPEN database: the copy is
// taken through SQLite's online backup API while the app keeps running with WAL active. Restore is
// deliberately NOT here - it needs the studio stopped, so it is the launcher's `fravashi restore` verb.
// After persistence (connection factory + provider store) and AddUpdateNotifications (IProductInfo).
builder.Services.AddStudioBackup();

// Tamper-evidence (F3 / ADR 0007): the fail-closed integrity BackgroundService verifies the running shape against
// the launcher-recorded expected-shape record (digest, env, mounts, non-root uid, port) and, in-container +
// non-Development + provisioned, terminates on a genuine mismatch. Outside a container / in Development / on an
// unprovisioned container it is report-only (do-no-harm). After persistence (the record reader needs DataDirectory).
builder.Services.AddIntegrityChecks();

// Post-onboarding work (F2 / ADR 0005): clone the source repo into the working-copy volume + initial wiki sync
// into the DB-backed local store (site-authoritative), each with a dashboard progress card + an on-start trigger.
builder.Services.AddPostOnboardingSync();

// The Code page (docs/plans/code-and-git-editor.md): the working-tree reader the editor's file tree and
// file loads go through, the git CLI service (ref-addressed reads, later phases add working-tree ops),
// and the material-icon-theme catalog mapping tree entries to their file-type icons.
builder.Services.AddRepositoryExplorer(builder.Configuration);
builder.Services.AddGitService();
// Backlog 298 (ADR 0031 decisions 1 to 3): builds and saves a run from a project's marked Ready items.
// Scoped because it holds the circuit-scoped IAutopilotDeliveryReadiness that AddGitService() just
// registered; IAutopilotWorkspaceStatus (backlog 310) is a singleton, registered by AddAutopilotEngine()
// below, shared with IAutopilotWorkspaceReturn so the dispatch's dirty check and the eventual return
// resolve the project's workspace through the identical lookup.
builder.Services.AddScoped<Fravashi.Application.Autopilot.AutopilotDispatcher>();
// Work item #368: a release's tag goes through the window's own git seams, and the dialog shows the commit's CI state.
builder.Services.AddScoped(sp => new Fravashi.Application.Releases.ReleaseTagger(
    sp.GetRequiredService<Fravashi.Application.Git.IGitService>(),
    sp.GetRequiredService<Fravashi.Application.Git.IGitRemoteService>()));
builder.Services.AddScoped<Fravashi.Application.Releases.ICommitChecks>(sp => new Fravashi.Infrastructure.Ci.ForgeCommitChecks(
    sp.GetRequiredService<Fravashi.Application.Git.IGitService>(),
    sp.GetRequiredService<Fravashi.Infrastructure.Ci.ForgeRuns>()));
// Host-bridge workspaces (plan 2b-i): the bridge arms behind the repository/git seams, selected
// per circuit when Workspace:ActiveBridgeId points at a registered host folder.
builder.Services.AddBridgeWorkspaces();
// Plan 2b-ii: the circuit's workspace comes from the window's ?ws= query, captured once when the
// scoped service materializes (SPA navigations never re-create it). The prerender scope reads the
// HTTP request; the interactive circuit reads its NavigationManager, because the circuit scope's
// HttpContext is the /_blazor WebSocket request and never carries the page query (field defect
// 2026-08-01). Overrides the library's uncaptured default registration.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(WorkspaceSelectionCapture.Create);
// Agent sessions (plan 2e): the approval broker the studio answers the CLI's PreToolUse hook
// through, and the writer that lays down each session's hook files on the data volume.
builder.Services.AddAgentSessions(builder.Configuration);
// Backlog 309 (ADR 0031 decisions 10, 11 and 13): the autopilot's real building session, its commit reviews,
// and the host that ticks every open run. Backlog 301 (decisions 3 to 5): the real delivery (push, pull
// request, CI, scenario, evidence, In Review). After AddAgentSessions (needs LiveSessionRegistry, the
// approval broker and the already-registered operator) and AddGitService/AddRepositoryExplorer (needs the
// autopilot run store, the project-to-workspace lookup, SessionGit and the GitHostAccess factory).
builder.Services.AddAutopilotEngine();

// Backlog 75: every evidence class the studio keeps, with its size and policy, deletable from Settings >
// Environment > Evidence. After AddAgentSessions: it asks the live registry whether a session is running.
builder.Services.AddEvidenceLifecycle();
builder.Services.AddPullRequests();
builder.Services.AddSingleton<FileIconCatalog>();
builder.Services.AddScoped<CodeWorkspace>();
// Backlog 264: one instance per circuit, both the circuit's handler and what the Code page injects (see the class).
builder.Services.AddScoped<CircuitConnection>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler>(
    sp => sp.GetRequiredService<CircuitConnection>());
builder.Services.AddSingleton<Fravashi.Infrastructure.Rendering.MarkdownPreviewRenderer>();

var app = builder.Build();
verificationRun.Log(app.Logger);

// The bridge release (docs/plans/rename-internal-names.md, phase 2): every stored secret protected under the Data
// Protection name from before the internal rename is protected again under the current one, once, before anything below
// reads a secret (the secret bridge is attached next). A value that reads under neither name is left as it is and logged.
Fravashi.Web.Services.FormerKeyRingCarryOver.Run(
    app.Services.GetRequiredService<Fravashi.Infrastructure.Persistence.ISqliteConnectionFactory>(),
    app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(DataProtectionSecretProtector.Purpose),
    Fravashi.Web.Services.FormerKeyRingCarryOver.FormerProtector(dataDirectory.KeysPath),
    line => app.Logger.LogWarning("{Line}", line));

// Wire the secret→adapter bridge now the container exists, then reload so any token onboarding already
// stored surfaces at its adapter key. On a fresh install there is nothing yet; onboarding's completion
// reload re-runs the bridge once a token is entered.
secretBridge.Provider.Attach(
    app.Services.GetRequiredService<IProviderConnectionStore>(),
    app.Services.GetRequiredService<ISecretStore>(),
    app.Services.GetRequiredService<IConfigurationStore>());

// Wire the non-secret coordinate bridge the same way, so a connection's domain/org/project surface at the
// adapter keys. It reads no secret store (coordinates are non-secret) - only the connection + settings stores.
coordinateBridge.Provider.Attach(
    app.Services.GetRequiredService<IProviderConnectionStore>(),
    app.Services.GetRequiredService<IConfigurationStore>());

// Backlog 287: a Content-Security-Policy on every response, outermost so the error, Not found and refusal answers
// carry it too. See StudioContentSecurityPolicy for each directive's reason.
app.UseStudioContentSecurityPolicy();

if (!app.Environment.IsDevelopment())
{
    // Backlog 281: a page request whose handler throws is re-executed to its own Error page inside the shell,
    // keeping its 500; an API (or other machine) request, told apart by its ORIGINAL path, keeps a plain 500 with
    // no body instead (StudioError.ShowsPage). Wired ahead of UseStudioNotFound and every gate below, same as
    // that re-execution, so a re-executed page request walks each gate again in its order.
    app.UseStudioError();
    app.UseHsts();
}
else
{
    // Backlog 281: UseStudioError's exception handler is skipped in Development (the framework's own developer
    // exception page answers first), but the five gates below exempt /Error UNCONDITIONALLY, so the direct-
    // request guard still has to run here too, or a direct GET of /Error on a dev host would pass every closed
    // gate and reach the live page with no exception in sight.
    app.UseStudioErrorGuard();
}

// Backlog 280: a GET for a page the studio does not route is re-executed to its own Not found page inside the shell
// (a bare 404 shows Chromium's error page, with no title bar in the window). Wired ahead of UseAntiforgery and every
// gate below, so the re-executed request walks each gate again in its order; API, framework, liveness and static
// paths keep their plain 404 (StudioNotFound.ShowsPage).
app.UseStudioNotFound();

app.MapStaticAssets();
app.UseAntiforgery();

// Liveness probe for the container/launcher (the old Aspire health endpoints are gone). 200 when the app
// is up and the database opens; 503 otherwise. The schema version rides along (backlog 79): after
// `fravashi restore` the launcher has only the version the manifest CLAIMED, and the version the running
// studio opened the restored database at is the one that proves the product came back up on it.
app.MapGet("/healthz", (ISqliteConnectionFactory connections) =>
{
    try
    {
        using var connection = connections.Open();
        return Results.Ok(new { status = "ok", schemaVersion = SchemaMigrator.StoredVersion(connection) });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unhealthy", error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// First-run detection, read straight from the database (ADR 0003 V4/V5). S5's wizard route builds on this;
// for now it is the flag the container and the scenario gate observe. A fresh volume reports incomplete.
app.MapGet("/api/onboarding/status", (IOnboardingState onboarding) =>
{
    var status = onboarding.Get();
    return Results.Ok(new
    {
        onboardingComplete = status.IsComplete,
        currentStep = status.CurrentStep,
    });
});

// Atlassian 3LO callback (owner feedback F6). The IdP redirects the browser here with ?code&state (or ?error).
// The handler validates the CSRF state, exchanges the code, stores the credential, and redirects back to the
// wizard/settings. It is registered as a localhost redirect URI in the Atlassian app; it is exempt from the
// first-run onboarding gate so it works mid-onboarding.
app.MapGet("/auth/callback/atlassian", async (HttpContext context, AuthCodeCallbackService callback, CancellationToken cancellationToken) =>
{
    var query = context.Request.Query;
    var result = await callback.HandleAsync(query["code"], query["state"], query["error"], cancellationToken);
    return Results.Redirect(result.RedirectPath);
});

// The owner-IdP callback (plan Phase 3): the sign-in gate's authorization-code flow lands here.
// Success opens the gate (redirect to the studio); failure returns to the sign-in page with the
// honest reason. Exempt from every gate via the shared /auth/callback prefix.
app.MapGet("/auth/callback/idp", async (
    HttpContext context,
    Fravashi.Infrastructure.Onboarding.Auth.IdpAuthService idp,
    CancellationToken cancellationToken) =>
{
    var query = context.Request.Query;
    // #372: the caller is the window's user agent (it carries the launcher's window token), so a callback that arrives
    // twice is answered with the first one's outcome only when it comes from the same window with the same code.
    var result = await idp.HandleCallbackAsync(
        query["code"], query["state"], query["error"], context.Request.Headers.UserAgent.ToString(), cancellationToken);
    return Results.Redirect(result.Success
        ? "/"
        : $"{SignInGate.GatePath}?error={Uri.EscapeDataString(result.Detail)}");
});

// Framework resource bytes (uploaded skill icons / resource files, ADR 0005), window-gated (backlog 284).
// See FrameworkResourceEndpoint for the reasoning and the test.
app.MapFrameworkResources();

// Agent tool approvals (plan 2e Task 3). The CLI's PreToolUse hook POSTs the pending tool call here and
// BLOCKS on the answer, which is the user clicking Approve or Deny in the studio (read-only tools are
// answered immediately by the broker's policy). Under /api, so it is exempt from every studio gate by
// design: the per-session secret in the X-Fravashi-Session header is the authentication, and any caller
// without it is refused with a DENIAL, never an allow.
app.MapPost(Fravashi.Infrastructure.Sessions.AgentApprovalEndpoint.Route, async (
    HttpContext context,
    Fravashi.Application.Sessions.IAgentApprovalBroker broker,
    ILoggerFactory loggers,
    CancellationToken cancellationToken) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync(cancellationToken);
    var secret = context.Request.Headers[
        Fravashi.Infrastructure.Sessions.AgentApprovalEndpoint.SecretHeader].ToString();

    var response = await Fravashi.Infrastructure.Sessions.AgentApprovalEndpoint
        .HandleAsync(secret, body, broker, cancellationToken);

    // Backlog 117: a secret that belongs to no session has no transcript to be recorded in, so the refusal
    // is logged instead of being invisible. The secret itself is never written anywhere.
    if (response.StatusCode == StatusCodes.Status401Unauthorized)
    {
        loggers.CreateLogger("Fravashi.AgentApproval").LogWarning(
            "Refused an agent tool approval call whose session secret belongs to no session.");
    }

    context.Response.StatusCode = response.StatusCode;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(response.Json, cancellationToken);
});

// The studio's tool server (ADR 0019, backlog 84): agent sessions reach it over MCP's Streamable HTTP transport
// through the MCP config written beside their hook script. Under /api for the same reason as the approval route:
// the per-session secret header is the authentication, and the server refuses anything without one. GET and
// DELETE are mapped so they get the protocol's 405 rather than the studio's HTML fallback.
app.MapMethods(Fravashi.Infrastructure.Sessions.Tools.StudioToolServer.Route, ["POST", "GET", "DELETE"], async (
    HttpContext context,
    Fravashi.Infrastructure.Sessions.Tools.StudioToolServer toolServer,
    CancellationToken cancellationToken) =>
{
    var limit = Fravashi.Infrastructure.Sessions.Tools.StudioToolServer.MaxRequestBytes;
    if (context.Request.ContentLength > limit)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return;
    }

    // Read at most one byte past the ceiling, so an unannounced oversized body is refused without being buffered.
    var buffer = new char[limit + 1];
    using var reader = new StreamReader(context.Request.Body);
    var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
    var body = new string(buffer, 0, read);

    var response = await toolServer.HandleAsync(
        new Fravashi.Infrastructure.Sessions.Tools.StudioToolHttpRequest(
            context.Request.Method,
            context.Request.Headers[Fravashi.Infrastructure.Sessions.Tools.StudioToolServer.SecretHeader].ToString(),
            context.Request.Headers[Fravashi.Infrastructure.Sessions.Tools.StudioToolServer.ProtocolVersionHeader].ToString(),
            context.Request.Headers.Origin.ToString(),
            body),
        cancellationToken);

    context.Response.StatusCode = response.StatusCode;
    if (response.Allow is { } allow)
    {
        context.Response.Headers.Allow = allow;
    }

    if (response.Json is { } json)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json, cancellationToken);
    }
});

// Backlog 71: a demo install seeds itself, once, into an empty database, and marks onboarding complete so
// the studio opens instead of the wizard. The seed refuses an install that already holds work items, so
// starting a demo container twice is safe and does not double every board column. It writes through the
// same stores a user's own actions go through, never SQL.
if (DemoMode.IsEnabled)
{
    var seed = new DemoSeed(
        app.Services.GetRequiredService<ITrackerResolver>(),
        app.Services.GetRequiredService<IWikiLibraries>(),
        app.Services.GetRequiredService<IOnboardingState>(),
        app.Services.GetRequiredService<TimeProvider>());
    // DefaultProject.Id, not the constant: the default project is decided in exactly one place and
    // DefaultProjectIsDecidedInOnePlaceTests fails the build for naming it anywhere else. It caught this
    // line, which is the guard doing its job.
    var seeded = await seed.SeedAsync(DefaultProject.Id);
    app.Logger.LogInformation(
        seeded.Seeded
            ? "Demo install seeded: {Items} work item(s), {Documents} document(s)."
            : "Demo install already holds data; nothing was seeded.",
        seeded.WorkItems,
        seeded.Documents);
}

// Backlog 84: a studio tool call the previous process left running is resolved as interrupted, and its
// conversation says so, before any new session can reach the tool server. This has to run here,
// synchronously, before app.Run(): it starts no CLI and only touches the ledger and the store, but
// backlog 84 needs it done before ANY session - not only a resumed one - can reach the tool server, and
// Kestrel has not bound yet at this point, so nothing can reach it regardless (backlog 205).
app.Services.GetRequiredService<Fravashi.Infrastructure.Sessions.LiveSessionRegistry>().RecoverInterruptedToolCalls();

// Backlog 156: a conversation the previous process left mid-turn (an upgrade replaced the container, or it crashed) is
// resumed and told what happened, after the interrupted tool calls above have been recorded on it (that recovery
// already ran, synchronously, just above). Backlog 205: unlike the recovery above, resuming STARTS a CLI child,
// whose first call to the tool server would race Kestrel's bind if it ran here too - so this one is registered to
// run from ApplicationStarted instead, which fires only once the host has started every hosted service, including
// the one that binds and starts the server.
Fravashi.Web.Services.StudioStartupResume.RunAfterStart(
    app.Lifetime, app.Services.GetRequiredService<Fravashi.Infrastructure.Sessions.LiveSessionRegistry>());

// Window-shell-only access (ADR 0017): the studio is NOT a website; only the studio window shell may
// browse it (ruling 2026-07-26). Outermost gate, above even the integrity lock: a plain
// browser gets a static refusal page for every request, including lock pages and framework assets
// (the page is inline HTML precisely so it needs none of them). Machine endpoints (/healthz, /api,
// the OAuth callback) stay open. The studio window brands its user agent with the marker plus, when
// the launcher minted one, the per-install token the container received as FRAVASHI_WINDOW_TOKEN.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "/";
    if (!Fravashi.Web.Services.WindowOnlyGate.IsExempt(path)
        && !Fravashi.Web.Services.WindowOnlyGate.IsAllowed(
            context.Request.Headers.UserAgent.ToString(),
            app.Configuration[Fravashi.Web.Services.WindowOnlyGate.TokenConfigKey]))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(Fravashi.Web.Services.WindowOnlyGate.BlockedHtml);
        return;
    }

    await next();
});

// The five redirecting full-page gates, most authoritative first: the integrity lock, the critical-update lock, the
// VPN gate, the sign-in gate and the first-run gate. Their order, their closed conditions and which page each sends a
// person to live in Services/StudioGates.cs, and each one's exemptions in its own static policy class (IntegrityLock,
// CriticalUpdateLock, VpnGate, SignInGate, OnboardingGate). Backlog 282: they live there and not inline here because a
// SECOND place asks the same decision: Components/GateGuard.razor, for every in-app navigation inside a live circuit,
// which makes no HTTP request and so never reached this middleware. Neither asker holds a copy of any rule.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "/";
    if (await StudioGates.RedirectAsync(path, context.RequestServices, context.RequestAborted) is { } target)
    {
        context.Response.Redirect(target);
        return;
    }

    await next();
});

// Backlog 287: Blazor's own frame-ancestors header would otherwise win over the studio's whole policy.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode(StudioContentSecurityPolicy.LeaveFrameAncestorsToTheStudio);

app.Run();

namespace Fravashi.Web
{
    /// <summary>Exposed so the Web.Tests host can reference the application entry point.</summary>
    public partial class Program;
}
