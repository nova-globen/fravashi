using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Application.Variants;
using Fravashi.Infrastructure.AgentRuntimes.Broker;
using Fravashi.Infrastructure.Onboarding.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fravashi.Web.Tests;

/// <summary>
/// Registers the provider sign-in (SSO/OIDC) services the onboarding steps now inject. The steps read
/// <see cref="IAuthConfigStore"/> on init (SSO is "not configured" with the test's empty settings, so the
/// PAT fields still render); the sign-in authenticators/starter are injected but only used on a click.
/// </summary>
internal static class ProviderSignInTestServices
{
    public static IServiceCollection AddProviderSignInForTests(this IServiceCollection services)
    {
        services.AddProviderAuth();
        services.TryAddSingleton<IConfigurationReloader>(new FakeConfigurationReloader());
        // I1: ProviderSignIn injects IFeatureFlags to honour the GITHUB_LOGIN variant switch. Default to the
        // full variant (everything on); a test can register a locked-down FakeFeatureFlags before this to override.
        services.TryAddSingleton<IFeatureFlags>(new FakeFeatureFlags("full", FeatureFlags.Full));

        // Remote credential broker (plan 04 B3): the Settings broker card injects these. Default to the disabled
        // (single-user) posture so the card renders its honest "not enabled" state; a broker-specific test can
        // register an active BrokerOptions + a stub IBrokerTokenService before this to exercise the active path.
        services.TryAddSingleton(BrokerOptions.Disabled);
        services.TryAddSingleton(TimeProvider.System);
        // Backlog 269: the card follows the process's one health record, as AddAgentRuntimes registers it.
        services.TryAddSingleton(sp => new BrokerTokenHealth(sp.GetRequiredService<TimeProvider>()));
        services.AddHttpClient<IBrokerTokenService, BrokerTokenService>();
        return services;
    }
}

/// <summary>A fixed <see cref="IFeatureFlags"/> for component tests.</summary>
internal sealed record FakeFeatureFlags(string Variant, FeatureFlags Flags) : IFeatureFlags;

/// <summary>In-memory <see cref="IConfigurationStore"/> for onboarding component tests.</summary>
internal sealed class FakeConfigurationStore : IConfigurationStore
{
    public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
    public IReadOnlyDictionary<string, string?> GetAll() => Values;
    public void Set(string key, string? value) => Values[key] = value;
    public void SetMany(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (k, v) in values) Values[k] = v;
    }

    public void Remove(string key) => Values.Remove(key);
}

/// <summary>
/// An <see cref="IAuthConfigStore"/> that reports one fixed provider configuration, so a test can render the
/// honest "not available in this build yet" disabled state without depending on which providers happen to ship
/// a vendor default. Both device-code providers now ship a baked-in client id (ADR 0004), so the unconfigured
/// state is modelled here - a device-code config with the client id blanked to null - rather than by picking an
/// unregistered provider.
/// </summary>
internal sealed class FakeAuthConfigStore(ProviderAuthConfig config) : IAuthConfigStore
{
    public AuthStrategy StrategyFor(string provider) => config.Strategy;
    public ProviderAuthConfig Get(string provider) => config;
}

/// <summary>In-memory <see cref="IProviderConnectionStore"/> for onboarding component tests.</summary>
internal sealed class FakeProviderConnectionStore : IProviderConnectionStore
{
    public Dictionary<string, ProviderConnection> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ProviderConnection> GetAll() => Items.Values.ToList();
    public IReadOnlyList<ProviderConnection> GetByKind(ProviderKind kind) => Items.Values.Where(c => c.Kind == kind).ToList();
    public ProviderConnection? Get(string id) => Items.TryGetValue(id, out var c) ? c : null;
    public void Upsert(ProviderConnection connection) => Items[connection.Id] = connection;
    public void Remove(string id) => Items.Remove(id);
}

/// <summary>In-memory <see cref="ISecretStore"/> for onboarding component tests.</summary>
internal sealed class FakeSecretStore : ISecretStore
{
    public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

    public void Set(string reference, string plaintext) => Secrets[reference] = plaintext;
    public string? Get(string reference) => Secrets.TryGetValue(reference, out var v) ? v : null;
    public void Remove(string reference) => Secrets.Remove(reference);
}

/// <summary>Configurable <see cref="IOnboardingState"/> for onboarding component tests.</summary>
internal sealed class FakeOnboardingState : IOnboardingState
{
    private bool _complete;
    public string? CurrentStep { get; private set; }
    public bool MarkCompleteCalled { get; private set; }

    public FakeOnboardingState(bool complete = false, string? currentStep = null)
    {
        _complete = complete;
        CurrentStep = currentStep;
    }

    public bool IsComplete => _complete;
    public OnboardingStatus Get() => new(_complete, CurrentStep, null, null);
    public void SetCurrentStep(string step) => CurrentStep = step;
    public void MarkComplete() { _complete = true; MarkCompleteCalled = true; }
    public void Reset() { _complete = false; }
}

internal sealed class FakeConfigurationReloader : IConfigurationReloader
{
    public int ReloadCount { get; private set; }
    public void Reload() => ReloadCount++;
}

internal sealed class FakeApplicationRestarter : IApplicationRestarter
{
    public int RestartCount { get; private set; }
    public void Restart() => RestartCount++;
}

/// <summary>Per-git-host credential entries (plan 2c), dictionary-backed.</summary>
internal sealed class FakeGitHostStore : Fravashi.Application.Persistence.IGitHostStore
{
    public Dictionary<string, Fravashi.Application.Persistence.GitHostRecord> Map { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Fravashi.Application.Persistence.GitHostRecord> GetAll() =>
        Map.Values.OrderBy(r => r.Host).ToList();

    public Fravashi.Application.Persistence.GitHostRecord? Get(string host) => Map.GetValueOrDefault(host);

    public void Upsert(Fravashi.Application.Persistence.GitHostRecord record) => Map[record.Host] = record;

    public void Remove(string host) => Map.Remove(host);
}

internal sealed class FakeWikiAccessProbe(AccessProbeResult result) : IWikiAccessProbe
{
    public WikiAccessRequest? LastRequest { get; private set; }

    public Task<AccessProbeResult> ProbeAsync(WikiAccessRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(result);
    }
}

internal sealed class FakeBoardAccessProbe(AccessProbeResult result) : IBoardAccessProbe
{
    public BoardAccessRequest? LastRequest { get; private set; }

    public Task<AccessProbeResult> ProbeAsync(BoardAccessRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(result);
    }
}

/// <summary>A board-states probe that returns a preset result and captures the request it was given.</summary>
internal sealed class FakeBoardStatesProbe(BoardStatesResult result) : IBoardStatesProbe
{
    public BoardAccessRequest? LastRequest { get; private set; }

    public Task<BoardStatesResult> FetchAsync(BoardAccessRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(result);
    }
}

/// <summary>Canned probe results covering each honesty outcome.</summary>
internal static class ProbeResults
{
    public static AccessProbeResult Verified => new(
    [
        new AccessCheck("Read", AccessProbeOutcome.Passed, "git ls-remote", "reachable"),
        new AccessCheck("Write", AccessProbeOutcome.NotVerifiable, "no non-destructive push probe", "recorded, not verified"),
    ]);

    public static AccessProbeResult Failed => new(
    [
        new AccessCheck("Read", AccessProbeOutcome.Failed, "git ls-remote", "authentication failed"),
    ]);

    public static AccessProbeResult NotVerifiableOnly => new(
    [
        new AccessCheck("Read", AccessProbeOutcome.Passed, "Jira GET /rest/api/3/myself", "ok"),
        new AccessCheck("Query", AccessProbeOutcome.Passed, "Jira POST /rest/api/3/search/jql", "ran"),
        new AccessCheck("Write", AccessProbeOutcome.NotVerifiable, "Jira issue round-trip (needs a project key)", "Not verified - provide a project key to run the write test."),
        new AccessCheck("Modify", AccessProbeOutcome.NotVerifiable, "Jira issue round-trip (needs a project key)", "Not verified - provide a project key to run the modify test."),
        new AccessCheck("Status change", AccessProbeOutcome.NotVerifiable, "Jira issue round-trip (needs a project key)", "Not verified - provide a project key to run the status-change test."),
    ]);
}
