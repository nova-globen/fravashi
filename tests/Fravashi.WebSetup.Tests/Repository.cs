namespace Fravashi.WebSetup.Tests;

internal static class Repository
{
    public static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fravashi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (Fravashi.sln) was not found above the test output.");
    }

    public static string EnginePath => Path.Combine(Root(), "setup", "webinstall", "webinstall.ps1");

    public static string EngineText => File.ReadAllText(EnginePath);
}

/// <summary>A scratch folder per test, removed afterwards.</summary>
internal sealed class Scratch : IDisposable
{
    public Scratch() => Directory.CreateDirectory(Folder);

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "websetup-tests-" + Guid.NewGuid().ToString("N"));

    public string File(string name) => Path.Combine(Folder, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
