using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;

namespace Fravashi.Web.Services;

/// <summary>
/// Wizard-scoped working state for onboarding (F13). Registered <b>scoped</b>, so it outlives the individual
/// step components and survives navigating between the per-step routes within one circuit - the forms no
/// longer reset when the user moves Back/Forward through the wizard.
/// <para>
/// Two persistence tiers, by sensitivity:
/// <list type="bullet">
/// <item><b>Non-secret fields</b> (provider selections, URLs, emails, project/space keys) are written through
/// to the settings table under <c>Onboarding:Draft:*</c> via <see cref="Save"/>, so they also survive a
/// browser refresh, a circuit reconnect, and an app restart. They are hydrated back on construction.</item>
/// <item><b>Tokens / PATs</b> are held here for the circuit's lifetime only and are <b>never</b> written to the
/// draft store. On a refresh the token field comes back empty (see <see cref="SourceTokenLost"/> et al.), the
/// UI shows a "re-enter to re-test" hint, and any previously <i>verified</i> result stays visible from the
/// connection record instead (its status + verified-at time).</item>
/// </list>
/// </para>
/// The in-memory probe results live for the circuit only; a refresh clears them, which is why the steps fall
/// back to the persisted connection record to show what was verified and when.
/// </summary>
public sealed class OnboardingDraft
{
    private readonly IConfigurationStore _config;

    /// <summary>Raised when a field the wizard chrome depends on changes (e.g. the data-location gate), so the
    /// URL-aware layout can re-render its Next button across the component boundary.</summary>
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    public OnboardingDraft(IConfigurationStore config, IProviderConnectionStore connections)
    {
        _config = config;
        ArgumentNullException.ThrowIfNull(connections);

        var wiki = connections.Get("wiki");
        var board = connections.Get("board");

        // Hydrate each non-secret field: prefer the live draft key, then the value the step last SAVED
        // (Onboarding:*:Provider + the connection's BaseUrl), then the component default. Tokens are never
        // hydrated: they stay empty until re-entered. (The source/git step retired in plan 2c: host
        // folders are the working copies, and git identity comes from the HOST git config.)
        WikiProvider = Seed("Onboarding:Draft:Wiki:Provider", "Onboarding:Wiki:Provider", OnboardingProviders.Confluence);
        WikiBaseUrl = Seed("Onboarding:Draft:Wiki:BaseUrl", null, wiki?.BaseUrl ?? string.Empty);
        WikiEmail = Seed("Onboarding:Draft:Wiki:Email", "Onboarding:Wiki:Email", string.Empty);
        WikiSpaceKey = Seed("Onboarding:Draft:Wiki:SpaceKey", null, string.Empty);

        BoardProvider = Seed("Onboarding:Draft:Board:Provider", "Onboarding:Board:Provider", OnboardingProviders.AzureDevOps);
        BoardBaseUrl = Seed("Onboarding:Draft:Board:BaseUrl", null, board?.BaseUrl ?? string.Empty);
        // Prefer the live draft, then the committed coordinate the coordinate bridge projects, then empty - so a
        // Settings re-entry shows the values that are actually wired into the adapter, not a blank field.
        BoardProject = Seed("Onboarding:Draft:Board:Project", "Onboarding:Board:Project", string.Empty);
        BoardEmail = Seed("Onboarding:Draft:Board:Email", "Onboarding:Board:Email", string.Empty);
    }

    // ── Wiki ───────────────────────────────────────────────────────────────────────────────────────────────
    public string WikiProvider { get; set; }
    public string WikiBaseUrl { get; set; }
    public string WikiEmail { get; set; }
    public string WikiSpaceKey { get; set; }
    public string WikiToken { get; set; } = string.Empty;
    public AccessProbeResult? WikiResult { get; set; }
    public string? WikiSavedStatus { get; set; }
    public StoredCredential? WikiCredential { get; set; }

    // ── Board ──────────────────────────────────────────────────────────────────────────────────────────────
    public string BoardProvider { get; set; }
    public string BoardBaseUrl { get; set; }
    public string BoardProject { get; set; }
    public string BoardEmail { get; set; }
    public string BoardToken { get; set; } = string.Empty;
    public AccessProbeResult? BoardResult { get; set; }
    public string? BoardSavedStatus { get; set; }
    public StoredCredential? BoardCredential { get; set; }

    /// <summary>
    /// Persists every non-secret draft field to the settings table in one write. Called from each step when a
    /// field changes and when a connection is saved, so a subsequent refresh/restart rehydrates them.
    /// </summary>
    public void Save() => _config.SetMany(new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["Onboarding:Draft:Wiki:Provider"] = WikiProvider,
        ["Onboarding:Draft:Wiki:BaseUrl"] = WikiBaseUrl,
        ["Onboarding:Draft:Wiki:Email"] = WikiEmail,
        ["Onboarding:Draft:Wiki:SpaceKey"] = WikiSpaceKey,
        ["Onboarding:Draft:Board:Provider"] = BoardProvider,
        ["Onboarding:Draft:Board:BaseUrl"] = BoardBaseUrl,
        ["Onboarding:Draft:Board:Project"] = BoardProject,
        ["Onboarding:Draft:Board:Email"] = BoardEmail,
    });

    private string Seed(string draftKey, string? savedKey, string fallback)
    {
        var draft = _config.Get(draftKey);
        if (!string.IsNullOrEmpty(draft))
        {
            return draft;
        }

        if (savedKey is not null)
        {
            var saved = _config.Get(savedKey);
            if (!string.IsNullOrEmpty(saved))
            {
                return saved;
            }
        }

        return fallback;
    }
}
