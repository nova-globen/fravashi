using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>
/// Plan 2d, decision 10: the PATH prefix for a project's Node version, served from an nvs layout
/// on the persistent data volume (<c>{NVS_HOME}/node/{version}/{arch}/bin</c>). This class only
/// LOOKS UP provisioned versions; putting one there is <see cref="NodeProvisioner"/>'s job and it
/// runs only when the user clicks Install on the banner (backlog 10), so an agent run never blocks
/// on a download. A missing version means the image Node serves, and since backlog 10 the session
/// pane says so instead of staying silent.
/// </summary>
public sealed class NodePathProvisioner
{
    private readonly string? _nvsHome;

    public NodePathProvisioner(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _nvsHome = configuration["NVS_HOME"];
        if (string.IsNullOrWhiteSpace(_nvsHome))
        {
            var dataDir = configuration["FRAVASHI_DATA_DIR"];
            _nvsHome = string.IsNullOrWhiteSpace(dataDir) ? null : Path.Combine(dataDir, "nvs");
        }
    }

    /// <summary>The nvs root this lookup reads, or null when neither NVS_HOME nor a data dir is configured.</summary>
    public string? NvsHome => _nvsHome;

    /// <summary>
    /// The bin directory of the best provisioned match for <paramref name="nodeVersion"/>
    /// (exact, or the newest one sharing the requested prefix, e.g. "22" matches "22.12.0").
    /// Null when nothing matches: the caller keeps the image Node on PATH.
    /// </summary>
    public string? PathPrefixFor(string? nodeVersion) => Match(nodeVersion)?.Bin;

    /// <summary>The exact provisioned version <see cref="PathPrefixFor"/> would serve, or null.</summary>
    public string? ProvisionedVersionFor(string? nodeVersion) => Match(nodeVersion)?.Version;

    /// <summary>Where an install of <paramref name="version"/> for <paramref name="arch"/> lands (the folder whose <c>bin</c> is served).</summary>
    public string? InstallDirectoryFor(string version, string arch) =>
        string.IsNullOrWhiteSpace(_nvsHome) ? null : Path.Combine(_nvsHome, "node", version.TrimStart('v', 'V'), arch);

    private (string Version, string Bin)? Match(string? nodeVersion)
    {
        if (string.IsNullOrWhiteSpace(nodeVersion) || string.IsNullOrWhiteSpace(_nvsHome))
        {
            return null;
        }

        var nodeRoot = Path.Combine(_nvsHome, "node");
        if (!Directory.Exists(nodeRoot))
        {
            return null;
        }

        var requested = nodeVersion.TrimStart('v', 'V');
        var candidates = Directory.EnumerateDirectories(nodeRoot)
            .Select(Path.GetFileName)
            .Where(name => name is not null
                && (name == requested || name.StartsWith(requested + ".", StringComparison.Ordinal)))
            .OrderByDescending(name => NodeVersionOrder.Parse(name!))
            .ToList();

        foreach (var candidate in candidates)
        {
            var versionDir = Path.Combine(nodeRoot, candidate!);
            var bin = Directory.EnumerateDirectories(versionDir)
                .Select(arch => Path.Combine(arch, "bin"))
                .FirstOrDefault(Directory.Exists);
            if (bin is not null)
            {
                return (candidate!, bin);
            }
        }

        return null;
    }
}

/// <summary>
/// Orders version strings numerically: "22.9.0" sorts below "22.12.0", where an ordinal string sort
/// put 9 above 12 and would have served the older one for a "22" request.
/// </summary>
internal static class NodeVersionOrder
{
    public static (int, int, int) Parse(string version)
    {
        var parts = version.TrimStart('v', 'V').Split('.');
        return (Part(parts, 0), Part(parts, 1), Part(parts, 2));

        static int Part(string[] parts, int index) =>
            index < parts.Length && int.TryParse(parts[index], out var n) ? n : 0;
    }
}
