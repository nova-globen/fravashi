using Fravashi.Application.Persistence;

namespace Fravashi.Web.Services;

/// <summary>
/// <see cref="IApplicationRestarter"/> that stops the host. In the Docker deployment the container runs
/// with a restart policy, so stopping the process brings the app straight back up on the same port and
/// data volume - the mechanism onboarding (S5) uses to apply registration-time configuration (e.g. a
/// changed <c>Tracker:Provider</c>) that <see cref="IConfigurationReloader"/> cannot re-run. Outside a
/// container the launcher restarts it.
/// </summary>
public sealed class ApplicationRestarter : IApplicationRestarter
{
    private readonly IHostApplicationLifetime _lifetime;

    public ApplicationRestarter(IHostApplicationLifetime lifetime) => _lifetime = lifetime;

    public void Restart() => _lifetime.StopApplication();
}
