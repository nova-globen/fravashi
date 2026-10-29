using System.Text.Json;

namespace Fravashi.Web.Services;

/// <summary>
/// Maps tree entries to their material-icon-theme SVGs (the same file-icon set the reference editor ships as its
/// default). Parses the vendored <c>material-icons.json</c> once at startup into lookup dictionaries:
/// exact file name first, then progressively shorter multi-dot extension suffixes (so
/// <c>foo.spec.ts</c> prefers the <c>spec.ts</c> icon over the plain <c>ts</c> one), then the default
/// file icon; folders match by name with collapsed/expanded variants. Icon names resolve through
/// <c>iconDefinitions</c> to a URL under <c>vendor/material-icons/icons/</c>.
/// </summary>
public sealed class FileIconCatalog
{
    private const string IconBase = "vendor/material-icons/icons/";

    private readonly Dictionary<string, string> _fileNames;
    private readonly Dictionary<string, string> _fileExtensions;
    private readonly Dictionary<string, string> _folderNames;
    private readonly Dictionary<string, string> _folderNamesExpanded;
    private readonly Dictionary<string, string> _iconFiles;
    private readonly string _defaultFile;
    private readonly string _defaultFolder;
    private readonly string _defaultFolderExpanded;

    public FileIconCatalog(IWebHostEnvironment environment)
        : this(ReadManifest(environment))
    {
    }

    private FileIconCatalog(JsonDocument manifest)
    {
        using (manifest)
        {
            var root = manifest.RootElement;
            _fileNames = ToMap(root, "fileNames");
            _fileExtensions = ToMap(root, "fileExtensions");
            _folderNames = ToMap(root, "folderNames");
            _folderNamesExpanded = ToMap(root, "folderNamesExpanded");
            _defaultFile = root.TryGetProperty("file", out var f) ? f.GetString() ?? "file" : "file";
            _defaultFolder = root.TryGetProperty("folder", out var d) ? d.GetString() ?? "folder" : "folder";
            _defaultFolderExpanded = root.TryGetProperty("folderExpanded", out var e) ? e.GetString() ?? "folder-open" : "folder-open";

            _iconFiles = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("iconDefinitions", out var definitions))
            {
                foreach (var definition in definitions.EnumerateObject())
                {
                    if (definition.Value.TryGetProperty("iconPath", out var path) && path.GetString() is { Length: > 0 } p)
                    {
                        _iconFiles[definition.Name] = p[(p.LastIndexOf('/') + 1)..];
                    }
                }
            }
        }
    }

    /// <summary>Build a catalog straight from manifest JSON (tests; production reads the vendored file).</summary>
    public static FileIconCatalog FromJson(string json) => new(JsonDocument.Parse(json));

    public string FileIconUrl(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (_fileNames.TryGetValue(lower, out var byName))
        {
            return Url(byName);
        }

        // Longest-suffix extension match: "a.spec.ts" tries "spec.ts" before "ts".
        var index = lower.IndexOf('.', StringComparison.Ordinal);
        while (index >= 0 && index + 1 < lower.Length)
        {
            if (_fileExtensions.TryGetValue(lower[(index + 1)..], out var byExtension))
            {
                return Url(byExtension);
            }

            index = lower.IndexOf('.', index + 1);
        }

        return Url(_defaultFile);
    }

    public string FolderIconUrl(string folderName, bool expanded)
    {
        var lower = folderName.ToLowerInvariant();
        var map = expanded ? _folderNamesExpanded : _folderNames;
        return Url(map.TryGetValue(lower, out var icon) ? icon : expanded ? _defaultFolderExpanded : _defaultFolder);
    }

    private string Url(string iconName) =>
        IconBase + (_iconFiles.TryGetValue(iconName, out var file) ? file : iconName + ".svg");

    private static Dictionary<string, string> ToMap(JsonElement root, string property)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty(property, out var element))
        {
            foreach (var entry in element.EnumerateObject())
            {
                if (entry.Value.GetString() is { Length: > 0 } value)
                {
                    map[entry.Name.ToLowerInvariant()] = value;
                }
            }
        }

        return map;
    }

    private static JsonDocument ReadManifest(IWebHostEnvironment environment)
    {
        var file = environment.WebRootFileProvider.GetFileInfo("vendor/material-icons/material-icons.json");
        using var stream = file.CreateReadStream();
        return JsonDocument.Parse(stream);
    }
}
