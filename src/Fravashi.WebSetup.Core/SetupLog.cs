using System.Globalization;
using System.Text;

namespace Fravashi.WebSetup;

/// <summary>
/// The install's log file: every engine line with a time, the command that ran it, and the verdict. It is what
/// a silent run leaves for whoever drove it, and what the wizard names when something went wrong. The token
/// itself is never handed to it. Engine lines arrive masked when the token was TYPED; a <c>/tokenfile:</c> is
/// handed to the engine unread, so for that one the log relies on the engine never echoing the token.
/// </summary>
public sealed class SetupLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();

    private SetupLog(string path, StreamWriter writer)
    {
        Path = path;
        _writer = writer;
    }

    public string Path { get; }

    /// <summary>The default log path: <c>FravashiSetup-&lt;utc&gt;.log</c> in the temp folder.</summary>
    public static string DefaultPath(string tempRoot, DateTimeOffset now) =>
        System.IO.Path.Combine(tempRoot, "FravashiSetup-" + now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");

    public static SetupLog Open(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var writer = new StreamWriter(path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        return new SetupLog(path, writer);
    }

    public void Write(string line)
    {
        lock (_gate)
        {
            _writer.WriteLine(DateTimeOffset.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}
