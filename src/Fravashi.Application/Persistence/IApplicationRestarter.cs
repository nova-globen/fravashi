namespace Fravashi.Application.Persistence;

/// <summary>
/// Requests a full process restart. Onboarding (S5) calls this after it completes so that
/// registration-time configuration bindings (e.g. the tracker provider selected from
/// <c>Tracker:Provider</c>) take effect - <see cref="IConfigurationReloader"/> alone cannot re-run those.
/// In the Docker deployment the container's restart policy brings the app straight back up on the same
/// port and data volume, so from the user's side this is a brief reload of the browser tab.
/// </summary>
public interface IApplicationRestarter
{
    void Restart();
}
