using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// Engine failures the launcher has actually met and can say something useful about (backlog 134), instead of
/// handing the person a stack trace whose only readable part is the engine's own sentence.
/// <para>
/// Each entry is here because it happened on a real machine, and says only what was seen to work. The launcher
/// does not perform the repair itself: the one below means reaching into the engine's own storage, which is
/// not the launcher's to touch unasked.
/// </para>
/// </summary>
public static class EngineFailures
{
    /// <summary>
    /// What to tell the person about a failed <c>run</c>, or null when the launcher has nothing to add to
    /// what the engine said.
    /// </summary>
    public static string? Explain(string cli, string containerName, string engineSaid)
    {
        ArgumentNullException.ThrowIfNull(engineSaid);

        // Met on the owner's machine, 2026-09-21, installing v0.9.25: the old container had left the engine's
        // database and NOT its storage, so `ps -a` did not list it, the launcher saw nothing to remove, and
        // `run --name` was refused. `ps -a --external` showed it with the status "Storage".
        if (engineSaid.Contains("is already in use", StringComparison.OrdinalIgnoreCase)
            && engineSaid.Contains(containerName, StringComparison.Ordinal))
        {
            return
                $"The container engine still holds the name '{containerName}' for a container that no longer exists: "
                + "a leftover in its storage, usually after it failed to unmount the old container. None of your "
                + "data is in it (the data folder and the workspaces volume are separate and untouched). "
                + $"To clear it, run: {cli} rm -f {containerName}   and then start {ProductName.Short} again. If that answers "
                + "that a folder ending in /merged is \"not empty\", that folder holds only empty leftovers: remove "
                + $"them with   {cli} machine ssh \"sudo find <the path it named> -mindepth 1 -type d -empty -delete\"   "
                + "(it deletes empty folders and nothing else), then repeat the remove.";
        }

        // Met on the test VM, 2026-10-08 (backlog 252): WSL 3.0's cgroup isolation, under which no podman container starts.
        return PodmanWords.CgroupIsolation(cli, engineSaid);
    }

    /// <summary>
    /// The explanation an <see cref="ExplainedEngineFailure"/> anywhere in <paramref name="failure"/>'s chain carries,
    /// or null when the launcher had nothing to explain. What a start that failed shows the person (backlog 252).
    /// </summary>
    public static string? ExplanationIn(Exception? failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is ExplainedEngineFailure explained)
            {
                return explained.Message;
            }

            if (current is AggregateException { InnerExceptions.Count: > 0 } aggregate)
            {
                return aggregate.InnerExceptions.Select(ExplanationIn).FirstOrDefault(said => said is not null);
            }
        }

        return null;
    }
}

/// <summary>
/// A container-engine failure the launcher recognised and explained (<see cref="EngineFailures"/>). Its message is
/// the explanation and the engine's own words are the inner exception, so a log keeps both; fravashi.exe tells it apart
/// from any other failure so the explanation reaches the person's screen and not only the log (backlog 252).
/// </summary>
public sealed class ExplainedEngineFailure(string explanation, Exception engineSaid)
    : InvalidOperationException(explanation, engineSaid);
