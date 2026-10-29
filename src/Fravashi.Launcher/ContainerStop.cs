namespace Fravashi.Launcher;

/// <summary>
/// Stopping a container, judged by its POSTCONDITION rather than by the engine's exit code. A runtime's
/// <see cref="IContainerRuntime.StopContainer"/> throws on any non-zero exit, and an engine can exit non-zero
/// AFTER it has stopped the container: podman did exactly that on the owner's machine on 2026-09-21 (the
/// container stopped, then the overlay unmount failed with "directory not empty", exit 125). Treated as
/// fatal, that killed <c>fravashi stop</c> with a stack trace, and the same call sits in the middle of a product
/// update and of a restore, where dying there abandons the job with the container already down.
/// <para>
/// So the question a caller actually has is asked: is it still running? If the engine ANSWERS that it is not,
/// the stop happened and the engine's complaint is reported as a warning, never swallowed. Otherwise the
/// failure stands exactly as before. "Answers" is load-bearing: the runtimes' running check reads false when
/// the engine cannot be reached at all, and a restore that took that silence for "stopped" would replace the
/// database underneath a container that may still be up. So silence is not an answer here: the engine has to
/// be reachable (its own probe) before its "not running" counts.
/// </para>
/// </summary>
public static class ContainerStop
{
    public static void StopAndConfirm(this IContainerRuntime runtime, string containerName, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            runtime.StopContainer(containerName);
        }
        catch (InvalidOperationException complaint) when (IsKnownToBeDown(runtime, containerName))
        {
            log($"WARNING: the container engine reported a problem while stopping '{containerName}', but the " +
                $"container is no longer running, so the stop itself succeeded. The engine said: {complaint.Message}");
        }
    }

    private static bool IsKnownToBeDown(IContainerRuntime runtime, string containerName) =>
        runtime.Probe() == RuntimeAvailability.Available && !runtime.ContainerRunning(containerName);
}
