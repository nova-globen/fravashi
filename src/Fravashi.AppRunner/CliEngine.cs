using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Fravashi.AppRunner;

/// <summary>
/// <see cref="IEngine"/> over the <c>docker</c> or <c>podman</c> command line. Every argument list is built HERE, from
/// the app runner's own records, and passed as an argument vector (never through a shell), so a value can never
/// become a second argument, and no field exists that could carry a mount, a device, a network mode or a flag from
/// a spec.
/// </summary>
public sealed class CliEngine(string executable) : IEngine
{
    /// <summary>
    /// The capabilities a run keeps. "Drop them all" breaks ordinary images, which start as root and switch to their
    /// own user (nginx, postgres), and that needs SETUID, SETGID and CHOWN. This is the set that pattern needs and no
    /// more: no raw sockets (NET_RAW), no device nodes (MKNOD), nothing administrative. Decided 2026-09-23 while
    /// building backlog 87; ADR 0027's addendum records it.
    /// </summary>
    public static readonly IReadOnlyList<string> KeptCapabilities =
        ["CHOWN", "DAC_OVERRIDE", "FOWNER", "FSETID", "KILL", "NET_BIND_SERVICE", "SETGID", "SETUID"];

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);
    private const int OutputTail = 64 * 1024;

    public string Name => Path.GetFileNameWithoutExtension(executable);

    // ---- argument templates (pure, so the tests can read exactly what an engine is told) ------------------------

    public static IReadOnlyList<string> BuildArgs(EngineBuild build)
    {
        var args = new List<string> { "build", "--file", build.DockerfilePath, "--tag", build.Tag };
        if (build.Target is { } target)
        {
            args.AddRange(["--target", target]);
        }

        foreach (var (name, value) in build.BuildArgs)
        {
            args.AddRange(["--build-arg", $"{name}={value}"]);
        }

        AddLabels(args, build.Labels);
        args.Add(build.ContextDirectory);
        return args;
    }

    public static IReadOnlyList<string> NetworkCreateArgs(string name, bool isolated, IReadOnlyDictionary<string, string> labels)
    {
        var args = new List<string> { "network", "create" };
        if (isolated)
        {
            // No route out at all: the engine's own deny, not ours (ADR 0027, "What a run may reach").
            args.Add("--internal");
        }

        AddLabels(args, labels);
        args.Add(name);
        return args;
    }

    /// <summary>The fixed run template. The first network is given here; the rest are connected before start.</summary>
    public static IReadOnlyList<string> CreateArgs(ContainerTemplate template)
    {
        var ceilings = template.Ceilings;
        var args = new List<string>
        {
            "create",
            "--name", template.Name,
            "--network", template.Networks[0],
            "--cap-drop", "ALL",
            "--cap-add", string.Join(',', KeptCapabilities),
            "--security-opt", "no-new-privileges",
            "--cpus", ceilings.Cpus.ToString("0.##", CultureInfo.InvariantCulture),
            "--memory", $"{ceilings.MemoryMb}m",
            // Equal to --memory: no swap, so a leak fails fast and visibly instead of dragging the host.
            "--memory-swap", $"{ceilings.MemoryMb}m",
            "--pids-limit", ceilings.Processes.ToString(CultureInfo.InvariantCulture),
        };
        AddLabels(args, template.Labels);
        if (template.ReadOnlyRoot)
        {
            args.Add("--read-only");
        }

        if (template.User is { } user)
        {
            args.AddRange(["--user", user]);
        }

        foreach (var (name, value) in template.Environment)
        {
            args.AddRange(["--env", $"{name}={value}"]);
        }

        // Backlog 226: the name only; the value reaches the engine command through its own environment (StartInfo).
        foreach (var name in template.SecretEnvironment.Keys)
        {
            args.AddRange(["--env", name]);
        }

        if (template.Publish is { } publish)
        {
            // Loopback only, on a port the APP RUNNER picked: a spec never chooses one.
            args.AddRange(["--publish", $"127.0.0.1:{publish.HostPort}:{publish.ContainerPort}"]);
        }

        if (template.Entrypoint is { } entrypoint)
        {
            args.AddRange(["--entrypoint", entrypoint]);
        }

        args.Add(template.Image);
        if (template.Command is { } command)
        {
            args.AddRange(command);
        }

        return args;
    }

    private static void AddLabels(List<string> args, IReadOnlyDictionary<string, string> labels)
    {
        foreach (var (name, value) in labels)
        {
            args.AddRange(["--label", $"{name}={value}"]);
        }
    }

    // ---- the operations -------------------------------------------------------------------------------------------

    public Task<EngineOutcome> BuildAsync(EngineBuild build, CancellationToken cancellationToken) =>
        RunAsync(BuildArgs(build), build.Timeout, cancellationToken);

    public Task<EngineOutcome> CreateNetworkAsync(
        string name, bool isolated, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken) =>
        RunAsync(NetworkCreateArgs(name, isolated, labels), CommandTimeout, cancellationToken);

    public async Task<EngineOutcome> StartContainerAsync(ContainerTemplate template, CancellationToken cancellationToken)
    {
        var created = await RunAsync(CreateArgs(template), CommandTimeout, cancellationToken, environment: template.SecretEnvironment)
            .ConfigureAwait(false);
        if (!created.Ok)
        {
            return created;
        }

        // Connected BEFORE start, one engine call per network, because docker takes one --network on create and
        // podman's multi-network syntax is its own; this sequence means the same thing to both.
        foreach (var network in template.Networks.Skip(1))
        {
            var connected = await ConnectAsync(network, template.Name, cancellationToken).ConfigureAwait(false);
            if (!connected.Ok)
            {
                return connected;
            }
        }

        return await RunAsync(["start", template.Name], CommandTimeout, cancellationToken).ConfigureAwait(false);
    }

    public Task<EngineOutcome> ConnectAsync(string network, string container, CancellationToken cancellationToken) =>
        RunAsync(["network", "connect", network, container], CommandTimeout, cancellationToken);

    public Task<EngineOutcome> DisconnectAsync(string network, string container, CancellationToken cancellationToken) =>
        RunAsync(["network", "disconnect", network, container], CommandTimeout, cancellationToken);

    public async Task<ContainerStatus> StatusAsync(string container, CancellationToken cancellationToken)
    {
        var outcome = await RunAsync(
            ["container", "inspect", "--format", "{{.State.Running}} {{.State.ExitCode}}", container],
            CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            return new ContainerStatus(false, false, null);
        }

        var parts = outcome.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var running = parts.Length > 0 && bool.TryParse(parts[0], out var r) && r;
        int? exit = parts.Length > 1 && int.TryParse(parts[1], out var e) ? e : null;
        return new ContainerStatus(true, running, exit);
    }

    public async Task<string> LogsAsync(string container, long maxBytes, CancellationToken cancellationToken)
    {
        var outcome = await RunAsync(["logs", container], CommandTimeout, cancellationToken, (int)Math.Min(maxBytes, int.MaxValue))
            .ConfigureAwait(false);
        return outcome.Output;
    }

    public async Task<long?> WritableBytesAsync(string container, CancellationToken cancellationToken)
    {
        var outcome = await RunAsync(
            ["container", "inspect", "--size", "--format", "{{.SizeRw}}", container], CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Ok && long.TryParse(outcome.Output.Trim(), out var bytes) ? bytes : null;
    }

    public Task<EngineOutcome> StopAsync(string container, CancellationToken cancellationToken) =>
        RunAsync(["stop", "--time", "5", container], CommandTimeout, cancellationToken);

    public Task<EngineOutcome> RemoveContainerAsync(string container, CancellationToken cancellationToken) =>
        RunAsync(["rm", "--force", container], CommandTimeout, cancellationToken);

    public Task<EngineOutcome> RemoveNetworkAsync(string network, CancellationToken cancellationToken) =>
        RunAsync(["network", "rm", network], CommandTimeout, cancellationToken);

    public Task<EngineOutcome> RemoveImageAsync(string image, CancellationToken cancellationToken) =>
        RunAsync(["rmi", image], CommandTimeout, cancellationToken);

    public async Task<IReadOnlyList<OwnedObject>> ListOwnedAsync(string label, string value, CancellationToken cancellationToken)
    {
        var owned = new List<OwnedObject>();
        var filter = $"label={label}={value}";
        await Collect("container", ["ps", "--all", "--filter", filter, "--format", "{{.Names}}"], ["container", "inspect", "--format", "{{json .Config.Labels}}"]).ConfigureAwait(false);
        await Collect("network", ["network", "ls", "--filter", filter, "--format", "{{.Name}}"], ["network", "inspect", "--format", "{{json .Labels}}"]).ConfigureAwait(false);
        await Collect("image", ["images", "--filter", filter, "--format", "{{.Repository}}:{{.Tag}}"], ["image", "inspect", "--format", "{{json .Config.Labels}}"]).ConfigureAwait(false);
        return owned;

        async Task Collect(string kind, IReadOnlyList<string> list, IReadOnlyList<string> inspect)
        {
            var listed = await RunAsync(list, CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (!listed.Ok)
            {
                return;
            }

            foreach (var name in listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var labels = await RunAsync([.. inspect, name], CommandTimeout, cancellationToken).ConfigureAwait(false);
                owned.Add(new OwnedObject(kind, name, ParseLabels(labels.Ok ? labels.Output : "{}")));
            }
        }
    }

    internal static IReadOnlyDictionary<string, string> ParseLabels(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json.Trim()) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    // ---- running the command line ----------------------------------------------------------------------------------

    /// <summary>
    /// How one engine command is started: an argument vector, and <paramref name="environment"/> set explicitly on top of
    /// what this process inherited, so a value an <c>--env NAME</c> argument picks up is always the one given here and
    /// never one fravashi.exe happened to carry (backlog 226). The verification-run names are removed from what is
    /// inherited, so no engine command holds them unless it is a verification run's create call.
    /// </summary>
    internal static ProcessStartInfo StartInfo(
        string executable, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        // Neither name is ever inherited: only a verification run's create call is given them, explicitly.
        foreach (var name in VerificationRunField.Names)
        {
            start.Environment.Remove(name);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        return start;
    }

    private async Task<EngineOutcome> RunAsync(
        IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken, int keep = OutputTail,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = StartInfo(executable, args, environment);

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(line);
                if (output.Length > keep * 2)
                {
                    output.Remove(0, output.Length - keep);
                }
            }
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new EngineOutcome(-1, $"the engine command could not be started: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new EngineOutcome(-2, $"the engine command did not finish within {timeout.TotalMinutes:0.#} minutes");
        }

        string text;
        lock (output)
        {
            text = output.Length > keep ? output.ToString(output.Length - keep, keep) : output.ToString();
        }

        return new EngineOutcome(process.ExitCode, text);
    }
}
