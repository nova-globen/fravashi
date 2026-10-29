namespace Fravashi.AppRunner;

/// <summary>One build the app runner asks an engine for. Everything in it was decided by the app runner.</summary>
public sealed record EngineBuild(
    string ContextDirectory,
    string DockerfilePath,
    string Tag,
    string? Target,
    IReadOnlyDictionary<string, string> BuildArgs,
    IReadOnlyDictionary<string, string> Labels,
    TimeSpan Timeout);

/// <summary>A port published on the host's loopback: the ONE way into a run.</summary>
public sealed record LoopbackPublish(int HostPort, int ContainerPort);

/// <summary>
/// One container the app runner starts, from its FIXED template (ADR 0027). There is deliberately no field here for
/// a mount, a device, a network mode, a privileged flag, an arbitrary capability or an engine flag, so no caller and
/// no spec can ask for one: the template in <see cref="CliEngine.CreateArgs"/> is the whole of what a run gets.
/// </summary>
public sealed record ContainerTemplate(
    string Name,
    string Image,
    IReadOnlyList<string> Networks,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> Environment,
    string? User,
    IReadOnlyList<string>? Command,
    string? Entrypoint,
    LoopbackPublish? Publish,
    Ceilings Ceilings,
    bool ReadOnlyRoot)
{
    /// <summary>
    /// Environment whose VALUES never appear in an argument (backlog 226: a verification run's window token). The
    /// template names each one as <c>--env NAME</c> and the engine command is given the value in its own environment,
    /// set explicitly for that one call.
    /// </summary>
    public IReadOnlyDictionary<string, string> SecretEnvironment { get; init; } = new Dictionary<string, string>();
}

/// <summary>An engine object the app runner owns, found by its label.</summary>
public sealed record OwnedObject(string Kind, string Name, IReadOnlyDictionary<string, string> Labels);

/// <summary>What an engine says about a container.</summary>
public sealed record ContainerStatus(bool Exists, bool Running, int? ExitCode);

/// <summary>What an engine command came to: its exit code and the tail of what it printed.</summary>
public sealed record EngineOutcome(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// The app runner's window onto a container engine (docker or podman). Narrow on purpose: these are the only engine
/// operations the app runner ever needs, and the adapter is the only code that turns them into arguments.
/// </summary>
public interface IEngine
{
    /// <summary>A short name for logs, e.g. <c>podman</c>.</summary>
    string Name { get; }

    Task<EngineOutcome> BuildAsync(EngineBuild build, CancellationToken cancellationToken);

    Task<EngineOutcome> CreateNetworkAsync(string name, bool isolated, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken);

    /// <summary>Creates, connects to every network, and starts a container from the template.</summary>
    Task<EngineOutcome> StartContainerAsync(ContainerTemplate template, CancellationToken cancellationToken);

    Task<EngineOutcome> ConnectAsync(string network, string container, CancellationToken cancellationToken);

    Task<EngineOutcome> DisconnectAsync(string network, string container, CancellationToken cancellationToken);

    Task<ContainerStatus> StatusAsync(string container, CancellationToken cancellationToken);

    /// <summary>The container's combined output, at most <paramref name="maxBytes"/> of its tail.</summary>
    Task<string> LogsAsync(string container, long maxBytes, CancellationToken cancellationToken);

    /// <summary>The size of the container's writable layer in bytes, or null when the engine will not say.</summary>
    Task<long?> WritableBytesAsync(string container, CancellationToken cancellationToken);

    Task<EngineOutcome> StopAsync(string container, CancellationToken cancellationToken);

    Task<EngineOutcome> RemoveContainerAsync(string container, CancellationToken cancellationToken);

    Task<EngineOutcome> RemoveNetworkAsync(string network, CancellationToken cancellationToken);

    Task<EngineOutcome> RemoveImageAsync(string image, CancellationToken cancellationToken);

    /// <summary>Every container, network and image carrying <paramref name="label"/>=<paramref name="value"/>.</summary>
    Task<IReadOnlyList<OwnedObject>> ListOwnedAsync(string label, string value, CancellationToken cancellationToken);
}
