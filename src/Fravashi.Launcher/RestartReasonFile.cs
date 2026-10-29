namespace Fravashi.Launcher;

/// <summary>
/// Leaves a plain-words reason for a restart the launcher caused on purpose (backlog 218's DNS recovery is
/// the first writer), for the studio to pick up at its own next start and show instead of the generic "the
/// studio restarted" sentence an orphaned conversation otherwise ends on.
/// <para>
/// The file lives at the root of the shared data directory - <c>&lt;DataDir&gt;/studio-restart-reason.txt</c>
/// here, <c>/data/studio-restart-reason.txt</c> as the studio reads it
/// (<c>Fravashi.Infrastructure.Sessions.StudioRestartReason.ConsumeOnce</c>). There is no shared type to carry
/// the file name across that boundary (this library never depends on the studio and the studio never depends
/// on it), so it is a literal on both sides - change one and the other, together.
/// </para>
/// <para>
/// Writing is always best-effort: a failure here must never stop the restart it is explaining, so it is
/// logged and swallowed, never thrown.
/// </para>
/// </summary>
public static class RestartReasonFile
{
    public const string FileName = "studio-restart-reason.txt";

    public static void Write(string dataDirectory, string reason, Action<string>? log = null)
    {
        try
        {
            File.WriteAllText(Path.Combine(dataDirectory, FileName), reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Could not leave a restart reason for the studio to read ({ex.Message}); it will show the generic one.");
        }
    }
}
