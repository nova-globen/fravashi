namespace Fravashi.Application.Persistence;

/// <summary>
/// Re-reads configuration from every source, including the SQLite settings table, after onboarding writes
/// new values. This refreshes <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> and any
/// <c>IOptionsMonitor&lt;T&gt;</c>-bound options. It does NOT re-run bindings resolved at DI-registration
/// time - most importantly <c>Tracker:Provider</c>, which selects the tracker adapter once when the
/// service collection is built. Switching provider (or any registration-time binding) requires a full
/// process restart; see <see cref="IApplicationRestarter"/>.
/// </summary>
public interface IConfigurationReloader
{
    void Reload();
}
