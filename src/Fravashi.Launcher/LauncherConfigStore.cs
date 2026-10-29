using System.Text.Json;

namespace Fravashi.Launcher;

/// <summary>Loads and saves <see cref="LauncherConfig"/> as JSON at a fixed path.</summary>
public sealed class LauncherConfigStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;

    public LauncherConfigStore(string path) => _path = path;

    /// <summary>The directory holding launcher.json - also where the installer drops <c>installed-release.json</c>.</summary>
    public string ConfigDirectory => Path.GetDirectoryName(_path) ?? ".";

    /// <summary>
    /// The default per-user config path: <c>&lt;LocalAppData&gt;/Fravashi/launcher.json</c>, or the former
    /// folder's while that one is still in use (<see cref="ProductHome"/>).
    /// </summary>
    public static string DefaultPath => ProductHome.ConfigPath(ProductHome.Current);

    public LauncherConfig? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(_path), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(LauncherConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(config, Json));
    }
}
