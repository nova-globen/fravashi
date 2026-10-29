namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// Reads the plain-words reason the LAUNCHER left for a restart it caused on purpose (backlog 218's DNS
/// recovery is the first writer), so an orphaned conversation's ending can name the real cause instead of
/// the generic "the studio restarted" sentence (<see cref="LiveSessionRegistry.OrphanedEnding"/>).
/// <para>
/// The file lives at the root of the shared data directory (<c>&lt;DataDir&gt;/studio-restart-reason.txt</c>
/// on the host, <c>/data/studio-restart-reason.txt</c> in the container - the one path both sides can reach
/// without either referencing the other's assembly: fravashi.exe's launcher library is cross-platform and never
/// depends on the studio, and the studio never depends on the launcher). There is no shared type to carry the
/// file name, so it is a literal on both sides (<c>Fravashi.Launcher.RestartReasonFile.FileName</c> there,
/// this constant here) - change one and the other, together.
/// </para>
/// <para>
/// Consumed EXACTLY ONCE: read and deleted together, so a later restart with no particular cause (a plain
/// <c>fravashi stop</c>/<c>fravashi</c>, or a crash) never finds a stale reason left over from an earlier one.
/// </para>
/// </summary>
public static class StudioRestartReason
{
    public const string FileName = "studio-restart-reason.txt";

    /// <summary>
    /// The reason, if the launcher left one, or null when there is none (the ordinary case) or it cannot be
    /// read. Best-effort and silent on failure: a missing or unreadable reason must never stop the studio
    /// from starting, and the ordinary path - nothing left here - is not an error.
    /// </summary>
    public static string? ConsumeOnce(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            File.Delete(path);
            return text.Length == 0 ? null : text;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
