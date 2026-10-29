using System.Text.Json;
using Fravashi.Launcher;

namespace Fravashi.Desktop;

/// <summary>
/// Best-effort file log for the studio window (directive 2026-07-26): host lifecycle,
/// unhandled exceptions, and the page's developer-console stream land in
/// <c>&lt;logs dir&gt;/window-yyyy-MM-dd.log</c>. The merged fravashi.exe passes the logs directory
/// in-process via <see cref="Init"/>; the <c>FRAVASHI_LOGS_DIR</c> env var remains as a fallback so
/// a directly-started exe still logs somewhere sensible.
/// </summary>
internal static class WindowLog
{
    private static readonly object Sync = new();
    private static string? _directory;

    public static void Init(string? directory = null)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _directory = directory!;
            return;
        }

        var configured = Environment.GetEnvironmentVariable("FRAVASHI_LOGS_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _directory = configured;
        }
    }

    /// <summary>Where the window log is being written; named on the window's own failure page.</summary>
    public static string Directory => _directory ??= Path.Combine(ProductHome.DataDir(ProductHome.Current), "logs");

    public static void Line(string source, string message)
    {
        lock (Sync)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(
                    Path.Combine(Directory, $"window-{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{source}] {message}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // Best effort by contract: logging must never take the window down.
            }
        }
    }

    /// <summary>
    /// Compress a DevTools protocol payload (Runtime.consoleAPICalled / Runtime.exceptionThrown)
    /// into one readable line; unparseable payloads are logged raw rather than dropped.
    /// </summary>
    public static string Summarize(string parameterJson)
    {
        try
        {
            using var document = JsonDocument.Parse(parameterJson);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var type))
            {
                var parts = new List<string>();
                if (root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                {
                    foreach (var argument in args.EnumerateArray())
                    {
                        parts.Add(
                            argument.TryGetProperty("value", out var value) ? value.ToString()
                            : argument.TryGetProperty("description", out var description) ? description.ToString()
                            : argument.ToString());
                    }
                }

                return $"{type.GetString()}: {string.Join(" ", parts)}";
            }

            if (root.TryGetProperty("exceptionDetails", out var details))
            {
                return details.TryGetProperty("exception", out var exception)
                    && exception.TryGetProperty("description", out var exceptionText)
                    ? exceptionText.ToString()
                    : details.ToString();
            }

            return parameterJson;
        }
        catch (JsonException)
        {
            return parameterJson;
        }
    }
}
