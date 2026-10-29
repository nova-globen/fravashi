using System.Reflection;

namespace Fravashi.WebSetup;

/// <summary>
/// The private folder one install runs from: the embedded engine written out as <c>webinstall.ps1</c>.
/// <see cref="Remove"/> deletes the folder. A removal that fails is REPORTED, never swallowed, so the log and the verdict
/// can name what was left behind.
/// </summary>
public sealed class EngineWorkspace : IDisposable
{
    public const string EngineResourceName = "Fravashi.WebSetup.webinstall.ps1";
    public const string EngineFileName = "webinstall.ps1";

    private EngineWorkspace(string folder, string scriptPath)
    {
        Folder = folder;
        ScriptPath = scriptPath;
    }

    public string Folder { get; }

    public string ScriptPath { get; }

    /// <summary>The engine exactly as it was embedded from <c>setup/webinstall/webinstall.ps1</c>.</summary>
    public static byte[] EngineBytes()
    {
        using var stream = typeof(EngineWorkspace).Assembly.GetManifestResourceStream(EngineResourceName)
            ?? throw new InvalidOperationException($"The engine resource {EngineResourceName} is missing from {Assembly.GetExecutingAssembly().GetName().Name}.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Creates a fresh folder under <paramref name="tempRoot"/> and writes the engine into it.</summary>
    public static EngineWorkspace Create(string tempRoot)
    {
        var folder = Path.Combine(tempRoot, "fravashi-websetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var scriptPath = Path.Combine(folder, EngineFileName);
            File.WriteAllBytes(scriptPath, EngineBytes());
            return new EngineWorkspace(folder, scriptPath);
        }
        catch (Exception ex)
        {
            // The report of what this partial folder left behind travels in the exception, which SetupRun puts in
            // the verdict and the log, rather than being dropped.
            var leftover = new EngineWorkspace(folder, Path.Combine(folder, EngineFileName)).Remove();
            if (leftover is null)
            {
                throw;
            }

            throw new IOException(ex.Message + " " + leftover, ex);
        }
    }

    /// <summary>
    /// Deletes the folder. Returns null when nothing is left, otherwise a sentence naming what remained and why, for
    /// the log and the verdict. It never throws: a virus scanner holding the script open must not turn a finished
    /// install into a crash.
    /// </summary>
    public string? Remove()
    {
        try
        {
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Left behind: the setup folder {Folder} could not be removed ({ex.Message}).";
        }
    }

    public void Dispose() => _ = Remove();
}
