using System.Globalization;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Infrastructure.Notifications;

/// <summary>
/// <see cref="IParkedNoticeDismissals"/> in the studio's own key-value store (work item #356): the smallest state that
/// survives a restart, one key per dismissed (run, item) pair, holding when it was dismissed. No schema step, because
/// nothing reads these keys but this class.
/// </summary>
public sealed class ConfigurationParkedNoticeDismissals(IConfigurationStore store, TimeProvider? clock = null)
    : IParkedNoticeDismissals
{
    /// <summary>The key prefix every dismissal is stored under.</summary>
    public const string Prefix = "Autopilot:DismissedParked:";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    public bool IsDismissed(Guid run, WorkItemId item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return !string.IsNullOrEmpty(store.Get(Key(run, item)));
    }

    /// <inheritdoc />
    public void Dismiss(Guid run, WorkItemId item)
    {
        ArgumentNullException.ThrowIfNull(item);
        store.Set(Key(run, item), _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
    }

    // A colon is the configuration separator, so an id carrying one cannot split the key into sections.
    private static string Key(Guid run, WorkItemId item) =>
        Prefix + run.ToString("N", CultureInfo.InvariantCulture) + ":" + item.Value.Replace(':', '_');
}
