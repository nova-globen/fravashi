using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// Handles the Atlassian 3LO callback out of band from the Blazor circuit: validates the CSRF <c>state</c>,
/// exchanges the code for tokens, stores the credential envelope + connection, records the cloudId (via
/// accessible-resources) for the future probe integration, and reloads configuration so the bridge projects
/// the token. Returns where to send the browser next. The endpoint in <c>Program.cs</c> owns only the HTTP
/// shell; all the logic lives here so it is unit-testable.
/// </summary>
public sealed class AuthCodeCallbackService
{
    private readonly IAuthCodeStateStore _states;
    private readonly AtlassianAuthCodeAuthenticator _atlassian;
    private readonly ISecretStore _secrets;
    private readonly IProviderConnectionStore _connections;
    private readonly IConfigurationStore _settings;
    private readonly IConfigurationReloader _reloader;

    public AuthCodeCallbackService(
        IAuthCodeStateStore states,
        AtlassianAuthCodeAuthenticator atlassian,
        ISecretStore secrets,
        IProviderConnectionStore connections,
        IConfigurationStore settings,
        IConfigurationReloader reloader)
    {
        _states = states;
        _atlassian = atlassian;
        _secrets = secrets;
        _connections = connections;
        _settings = settings;
        _reloader = reloader;
    }

    /// <summary>The result of handling a callback: a relative path to redirect the browser to.</summary>
    public sealed record CallbackResult(string RedirectPath);

    public async Task<CallbackResult> HandleAsync(string? code, string? state, string? error, CancellationToken cancellationToken = default)
    {
        var entry = string.IsNullOrEmpty(state) ? null : _states.Consume(state);

        // Validate the CSRF state first - a forged/replayed state has no entry, and we cannot trust its return
        // target, so send it to the wizard root with a clear reason.
        if (entry is null)
        {
            return new CallbackResult("/onboarding?signin=csrf");
        }

        var back = ReturnPath(entry);

        if (!string.IsNullOrEmpty(error))
        {
            return new CallbackResult($"{back}signin=denied");
        }

        if (string.IsNullOrEmpty(code))
        {
            return new CallbackResult($"{back}signin=error");
        }

        var exchange = await _atlassian.ExchangeCodeAsync(code, entry.RedirectUri, cancellationToken).ConfigureAwait(false);
        if (!exchange.Success || exchange.Credential is null)
        {
            return new CallbackResult($"{back}signin=error");
        }

        // Persist the credential envelope + connection. The 3LO access token is gateway-scoped, so the access
        // probe is NOT run here (its base URL differs - a tracked follow-up); the connection is recorded as
        // signed-in, not probe-verified.
        _secrets.Set(entry.CredentialRef, CredentialEnvelope.Serialize(exchange.Credential));

        var kind = KindFor(entry.Area);
        _connections.Upsert(new ProviderConnection(
            Id: entry.ConnectionId,
            Kind: kind,
            BaseUrl: entry.BaseUrl,
            CredentialRef: entry.CredentialRef,
            Status: "Signed in",
            UpdatedUtc: DateTimeOffset.UtcNow));

        _settings.Set($"Onboarding:{kind}:Provider", entry.Provider);
        _settings.Set(AuthSettings.AuthMethodKey(entry.Area), AuthMethods.AuthCode);
        if (kind == ProviderKind.Board)
        {
            _settings.Set("Tracker:Provider", entry.Provider);
        }

        // Verify the sign-in and capture the cloudId the future probe integration needs.
        var resources = await _atlassian.ListAccessibleResourcesAsync(exchange.Credential.AccessToken, cancellationToken).ConfigureAwait(false);
        var site = resources.FirstOrDefault();
        if (site is { Id.Length: > 0 })
        {
            _settings.Set($"Onboarding:{entry.Area}:CloudId", site.Id);
            _settings.Set($"Onboarding:{entry.Area}:CloudSite", site.Url ?? site.Name ?? site.Id);
        }

        _reloader.Reload();

        var verified = resources.Count > 0 ? "ok" : "ok-unverified";
        return new CallbackResult($"{back}signin={verified}");
    }

    private static ProviderKind KindFor(string area) => area switch
    {
        "source" => ProviderKind.Source,
        "board" => ProviderKind.Board,
        _ => ProviderKind.Wiki,
    };

    private static string ReturnPath(AuthCodeStateEntry entry)
    {
        // A settings-hosted sign-in round-trips back to the Settings sidebar location (/settings/{area}, work item
        // #357), NOT the onboarding wizard route - the step components render there as Settings sections, and the
        // area slug (wiki/board) matches the section slug (an unknown one, such as the retired source, lands on the
        // overview). The first-run wizard keeps its /onboarding/{area} return. Either way the ?signin=… result
        // query is appended by the caller.
        return entry.ReturnMode == "settings"
            ? $"/settings/{entry.Area}?"
            : $"/onboarding/{entry.Area}?";
    }
}
