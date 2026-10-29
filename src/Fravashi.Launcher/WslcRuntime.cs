using System.Text.Json;
using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// Container runtime backed by Microsoft's WSL Container CLI (<c>wslc</c>), GA in WSL 3.0.1 and since 2026-10-04 the
/// only engine on the dev host (ADR 0029). Its command surface differs from Docker's in several places, each
/// measured on 3.0.1 rather than assumed:
/// <list type="bullet">
///   <item>The executable is not on PATH by default (<see cref="WslcExecutable"/>).</item>
///   <item><c>version</c> takes no <c>--format</c>; a zero exit means the engine responds.</item>
///   <item>Introspection is <c>inspect -t image|container</c>, which prints a JSON ARRAY (one element per object,
///     an empty array and exit 1 for a missing one) and takes no Go templates, so every read here parses JSON.</item>
///   <item><c>rmi</c> without <c>--force</c> refuses an image a container still uses (exit 1), exactly the retire
///     guard Docker's gives.</item>
///   <item>Logs use <c>--tail</c> (alias <c>-n</c>).</item>
///   <item><b>No <c>--restart</c> flag exists.</b> The launcher instead re-creates-or-starts the container on every
///     <c>fravashi</c> run, tray Open and window relaunch, so the product comes back then, not spontaneously after a
///     reboot (ADR 0029, decision D3).</item>
/// </list>
/// The hardening flags T7/T8/T9 rely on under Docker (<c>--cap-drop</c>, <c>--security-opt</c>,
/// <c>--read-only</c>, <c>--pids-limit</c>) are not accepted by <c>wslc run</c> today; <see cref="ContainerHardening"/>
/// records them as Unavailable rather than pretending parity.
/// </summary>
public sealed class WslcRuntime : CliContainerRuntime
{
    public override string Name => RuntimeNames.Wslc;

    protected override string Cli { get; } = WslcExecutable.Resolve();

    protected override string[] AvailabilityProbeArgs => ["version"];

    /// <summary>
    /// wslc has no machine of its own: WSL starts the backing distro on demand when the CLI is used, so there
    /// is nothing for the launcher to start. An unresponsive wslc means WSL itself is stuck, which only
    /// <c>wsl --shutdown</c> (elevated, and destructive to other distros) clears, so Fravashi says it rather
    /// than doing it.
    /// </summary>
    public override MachineStartResult TryStartMachine() => MachineStartResult.NotSupported(
        $"wslc starts its WSL distro on demand, so there is no machine for {ProductName.Short} to start. If it is not " +
        $"answering, WSL itself is stuck: run `wsl --shutdown` from an elevated prompt, then run `{ProductName.Command}` again.");

    public override bool ImageExists(string image)
        => Run("inspect", "--type", "image", image).ExitCode == 0;

    public override bool ContainerExists(string containerName)
        => Run("inspect", "--type", "container", containerName).ExitCode == 0;

    public override bool ContainerRunning(string containerName)
    {
        var (exitCode, stdout, _) = Run("inspect", "--type", "container", containerName);
        return exitCode == 0 && ReadStateRunning(stdout);
    }

    public override void StartContainer(string containerName)
        => RunChecked("start", containerName);

    public override void StopContainer(string containerName)
        => RunChecked("stop", containerName);

    // Force-remove; a missing container exits non-zero, which we ignore (removal is idempotent by intent).
    public override void RemoveContainer(string containerName)
        => Run("rm", "--force", containerName);

    public override string? ImageVersion(string image)
    {
        var (exitCode, stdout, _) = Run("inspect", "--type", "image", image);
        if (exitCode != 0)
        {
            return null;
        }

        var root = FirstElement(stdout);
        if (root is not { } element)
        {
            return null;
        }

        var id = element.TryGetProperty("Id", out var idProp) ? idProp.GetString() : null;
        var created = element.TryGetProperty("Created", out var createdProp) ? createdProp.GetString() : null;
        return string.IsNullOrEmpty(id)
            ? null
            : string.IsNullOrEmpty(created) ? id : $"{id} (created {created})";
    }

    public override string? ResolveImageDigest(string image)
    {
        var (exitCode, stdout, _) = Run("inspect", "--type", "image", image);
        if (exitCode != 0 || FirstElement(stdout) is not { } element)
        {
            return null;
        }

        var id = element.TryGetProperty("Id", out var idProp) ? idProp.GetString() : null;
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    public override string GetLogs(string containerName, int tail)
    {
        var (exitCode, stdout, stderr) = Run("logs", "--tail", tail.ToString(), containerName);
        return exitCode == 0 ? string.Concat(stdout, stderr) : stderr.Trim();
    }

    // Deliberately NON-forced, as on Docker: wslc refuses while a container still uses the image (measured), which is
    // the retire guard. The refusal, like a missing image, is ignored; a skipped removal only costs disk.
    public override void RemoveImage(string imageRef)
        => Run("rmi", imageRef);

    /// <summary>What still names the image (backlog 165), from its <c>RepoTags</c>; empty means "cannot say".</summary>
    public override IReadOnlyList<string> ImageNames(string imageRef)
    {
        var (exitCode, stdout, _) = Run("inspect", "--type", "image", imageRef);
        return exitCode == 0 ? ReadRepoTags(stdout) : [];
    }

    /// <summary>One label of the image, from <c>[0].Config.Labels</c>.</summary>
    public override string? ImageLabel(string image, string label)
    {
        var (exitCode, stdout, _) = Run(BuildImageInspectArgs(image));
        return exitCode == 0 ? ReadLabel(stdout, label) : null;
    }

    /// <summary>The <c>inspect --type image</c> argument list labels are read from. Exposed for unit tests (pure, no process started).</summary>
    public static string[] BuildImageInspectArgs(string image) => ["inspect", "--type", "image", image];

    /// <summary>The image id a container was created from (<c>[0].Image</c>), so a reinstall under the same tag is seen.</summary>
    public override string? ContainerImageId(string containerName)
    {
        var (exitCode, stdout, _) = Run("inspect", "--type", "container", containerName);
        return exitCode == 0 ? ReadString(stdout, "Image") : null;
    }

    /// <summary>Reads <c>[0].RepoTags</c> from a <c>wslc inspect -t image</c> answer; empty on any miss. Exposed for tests.</summary>
    public static IReadOnlyList<string> ReadRepoTags(string inspectJson)
    {
        if (FirstElement(inspectJson) is not { } element
            || !element.TryGetProperty("RepoTags", out var tags)
            || tags.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. tags.EnumerateArray()
            .Where(t => t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
            .Select(t => t.GetString()!)];
    }

    /// <summary>Reads <c>[0].Config.Labels[label]</c>; null when absent. Exposed for tests.</summary>
    public static string? ReadLabel(string inspectJson, string label)
    {
        if (FirstElement(inspectJson) is not { } element
            || !element.TryGetProperty("Config", out var config)
            || config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("Labels", out var labels)
            || labels.ValueKind != JsonValueKind.Object
            || !labels.TryGetProperty(label, out var found)
            || found.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = found.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Reads a top-level string property of <c>[0]</c>; null on any miss. Exposed for tests.</summary>
    public static string? ReadString(string inspectJson, string property)
    {
        if (FirstElement(inspectJson) is not { } element
            || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // No deadline: `run` pulls the image first when it is not local, which is a download, not a hang.
    public override void RunContainer(ContainerRunSpec spec) => RunCheckedUnbounded(BuildRunArgs(spec));

    /// <summary>
    /// The <c>run</c> argument list. NOTE: no <c>--restart</c> - wslc does not support it (see the class
    /// remarks). Everything else matches the Docker run shape, so the container, its port map, its data mount,
    /// the git working-copy volume, and its env are identical. Exposed for unit tests.
    /// </summary>
    public string[] BuildRunArgs(ContainerRunSpec spec)
    {
        var args = new List<string> { "run", "--detach", "--name", spec.ContainerName };

        // F4 hardening (ADR 0007 / ADR 0009 matrix): wslc applies the SUBSET it accepts - loopback-only publish,
        // non-root user, a memory limit, tmpfs /tmp and an nproc ulimit (a partial pids cap). It REJECTS
        // --cap-drop / --security-opt / --read-only / --pids-limit, so those flags are never emitted here; the
        // launcher records them as Unavailable in the posture file rather than pretending parity with Docker.
        ContainerHardening.Plan(Name).AppendArgs(args, spec);

        // NOTE: no --restart - wslc does not support it (see the class remarks).
        args.AddRange(["--volume", $"{spec.DataDir}:/data"]);
        args.AddRange(["--env", "FRAVASHI_DATA_DIR=/data"]);
        ContainerRunArgs.AppendWorkspacesVolume(args, spec);
        ContainerRunArgs.AppendIntegrityEnv(args, spec);
        ContainerRunArgs.AppendWindowToken(args, spec);
        ContainerRunArgs.AppendVpnAppEnv(args, spec);
        ContainerRunArgs.AppendBrokerEnv(args, spec);
        ContainerRunArgs.AppendBridgeEnv(args, spec);
        args.Add(spec.Image);
        return [.. args];
    }

    /// <summary>Reads <c>[0].State.Running</c> from a <c>wslc inspect</c> JSON array; false on any parse miss.</summary>
    private static bool ReadStateRunning(string inspectJson)
    {
        if (FirstElement(inspectJson) is not { } element)
        {
            return false;
        }

        return element.TryGetProperty("State", out var state)
            && state.TryGetProperty("Running", out var running)
            && running.ValueKind == JsonValueKind.True;
    }

    /// <summary>Parses a <c>wslc inspect</c> response (a JSON array) and returns its first element, or null.</summary>
    private static JsonElement? FirstElement(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                return doc.RootElement[0].Clone();
            }
        }
        catch (JsonException)
        {
            // Not JSON (e.g. an error banner) - treat as no information.
        }

        return null;
    }
}
