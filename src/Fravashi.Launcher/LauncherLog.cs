namespace Fravashi.Launcher;

/// <summary>
/// Tee log for the launcher (directive 2026-07-26): everything the launcher reports also
/// lands in <c>&lt;data dir&gt;/logs/launcher-yyyy-MM-dd.log</c>, the same folder the studio
/// container (mounted at <c>/data</c>) and the studio window shell write into, so one folder holds
/// every app's stream. File writes are strictly best effort; the console line always goes out.
/// </summary>
public sealed class LauncherLog
{
    private readonly string _directory;
    private readonly Action<string> _console;
    private readonly object _sync = new();

    public LauncherLog(string dataDir, Action<string> console)
    {
        _directory = Path.Combine(dataDir, "logs");
        _console = console;
    }

    /// <summary>The logs folder, for handing to child processes (the studio window).</summary>
    public string Directory => _directory;

    public void Line(string message)
    {
        _console(message);
        Append(message);
    }

    private void Append(string message)
    {
        lock (_sync)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_directory);
                File.AppendAllText(
                    Path.Combine(_directory, $"launcher-{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // Best effort by contract: a full disk must never break the launcher.
            }
        }
    }
}
