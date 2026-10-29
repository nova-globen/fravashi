using System.Globalization;
using Fravashi.Infrastructure.Sessions;
using Microsoft.AspNetCore.Components;

namespace Fravashi.Web.Components.Dashboard;

/// <summary>
/// The shared machinery of the home surface's insight cards (backlog 36): when they reload, and how
/// they say what period they are about.
/// <para>
/// The acceptance is "updates without a restart", and a card reading a database has two ways to learn
/// that its answer moved. The registry's <see cref="LiveSessionRegistry.SessionsChanged"/> is the
/// prompt one and covers everything a conversation does in this window; the timer is what covers the
/// rest, because a commit made in a terminal, a turn finishing in ANOTHER studio window against the same
/// data volume, and a day rolling over at midnight all change these numbers without the registry
/// hearing a thing. The interval is deliberately modest: these are aggregates over a whole month, so a
/// couple of minutes late is invisible, and polling a SQLite file harder than that buys nothing.
/// </para>
/// </summary>
public abstract class InsightCard : ComponentBase, IDisposable
{
    /// <summary>How often a card re-reads its stores when nothing tells it to.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);

    [Inject]
    protected LiveSessionRegistry Registry { get; set; } = default!;

    [Inject]
    protected TimeProvider Clock { get; set; } = default!;

    private ITimer? _timer;
    private bool _disposed;

    /// <summary>Re-reads whatever this card shows. Called on initialise, on session changes, and on the timer.</summary>
    protected abstract Task LoadAsync();

    protected override async Task OnInitializedAsync()
    {
        Registry.SessionsChanged += OnSessionsChanged;
        _timer = Clock.CreateTimer(_ => Reload(), null, RefreshInterval, RefreshInterval);
        await LoadAsync();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Registry.SessionsChanged -= OnSessionsChanged;
        _timer?.Dispose();
        _timer = null;
    }

    private void OnSessionsChanged() => Reload();

    /// <summary>
    /// Marshals a reload onto the renderer. Both triggers fire off the render thread (the timer from the
    /// pool, the registry from whichever thread the session runner was on), and either can arrive while
    /// the circuit is going away, so the disposed component is simply not woken.
    /// </summary>
    private void Reload()
    {
        if (_disposed)
        {
            return;
        }

        _ = InvokeAsync(async () =>
        {
            if (_disposed)
            {
                return;
            }

            await LoadAsync();
            StateHasChanged();
        });
    }

    /// <summary>
    /// The card's period, as the card states it. Invariant on purpose: this is a figure's label, and the
    /// host's own culture (Swedish here) would otherwise decide what month names the studio shows.
    /// </summary>
    protected static string PeriodText(DateOnly fromDay, DateOnly toDay) =>
        $"{fromDay.ToString("d MMM", CultureInfo.InvariantCulture)} to "
        + $"{toDay.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}, UTC";

    /// <summary>A whole number, invariant, for the same reason as <see cref="PeriodText"/>.</summary>
    protected static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A duration a person can read at a glance: seconds under a minute, then minutes, then hours and
    /// minutes. Never a bare decimal of hours, which is the shape nobody can picture.
    /// </summary>
    protected static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 60)
        {
            return $"{(int)Math.Round(span.TotalSeconds, MidpointRounding.AwayFromZero)}s";
        }

        if (span.TotalMinutes < 60)
        {
            return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        }

        return $"{(int)span.TotalHours}h {span.Minutes}m";
    }
}
