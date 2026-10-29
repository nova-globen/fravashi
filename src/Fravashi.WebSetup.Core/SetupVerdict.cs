using Fravashi.Domain;

namespace Fravashi.WebSetup;

/// <summary>What an install came to, in words for the wizard and the log, and as the process exit code.</summary>
public sealed record SetupVerdict(SetupExitCode Code, string Headline, string? Detail)
{
    /// <summary>
    /// Maps how the engine ended to a verdict. The engine's own contract is the basis: it exits 0 after
    /// printing "Nova Globen Fravashi &lt;tag&gt; is installed.", and every failure goes through <c>Die</c> or its <c>trap</c>,
    /// which print the reason and exit 1. Anything else did not come from the engine, so it is never called
    /// an install failure (it would send somebody looking in the wrong place).
    /// </summary>
    public static SetupVerdict From(EngineExit exit, EngineProgress progress)
    {
        if (exit.Cancelled)
        {
            var during = progress.Stage >= EngineStage.Installing
                ? "The release's own installer had already started, so check what it left before running setup again."
                : "Nothing was installed: the image and the launcher are only touched after every download is verified. " +
                  "Files already downloaded may remain in a fravashi-webinstall folder under %TEMP%.";
            return new SetupVerdict(SetupExitCode.Cancelled, "The install was cancelled.", during);
        }

        if (exit.StartError is not null)
        {
            return new SetupVerdict(SetupExitCode.EngineDidNotRun, "Windows PowerShell could not be started.", exit.StartError);
        }

        switch (exit.ExitCode)
        {
            case 0 when progress.Stage == EngineStage.Installed:
                return new SetupVerdict(
                    SetupExitCode.Installed,
                    $"{ProductName.Short} {progress.ResolvedTag ?? "the release"} is installed.",
                    $"Start it from the {ProductName.Short} shortcut, or run {ProductName.Command}.");
            case 0:
                return new SetupVerdict(
                    SetupExitCode.EngineDidNotRun,
                    "The installer ended without saying the release is installed.",
                    progress.LastLine ?? progress.LastErrorLine);
            case 1 when progress.NotPublished:
                // The channel answered 404 for that version's manifest: said plainly rather than reported as a failed
                // install, because nothing went wrong but the version asked for.
                return new SetupVerdict(
                    SetupExitCode.NotPublished,
                    $"No {ProductName.Short} release is published on the update channel for this version.",
                    "Check the version, or leave it empty for the newest. " + (progress.LastLine ?? ""));
            case 1:
                return new SetupVerdict(
                    SetupExitCode.Failed,
                    $"The {ProductName.Short} install did not complete.",
                    Reason(progress));
            default:
                return new SetupVerdict(
                    SetupExitCode.EngineDidNotRun,
                    $"Windows PowerShell ended with exit code {exit.ExitCode} before the installer reached a verdict.",
                    Reason(progress));
        }
    }

    private static string? Reason(EngineProgress progress)
    {
        var line = progress.LastLine;
        if (line is not null && line.StartsWith(EngineProgress.FailedPrefix, StringComparison.Ordinal))
        {
            line = line[EngineProgress.FailedPrefix.Length..];
        }

        return line ?? progress.LastErrorLine;
    }
}
