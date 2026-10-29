using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.Persistence.Configuration;

/// <summary>
/// Configuration source for the <see cref="CoordinateBridgeConfigurationProvider"/>. It keeps a reference to the
/// provider it builds so the host can wire the connection / settings stores into it once the DI container is
/// available (see <see cref="CoordinateBridgeConfigurationProvider.Attach"/>).
/// </summary>
public sealed class CoordinateBridgeConfigurationSource : IConfigurationSource
{
    /// <summary>The provider built by <see cref="Build"/>, exposed so the host can attach its stores.</summary>
    public CoordinateBridgeConfigurationProvider Provider { get; } = new();

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}
