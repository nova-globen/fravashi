using Fravashi.Application.Persistence;
using Fravashi.Application.Telemetry;
using Fravashi.Domain.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Telemetry;

/// <summary>
/// The one place the opt-in switch is enforced (backlog 74). Producers call
/// <see cref="Record"/> unconditionally; this class is what decides whether anything is written, so a
/// producer added later cannot be the one that forgets to ask.
/// <para>
/// The switch is read from the settings table on EVERY record rather than cached, which is deliberate:
/// switching recording off in Settings must stop the next event, not the next restart. The cost is one
/// indexed key-value read per event, and events happen at human speed (a session start, an approval, a
/// finished turn), not in a loop.
/// </para>
/// <para>
/// Writing is best effort by contract. Telemetry exists to tell us whether the product works and must
/// never be the reason it does not: a failed append is logged at debug and swallowed.
/// </para>
/// </summary>
public sealed class ProductEventRecorder : IProductEventRecorder
{
    private readonly IConfigurationStore _settings;
    private readonly IProductEventStore _store;
    private readonly ILogger _logger;

    public ProductEventRecorder(
        IConfigurationStore settings,
        IProductEventStore store,
        ILogger<ProductEventRecorder>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? NullLogger<ProductEventRecorder>.Instance;
    }

    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            try
            {
                return TelemetrySettings.IsEnabled(_settings.Get(TelemetrySettings.EnabledKey));
            }
            catch (Exception ex)
            {
                // A settings store that cannot be read is not consent, so it reads as OFF.
                _logger.LogDebug(ex, "Could not read the telemetry opt-in; recording stays off.");
                return false;
            }
        }
    }

    /// <inheritdoc />
    public void Record(ProductEvent productEvent)
    {
        if (productEvent is null || !IsEnabled)
        {
            return;
        }

        try
        {
            _store.Append(productEvent);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not record a {Kind} product event.", productEvent.Kind);
        }
    }
}
