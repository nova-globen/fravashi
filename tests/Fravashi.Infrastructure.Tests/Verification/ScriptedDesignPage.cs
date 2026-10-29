using System.Globalization;
using Fravashi.Application.Verification;
using Fravashi.Infrastructure.Verification;

namespace Fravashi.Infrastructure.Tests.Verification;

/// <summary>
/// A page with no browser behind it (backlog 223): each state is an aria snapshot, a load goes to the start state and an
/// act moves to whatever state the script names for that reference. Records every call, so a test can say what the drive
/// did as well as what it saw.
/// </summary>
internal sealed class ScriptedDesignPage : IDesignPage
{
    private readonly Dictionary<string, string> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _moves = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DesignProbe> _probes = new(StringComparer.Ordinal);
    private readonly List<string> _said = [];

    public string Start { get; set; } = "rest";

    public string State { get; private set; } = "rest";

    public int Width { get; private set; }

    public List<string> Calls { get; } = [];

    /// <summary>Thrown by the next load, then forgotten.</summary>
    public DesignPageException? FailNextLoad { get; set; }

    /// <summary>Run on every load before it completes: a test that wants a load to hang waits here.</summary>
    public Func<CancellationToken, Task>? OnLoad { get; set; }

    /// <summary>The state a width puts the page in at a load, when it differs per viewport.</summary>
    public Func<int, string>? StartAt { get; set; }

    /// <summary>Where an act on a reference leads from a state, when that depends on the state; null falls back to <see cref="Move"/>.</summary>
    public Func<string, string, string?>? MoveWith { get; set; }

    public ScriptedDesignPage Snapshot(string state, string text)
    {
        _snapshots[state] = text;
        return this;
    }

    public ScriptedDesignPage Move(string reference, string to)
    {
        _moves[reference] = to;
        return this;
    }

    public ScriptedDesignPage Probe(string state, string reference, DesignProbe probe)
    {
        _probes[state + "|" + reference] = probe;
        return this;
    }

    public ScriptedDesignPage Says(params string[] entries)
    {
        _said.AddRange(entries);
        return this;
    }

    public Task SetViewportAsync(int width, int height, CancellationToken cancellationToken)
    {
        Width = width;
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"viewport {width}x{height}"));
        return Task.CompletedTask;
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        Calls.Add("load");
        if (OnLoad is { } wait)
        {
            await wait(cancellationToken);
        }

        if (FailNextLoad is { } failure)
        {
            FailNextLoad = null;
            throw failure;
        }

        State = StartAt?.Invoke(Width) ?? Start;
    }

    public Task SettleAsync(bool dialog, CancellationToken cancellationToken)
    {
        Calls.Add(dialog ? "settle dialog" : "settle");
        return Task.CompletedTask;
    }

    public Task<string> SnapshotAsync(CancellationToken cancellationToken)
    {
        Calls.Add("snapshot " + State);
        return _snapshots.TryGetValue(State, out var text)
            ? Task.FromResult(text)
            : throw new DesignPageException("no snapshot scripted for " + State);
    }

    public Task<DesignProbe> ProbeAsync(string reference, string role, DesignProbeAsk ask, CancellationToken cancellationToken)
    {
        Calls.Add($"probe {reference} {role} box={ask.Box} contrast={ask.Contrast} name={ask.Name}");
        return Task.FromResult(_probes.TryGetValue(State + "|" + reference, out var probe) ? probe : new DesignProbe(null, null, null));
    }

    public Task ActAsync(string reference, DesignPageAct act, CancellationToken cancellationToken)
    {
        Calls.Add($"act {act} {reference}");
        if (MoveWith?.Invoke(State, reference) is { } moved)
        {
            State = moved;
        }
        else if (_moves.TryGetValue(reference, out var to))
        {
            State = to;
        }

        return Task.CompletedTask;
    }

    public Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken)
    {
        Calls.Add("screenshot " + State);
        return Task.FromResult(new byte[] { 0x89, (byte)State.Length });
    }

    public IReadOnlyList<string> TakeConsole()
    {
        var taken = _said.ToList();
        _said.Clear();
        return taken;
    }
}
