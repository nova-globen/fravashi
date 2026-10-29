using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Fravashi.Domain;

namespace Fravashi.AppRunner;

/// <summary>How the app runner is set up on this machine. None of it comes from a caller or a spec.</summary>
public sealed class AppRunnerOptions
{
    /// <summary>Where build contexts are assembled; each one is deleted when its build ends.</summary>
    public required string ScratchDirectory { get; init; }

    /// <summary>
    /// The image the one-port relay runs from: the studio's own image, which already carries <c>socat</c>, so the
    /// relay pulls nothing from anywhere. Tests point it at a small image that has <c>socat</c>.
    /// </summary>
    public required string RelayImage { get; init; }

    /// <summary>The studio's container, joined to each run's edge network so the verifier can reach the relay by name.</summary>
    public string? StudioContainer { get; init; }

    /// <summary>The label value that marks this install's objects, so two installs sharing an engine never touch each other's.</summary>
    public string Owner { get; init; } = "default";

    public Ceilings Ceilings { get; init; } = Ceilings.Default;

    /// <summary>How long a built image is kept before the sweep may remove it.</summary>
    public TimeSpan ImageLifetime { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>The outcome of <see cref="AppRunner.BuildAsync"/>.</summary>
public sealed record BuildOutcome(
    string? BuildId,
    int? EntryPort,
    IReadOnlyList<SpecRefusal> Refusals,
    IReadOnlyList<string> NotRead,
    IReadOnlyList<string> SkippedLinks,
    string? Failure,
    string Output)
{
    public bool Built => BuildId is not null;
}

/// <summary>The outcome of <see cref="AppRunner.StartAsync"/>.</summary>
/// <param name="LoopbackUrl">Where the HOST reaches the run: the relay's port on 127.0.0.1.</param>
/// <param name="EngineUrl">Where a container on the run's edge network (the studio's) reaches it, by name.</param>
public sealed record StartOutcome(string? RunId, string? LoopbackUrl, string? EngineUrl, DateTimeOffset? Deadline, string? Refusal, string Output)
{
    public bool Started => RunId is not null;

    /// <summary>The token minted for a verification run (backlog 226), handed back only to the caller that started it.</summary>
    public RunWindowToken? WindowToken { get; init; }
}

/// <summary>Whether a run is accepting connections on its entry port.</summary>
public sealed record ReadyOutcome(bool Known, bool Ready, bool AppRunning, int? ExitCode, TimeSpan Elapsed, string? Stopped);

/// <summary>A slice of a run's output.</summary>
public sealed record LogsOutcome(bool Known, string Text, int Next, bool Truncated);

/// <summary>One run this app runner owns, as <see cref="AppRunner.ListAsync"/> reports it.</summary>
public sealed record RunSummary(string RunId, string State, DateTimeOffset? Deadline, string? Stopped);

/// <summary>
/// The app runner (backlog 87, ADR 0027): builds and runs a project's application in sibling containers for the
/// verifier, through a CLOSED set of operations: <see cref="BuildAsync"/>, <see cref="StartAsync"/>,
/// <see cref="ReadyAsync"/>, <see cref="LogsAsync"/>, <see cref="StopAsync"/>, <see cref="RemoveAsync"/> and
/// <see cref="ListAsync"/>, plus the <see cref="SweepAsync"/> that runs on its own.
/// <para>
/// The shape of a run, measured on podman 6.1 before a line of this was written: the APPLICATION sits alone on an
/// internal network (<c>--internal</c>), so it has no route to the internet, the host, the studio or the bridge.
/// An internal network cannot publish a port (measured: the published port never answers), so the one way in is
/// a RELAY the app runner starts from its own image: it joins the internal network and a second, ordinary one, and
/// forwards one port to the application. The relay is the app runner's program, never the spec's.
/// </para>
/// <para>
/// Every object a run creates carries three labels: that this app runner made it (<see cref="OwnerLabel"/>), the run
/// or build id, and a deadline as an absolute time. The labels, not this process's memory, are the recovery story:
/// the sweep finds what is past its deadline and removes it, and never touches anything without the owner label.
/// </para>
/// </summary>
public sealed class AppRunner(IEngine engine, AppRunnerOptions options, Func<string, string?> resolveWorkspace, TimeProvider? clock = null)
{
    public const string OwnerLabel = "fravashi.apprunner";
    public const string IdLabel = "fravashi.apprunner.id";
    public const string DeadlineLabel = "fravashi.apprunner.deadline";

    /// <summary>The port the relay listens on inside its container.</summary>
    public const int RelayPort = 8080;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, BuildRecord> _builds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RunRecord> _runs = new(StringComparer.Ordinal);

    private sealed record BuildRecord(string Image, RunSpec Spec, IReadOnlyList<string> Images);

    private sealed class RunRecord
    {
        public required string App { get; init; }
        public required string Relay { get; init; }
        public required string Network { get; init; }
        public required string Edge { get; init; }
        public required int HostPort { get; init; }
        public required DateTimeOffset Started { get; init; }
        public required DateTimeOffset Deadline { get; init; }
        public required Ceilings Ceilings { get; init; }
        public bool StudioAttached { get; set; }
        public string? Stopped { get; set; }
        public bool Removed { get; set; }
    }

    // ---- Build -----------------------------------------------------------------------------------------------------

    public async Task<BuildOutcome> BuildAsync(string workspaceId, string? specPath, CancellationToken cancellationToken)
    {
        var root = resolveWorkspace(workspaceId);
        if (root is null || !Directory.Exists(root))
        {
            return Failed("the workspace is not one the host has registered");
        }

        var read = SpecReader.Read(root, specPath, options.Ceilings);
        if (read.Spec is not { } spec)
        {
            return new BuildOutcome(null, null, read.Refusals, [], [], null, string.Empty);
        }

        var buildId = NewId("b");
        var deadline = _clock.GetUtcNow() + options.ImageLifetime;
        var labels = Labels(buildId, deadline);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(spec.Ceilings.BuildMinutes));
        var timeout = TimeSpan.FromMinutes(spec.Ceilings.BuildMinutes);
        var images = new List<string>();
        var skipped = new List<string>();
        var output = string.Empty;
        Directory.CreateDirectory(options.ScratchDirectory);

        try
        {
            string baseImage;
            if (spec.Dockerfile is { } dockerfile)
            {
                var context = BuildContext.Assemble(root, spec.Context, options.ScratchDirectory);
                skipped.AddRange(context.SkippedLinks);
                try
                {
                    var baseTag = $"fravashi-apprun:{buildId}-base";
                    var built = await engine.BuildAsync(
                        new EngineBuild(context.Directory, WorkspacePath.Resolve(root, dockerfile)!, baseTag, spec.Target,
                            spec.BuildArgs, labels, timeout),
                        budget.Token).ConfigureAwait(false);
                    output = built.Output;
                    if (!built.Ok)
                    {
                        return Failed("the project's Dockerfile did not build", output, skipped);
                    }

                    images.Add(baseTag);
                    baseImage = baseTag;
                }
                finally
                {
                    BuildContext.TryDelete(context.Directory);
                }

                if (!spec.SpecPath.EndsWith("devcontainer.json", StringComparison.OrdinalIgnoreCase))
                {
                    // A bare Dockerfile is the application as its author built it: no layer on top.
                    return Record(buildId, baseTag: baseImage, spec, images, skipped, output);
                }
            }
            else
            {
                baseImage = spec.BaseImage!;
            }

            // The application layer: the workspace's source, copied in through the build, because a run mounts
            // nothing. The Dockerfile for it is written by the app runner and lives OUTSIDE the context, so it
            // never lands in the image.
            var source = BuildContext.Assemble(root, string.Empty, options.ScratchDirectory);
            skipped.AddRange(source.SkippedLinks);
            var layerFile = Path.Combine(options.ScratchDirectory, $"layer-{buildId}.Dockerfile");
            try
            {
                await File.WriteAllTextAsync(layerFile, LayerDockerfile(baseImage, spec), cancellationToken).ConfigureAwait(false);
                var tag = $"fravashi-apprun:{buildId}";
                var layered = await engine.BuildAsync(
                    new EngineBuild(source.Directory, layerFile, tag, null, new Dictionary<string, string>(), labels, timeout),
                    budget.Token).ConfigureAwait(false);
                output = layered.Output;
                if (!layered.Ok)
                {
                    return Failed("the application layer did not build", output, skipped);
                }

                images.Add(tag);
                return Record(buildId, tag, spec, images, skipped, output);
            }
            finally
            {
                BuildContext.TryDelete(source.Directory);
                TryDeleteFile(layerFile);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Failed(ex.Message, output, skipped);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed($"the build did not finish within {spec.Ceilings.BuildMinutes} minutes", output, skipped);
        }
    }

    /// <summary>The app runner's own Dockerfile for the application layer. Every value in it was validated by <see cref="SpecReader"/>.</summary>
    internal static string LayerDockerfile(string baseImage, RunSpec spec)
    {
        var chown = spec.User is { } user ? $"--chown={user} " : string.Empty;
        return $"FROM {baseImage}\nCOPY {chown}. {spec.WorkspaceFolder}\nWORKDIR {spec.WorkspaceFolder}\n";
    }

    private BuildOutcome Record(string buildId, string baseTag, RunSpec spec, List<string> images, List<string> skipped, string output)
    {
        _builds[buildId] = new BuildRecord(baseTag, spec, images);
        return new BuildOutcome(buildId, spec.EntryPort, [], spec.NotRead, skipped, null, output);
    }

    private static BuildOutcome Failed(string reason, string output = "", IReadOnlyList<string>? skipped = null) =>
        new(null, null, [], [], skipped ?? [], reason, output);

    // ---- Start -----------------------------------------------------------------------------------------------------

    /// <param name="buildId">A build this app runner made.</param>
    /// <param name="entryPort">The application's port, or null for the spec's.</param>
    /// <param name="lowered">Ceilings the caller lowers for this run.</param>
    /// <param name="cancellationToken">Abandons the start.</param>
    /// <param name="verification">
    /// Backlog 226: start it as a verification run. A window token is minted for THIS run and only the application
    /// container gets <see cref="VerificationRunField"/>; the token comes back on <see cref="StartOutcome.WindowToken"/>.
    /// No other operation ever passes the field.
    /// </param>
    public async Task<StartOutcome> StartAsync(
        string buildId, int? entryPort, LimitsRequest? lowered, CancellationToken cancellationToken, bool verification = false)
    {
        if (!_builds.TryGetValue(buildId, out var build))
        {
            return new StartOutcome(null, null, null, null, "no build with that id was made by this app runner", string.Empty);
        }

        var port = entryPort ?? build.Spec.EntryPort;
        if (port is not ( > 0 and < 65536))
        {
            return new StartOutcome(null, null, null, null,
                "no entry port: name one, or declare it in the spec (customizations.fravashi.port, forwardPorts or EXPOSE)", string.Empty);
        }

        var ceilings = build.Spec.Ceilings;
        if (lowered is not null)
        {
            // Only what the caller named is lowered; everything else keeps the build's value.
            var (result, refusal) = ceilings.Lower(lowered.Cpus, lowered.MemoryMb, lowered.Processes, lowered.WritableMb, lowered.RunMinutes);
            if (refusal is not null)
            {
                return new StartOutcome(null, null, null, null, refusal, string.Empty);
            }

            ceilings = result!;
        }

        var runId = NewId("r");
        var now = _clock.GetUtcNow();
        var run = new RunRecord
        {
            App = $"fravashi-run-{runId}",
            Relay = $"fravashi-run-{runId}-relay",
            Network = $"fravashi-run-{runId}",
            Edge = $"fravashi-run-{runId}-edge",
            HostPort = FreeLoopbackPort(),
            Started = now,
            Deadline = now.AddMinutes(ceilings.RunMinutes),
            Ceilings = ceilings,
        };
        _runs[runId] = run;
        var labels = Labels(runId, run.Deadline);
        var windowToken = verification ? VerificationRunField.Mint() : null;

        var steps = new (Func<Task<EngineOutcome>> Step, string What)[]
        {
            (() => engine.CreateNetworkAsync(run.Network, isolated: true, labels, cancellationToken), "the run's network"),
            (() => engine.CreateNetworkAsync(run.Edge, isolated: false, labels, cancellationToken), "the relay's network"),
            (() => engine.StartContainerAsync(new ContainerTemplate(
                run.App, build.Image, [run.Network], labels, build.Spec.Environment, build.Spec.User, build.Spec.Command,
                Entrypoint: null, Publish: null, ceilings, ReadOnlyRoot: false)
            {
                SecretEnvironment = windowToken?.Environment ?? new Dictionary<string, string>(),
            }, cancellationToken), "the application"),
            (() => engine.StartContainerAsync(new ContainerTemplate(
                run.Relay, options.RelayImage, [run.Edge, run.Network], labels, new Dictionary<string, string>(), "65534:65534",
                [$"TCP-LISTEN:{RelayPort},fork,reuseaddr", $"TCP:{run.App}:{port}"],
                Entrypoint: "socat", new LoopbackPublish(run.HostPort, RelayPort),
                new Ceilings(0.5, 128, 64, 64, ceilings.RunMinutes, ceilings.BuildMinutes, ceilings.LogBytes), ReadOnlyRoot: true),
                cancellationToken), "the relay"),
        };

        foreach (var (step, what) in steps)
        {
            var outcome = await step().ConfigureAwait(false);
            if (!outcome.Ok)
            {
                await TearDownAsync(run, CancellationToken.None).ConfigureAwait(false);
                _runs.TryRemove(runId, out _);
                return new StartOutcome(null, null, null, null, $"{what} could not be started", outcome.Output);
            }
        }

        if (options.StudioContainer is { Length: > 0 } studio)
        {
            // Not best effort (backlog 270): the studio is the only caller, it reaches the run by the relay's NAME, and
            // that name resolves only on the edge network. A run it cannot join is one no step can reach, so it is
            // refused in the engine's words rather than handed out to fail later on a DNS error. Rootless Podman
            // refusing a pasta container is the case that was measured.
            var joined = await engine.ConnectAsync(run.Edge, studio, cancellationToken).ConfigureAwait(false);
            if (!joined.Ok)
            {
                await TearDownAsync(run, CancellationToken.None).ConfigureAwait(false);
                _runs.TryRemove(runId, out _);
                return new StartOutcome(null, null, null, null,
                    $"the studio could not join the run's network, so no step could reach the application: {joined.Output.Trim()}"
                    + PastaHint(joined.Output),
                    joined.Output);
            }

            run.StudioAttached = true;
        }

        return new StartOutcome(
            runId, $"http://127.0.0.1:{run.HostPort}", $"http://{run.Relay}:{RelayPort}", run.Deadline, null, string.Empty)
        {
            WindowToken = windowToken,
        };
    }

    /// <summary>
    /// Advice only for the case that was measured (backlog 270): the engine naming pasta means a rootless Podman studio
    /// created before the launcher gave it a network of its own. Any other refusal stands in the engine's words alone.
    /// </summary>
    private static string PastaHint(string engineOutput) =>
        engineOutput.Contains("pasta", StringComparison.OrdinalIgnoreCase)
            ? ". The studio's container is in pasta network mode; on rootless Podman without a VPN sidecar, a launcher that "
              + "carries backlog 270 recreates it on its own network at the next start at which podman info answers"
            : string.Empty;

    // ---- Ready, Logs -------------------------------------------------------------------------------------------------

    public async Task<ReadyOutcome> ReadyAsync(string runId, CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(runId, out var run) || run.Removed)
        {
            return new ReadyOutcome(false, false, false, null, TimeSpan.Zero, null);
        }

        var elapsed = _clock.GetUtcNow() - run.Started;
        var status = await engine.StatusAsync(run.App, cancellationToken).ConfigureAwait(false);
        var accepting = status.Running && await AcceptsThroughRelayAsync(run.HostPort, cancellationToken).ConfigureAwait(false);
        return new ReadyOutcome(true, accepting, status.Running, status.Running ? null : status.ExitCode, elapsed, run.Stopped);
    }

    /// <summary>
    /// Whether the application answers through the relay. The relay itself always accepts a connection, so the test
    /// is what happens NEXT: when the application refuses, the relay closes the connection straight away; when it
    /// accepts, the connection stays open (a server waiting for its client to speak) or data arrives.
    /// </summary>
    internal static async Task<bool> AcceptsThroughRelayAsync(int hostPort, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(IPAddress.Loopback, hostPort, connect.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            var buffer = new byte[1];
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(TimeSpan.FromMilliseconds(700));
            try
            {
                var read = await stream.ReadAsync(buffer, wait.Token).ConfigureAwait(false);
                return read > 0;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return true; // Still open and silent: something is holding the other end.
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    public async Task<LogsOutcome> LogsAsync(string runId, int cursor, CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(runId, out var run) || run.Removed)
        {
            return new LogsOutcome(false, string.Empty, 0, false);
        }

        var text = await engine.LogsAsync(run.App, run.Ceilings.LogBytes + 1, cancellationToken).ConfigureAwait(false);
        var truncated = text.Length > run.Ceilings.LogBytes;
        if (truncated)
        {
            text = text[..(int)Math.Min(text.Length, run.Ceilings.LogBytes)]
                + $"\n[output truncated at {run.Ceilings.LogBytes / (1024 * 1024)} MB]\n";
        }

        var from = Math.Clamp(cursor, 0, text.Length);
        return new LogsOutcome(true, text[from..], text.Length, truncated);
    }

    // ---- Stop, Remove, List ------------------------------------------------------------------------------------------

    public async Task StopAsync(string runId, CancellationToken cancellationToken)
    {
        if (_runs.TryGetValue(runId, out var run) && !run.Removed)
        {
            await engine.StopAsync(run.Relay, cancellationToken).ConfigureAwait(false);
            await engine.StopAsync(run.App, cancellationToken).ConfigureAwait(false);
            run.Stopped ??= "stopped on request";
        }
    }

    /// <summary>Removes a run and everything it created. Idempotent; an id this app runner never made is a no-op.</summary>
    public async Task<IReadOnlyList<string>> RemoveAsync(string runId, CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(runId, out var run) || run.Removed)
        {
            return [];
        }

        var stuck = await TearDownAsync(run, cancellationToken).ConfigureAwait(false);
        run.Removed = stuck.Count == 0;
        return stuck;
    }

    public async Task<IReadOnlyList<RunSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var summaries = new List<RunSummary>();
        foreach (var (id, run) in _runs)
        {
            if (run.Removed)
            {
                continue;
            }

            var status = await engine.StatusAsync(run.App, cancellationToken).ConfigureAwait(false);
            summaries.Add(new RunSummary(id, status.Running ? "running" : status.Exists ? "exited" : "gone", run.Deadline, run.Stopped));
        }

        // Runs from before this process started (a fravashi.exe restart) are still on the engine, by their labels.
        var known = summaries.Select(s => s.RunId).ToHashSet(StringComparer.Ordinal);
        foreach (var orphan in (await engine.ListOwnedAsync(OwnerLabel, options.Owner, cancellationToken).ConfigureAwait(false))
                 .Where(o => o.Kind == "container" && o.Labels.TryGetValue(IdLabel, out var id) && id.StartsWith('r') && !known.Contains(id)))
        {
            var id = orphan.Labels[IdLabel];
            known.Add(id);
            summaries.Add(new RunSummary(id, "orphaned", DeadlineOf(orphan), "left by an earlier app runner process"));
        }

        return summaries;
    }

    // ---- the sweep and the watchdog -----------------------------------------------------------------------------------

    /// <summary>
    /// Removes every object this app runner owns whose deadline has passed, and stops a live run whose writable layer
    /// has passed its ceiling (the one ceiling the engine will not hold for us). Returns what could NOT be removed,
    /// so a stuck object is reported rather than retried in silence. Touches nothing without the owner label.
    /// </summary>
    public async Task<IReadOnlyList<string>> SweepAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        foreach (var (_, run) in _runs.Where(r => !r.Value.Removed && r.Value.Stopped is null))
        {
            if (await engine.WritableBytesAsync(run.App, cancellationToken).ConfigureAwait(false) is { } bytes
                && bytes > (long)run.Ceilings.WritableMb * 1024 * 1024)
            {
                await engine.StopAsync(run.Relay, cancellationToken).ConfigureAwait(false);
                await engine.StopAsync(run.App, cancellationToken).ConfigureAwait(false);
                run.Stopped = $"stopped: its writable layer passed {run.Ceilings.WritableMb} MiB";
            }
        }

        var stuck = new List<string>();
        var owned = await engine.ListOwnedAsync(OwnerLabel, options.Owner, cancellationToken).ConfigureAwait(false);
        var expired = owned.Where(o => o.Labels.TryGetValue(OwnerLabel, out var owner) && owner == options.Owner
                                       && DeadlineOf(o) is { } deadline && deadline <= now).ToList();
        // Containers before the networks they sit on, and images last, after anything that could still use them.
        foreach (var item in expired.OrderBy(o => o.Kind switch { "container" => 0, "network" => 1, _ => 2 }))
        {
            var removed = item.Kind switch
            {
                "container" => await engine.RemoveContainerAsync(item.Name, cancellationToken).ConfigureAwait(false),
                "network" => await engine.RemoveNetworkAsync(item.Name, cancellationToken).ConfigureAwait(false),
                _ => await engine.RemoveImageAsync(item.Name, cancellationToken).ConfigureAwait(false),
            };
            if (!removed.Ok)
            {
                stuck.Add($"{item.Kind} {item.Name}");
            }
        }

        foreach (var (id, run) in _runs.Where(r => r.Value.Deadline <= now))
        {
            run.Removed = true;
            _runs.TryRemove(id, out _);
        }

        foreach (var (id, build) in _builds.ToArray())
        {
            if (expired.Any(o => o.Kind == "image" && build.Images.Contains(o.Name, StringComparer.Ordinal)))
            {
                _builds.TryRemove(id, out _);
            }
        }

        return stuck;
    }

    /// <summary>
    /// Removes everything an app runner from before the internal rename left on the engine (the bridge release): its
    /// objects carry the FORMER owner label, which <see cref="SweepAsync"/> never matches, and the studio they served is
    /// gone with the former container, so each of them goes whatever its deadline. Containers before networks, images
    /// last. Returns what could not be removed. Touches nothing without that label; removed in the cleanup release.
    /// </summary>
    public async Task<IReadOnlyList<string>> RemoveFormerLeftoversAsync(CancellationToken cancellationToken)
    {
        var stuck = new List<string>();
        var owned = await engine.ListOwnedAsync(FormerNames.AppRunnerOwnerLabel, FormerNames.AppRunnerOwner, cancellationToken)
            .ConfigureAwait(false);
        foreach (var item in owned
                     .Where(o => o.Labels.TryGetValue(FormerNames.AppRunnerOwnerLabel, out var owner) && owner == FormerNames.AppRunnerOwner)
                     .OrderBy(o => o.Kind switch { "container" => 0, "network" => 1, _ => 2 }))
        {
            var removed = item.Kind switch
            {
                "container" => await engine.RemoveContainerAsync(item.Name, cancellationToken).ConfigureAwait(false),
                "network" => await engine.RemoveNetworkAsync(item.Name, cancellationToken).ConfigureAwait(false),
                _ => await engine.RemoveImageAsync(item.Name, cancellationToken).ConfigureAwait(false),
            };
            if (!removed.Ok)
            {
                stuck.Add($"{item.Kind} {item.Name}");
            }
        }

        return stuck;
    }

    private async Task<IReadOnlyList<string>> TearDownAsync(RunRecord run, CancellationToken cancellationToken)
    {
        var stuck = new List<string>();
        if (run.StudioAttached && options.StudioContainer is { } studio)
        {
            await engine.DisconnectAsync(run.Edge, studio, cancellationToken).ConfigureAwait(false);
            run.StudioAttached = false;
        }

        foreach (var container in new[] { run.Relay, run.App })
        {
            var removed = await engine.RemoveContainerAsync(container, cancellationToken).ConfigureAwait(false);
            if (!removed.Ok && (await engine.StatusAsync(container, cancellationToken).ConfigureAwait(false)).Exists)
            {
                stuck.Add("container " + container);
            }
        }

        foreach (var network in new[] { run.Edge, run.Network })
        {
            var removed = await engine.RemoveNetworkAsync(network, cancellationToken).ConfigureAwait(false);
            if (!removed.Ok && !removed.Output.Contains("no such", StringComparison.OrdinalIgnoreCase)
                && !removed.Output.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                stuck.Add("network " + network);
            }
        }

        return stuck;
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private Dictionary<string, string> Labels(string id, DateTimeOffset deadline) => new(StringComparer.Ordinal)
    {
        [OwnerLabel] = options.Owner,
        [IdLabel] = id,
        [DeadlineLabel] = deadline.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
    };

    private static DateTimeOffset? DeadlineOf(OwnedObject item) =>
        item.Labels.TryGetValue(DeadlineLabel, out var text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var deadline)
            ? deadline
            : null;

    /// <summary>An opaque id: a prefix and twelve random hex digits. Never a name a caller could have guessed.</summary>
    private static string NewId(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

    /// <summary>
    /// A free loopback port for a run's relay, chosen from a range of its own. Not the OS's ephemeral pick: on the dev
    /// host that handed out 6095 and 9056, close enough to the studio's reserved 9090 to 9093 that a run started while
    /// the studio was down could take the port the studio needs to come back on (its sign-in redirect is registered
    /// on those ports and no other).
    /// </summary>
    internal static int FreeLoopbackPort()
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var port = RandomNumberGenerator.GetInt32(RelayPortsFrom, RelayPortsTo + 1);
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException)
            {
                // Taken, or inside a range the OS reserves: try another.
            }
        }

        throw new InvalidOperationException($"no free loopback port between {RelayPortsFrom} and {RelayPortsTo}");
    }

    /// <summary>The range relay ports come from: well clear of the studio's, the bridge's and the OS's ephemeral ones.</summary>
    public const int RelayPortsFrom = 30000;

    public const int RelayPortsTo = 39999;

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
