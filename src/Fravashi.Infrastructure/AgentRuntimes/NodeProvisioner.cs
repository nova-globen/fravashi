using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>
/// Backlog 10: fetches an official Node release from nodejs.org/dist and puts it where
/// <see cref="NodePathProvisioner"/> looks (<c>{NVS_HOME}/node/{version}/{arch}/</c>), so the next agent
/// start in a workspace that asked for that version leads its PATH with it.
/// <para>
/// Decisions the owner took and this class keeps: it runs only when the user clicks Install (nothing here
/// is called on a timer or at start), the download is verified against the release's own
/// <c>SHASUMS256.txt</c> before a byte of it is unpacked, and it is never half-installed: everything
/// happens in a staging folder under the nvs root (same volume, so the final step is one rename), and a
/// failure at any point removes the staging folder and leaves the layout as it was. Progress is reported
/// by phase and by bytes so the banner can show something truthful while a 30 MB tarball streams in.
/// </para>
/// <para>
/// A request may be partial ("22" or "22.12"): it is resolved to the newest release in the distribution
/// index that matches AND ships a tarball for this platform. An exact request skips the index.
/// </para>
/// <para>
/// Symbolic links (npm's <c>bin/npm</c> is one) are created on Linux, the only host an agent runs on. On a
/// Windows host creating one needs a privilege the studio never has, so the entry is skipped and counted
/// in the result; the only Windows caller is the opt-in live test, which asserts <c>bin/node</c>.
/// </para>
/// </summary>
public sealed class NodeProvisioner : INodeProvisioner
{
    /// <summary>Configuration key for the distribution base URL (a mirror, or a test's fake).</summary>
    public const string BaseUrlKey = "Node:DistributionBaseUrl";

    /// <summary>The official distribution.</summary>
    public const string DefaultBaseUrl = "https://nodejs.org/dist";

    private const string StagingFolder = ".staging";

    /// <summary>
    /// The staging folders an install is filling right now, by full path. <see cref="SweepStaleStaging"/>
    /// reads it and leaves those alone: the coordinator serialises per REQUESTED VERSION, not globally, so
    /// two workspaces asking for two versions genuinely install side by side, and a sweep that removed
    /// every folder under <see cref="StagingFolder"/> unlinked the other install's folder mid-flight. The
    /// victim then failed at its own download or unpack with a file-not-found that was the studio's doing.
    /// It is static because what is guarded is a folder on disk shared by every provisioner pointed at the
    /// same nvs root (the typed HTTP client hands out a new instance per resolve), not a field of one
    /// instance. Liveness alone decides, with no age threshold: a leftover from a crashed process is in
    /// nobody's live set, so it is still swept on sight rather than after a grace period.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> LiveStaging = new(StringComparer.Ordinal);

    private readonly HttpClient _http;
    private readonly string? _nvsHome;
    private readonly NodeDistributionPlatform? _platform;
    private readonly string _baseUrl;
    private readonly ILogger _logger;

    public NodeProvisioner(
        HttpClient http,
        string? nvsHome,
        NodeDistributionPlatform? platform,
        string? baseUrl = null,
        ILogger<NodeProvisioner>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _nvsHome = nvsHome;
        _platform = platform;
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
        _logger = logger ?? NullLogger<NodeProvisioner>.Instance;
    }

    public async Task<NodeProvisionResult> InstallAsync(
        string requestedVersion,
        IProgress<NodeProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestedVersion))
        {
            return NodeProvisionResult.Failure("No Node version was requested.");
        }

        if (string.IsNullOrWhiteSpace(_nvsHome))
        {
            return NodeProvisionResult.Failure("There is no data directory to install Node into (NVS_HOME and FRAVASHI_DATA_DIR are both unset).");
        }

        if (_platform is null)
        {
            return NodeProvisionResult.Failure(
                $"Node can only be provisioned inside the studio's Linux container; this process runs on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}).");
        }

        var requested = requestedVersion.Trim().TrimStart('v', 'V');
        string? version = null;
        string? staging = null;
        try
        {
            progress?.Report(new NodeProvisionProgress(NodeProvisionPhase.Resolving));
            version = await ResolveVersionAsync(requested, cancellationToken);
            if (version is null)
            {
                return NodeProvisionResult.Failure($"No Node {requested} release ships a {_platform.IndexKey} build.");
            }

            var target = Path.Combine(_nvsHome, "node", version, _platform.Arch);
            if (Directory.Exists(Path.Combine(target, "bin")))
            {
                return new NodeProvisionResult(true, version, Path.Combine(target, "bin"), null);
            }

            // Claimed before it exists and before the sweep runs, so no sweep (this install's own included)
            // can ever remove a folder an install is about to fill.
            staging = Path.GetFullPath(Path.Combine(_nvsHome, StagingFolder, Guid.NewGuid().ToString("N")));
            LiveStaging[staging] = 0;
            Directory.CreateDirectory(staging);
            SweepStaleStaging();

            var expectedHash = await ReadExpectedHashAsync(version, cancellationToken);
            if (expectedHash is null)
            {
                return NodeProvisionResult.Failure(
                    $"The release's SHASUMS256.txt has no entry for {_platform.ArchiveNameFor(version)}.", version);
            }

            var archive = Path.Combine(staging, _platform.ArchiveNameFor(version));
            var actualHash = await DownloadAsync(version, archive, progress, cancellationToken);

            progress?.Report(new NodeProvisionProgress(NodeProvisionPhase.Verifying, Version: version));
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return NodeProvisionResult.Failure(
                    $"The downloaded Node {version} archive does not match its published SHA-256 (expected {Abbreviate(expectedHash)}, got {Abbreviate(actualHash)}). Nothing was installed.",
                    version);
            }

            progress?.Report(new NodeProvisionProgress(NodeProvisionPhase.Unpacking, Version: version));
            var unpackRoot = Path.Combine(staging, "unpack");
            var skippedLinks = Unpack(archive, unpackRoot);
            var unpackedTop = FindUnpackedTop(unpackRoot);
            if (unpackedTop is null || !Directory.Exists(Path.Combine(unpackedTop, "bin")))
            {
                return NodeProvisionResult.Failure($"The Node {version} archive did not contain a bin folder.", version);
            }

            progress?.Report(new NodeProvisionProgress(NodeProvisionPhase.Installing, Version: version));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                Directory.Move(unpackedTop, target);
            }
            catch (IOException) when (Directory.Exists(Path.Combine(target, "bin")))
            {
                // Someone else finished the same install between the check above and this rename.
            }

            var bin = Path.Combine(target, "bin");
            _logger.LogInformation("Provisioned Node {Version} for {Platform} at {Target}.", version, _platform.IndexKey, target);
            return new NodeProvisionResult(true, version, bin, null, skippedLinks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NodeProvisionResult.Failure("The install was cancelled. Nothing was installed.", version);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
            or InvalidDataException or JsonException or UnauthorizedAccessException or FormatException)
        {
            _logger.LogWarning(ex, "Provisioning Node {Requested} failed.", requested);
            var reason = ex is TaskCanceledException ? "the download timed out" : ex.Message;
            return NodeProvisionResult.Failure($"Node {version ?? requested} could not be installed: {reason}. Nothing was installed.", version);
        }
        finally
        {
            // Removed first, given up second: while the folder is still there it stays off limits to a
            // concurrent sweep.
            RemoveQuietly(staging);
            if (staging is not null)
            {
                LiveStaging.TryRemove(staging, out _);
            }
        }
    }

    // ---- resolving --------------------------------------------------------------------------

    private async Task<string?> ResolveVersionAsync(string requested, CancellationToken cancellationToken)
    {
        if (requested.Count(c => c == '.') == 2)
        {
            return requested;
        }

        using var response = await _http.GetAsync($"{_baseUrl}/index.json", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var index = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return PickNewest(index.RootElement, requested, _platform!.IndexKey);
    }

    /// <summary>The newest release matching the prefix that ships this platform's tarball, from the index shape nodejs.org serves.</summary>
    public static string? PickNewest(JsonElement index, string requested, string platformKey)
    {
        if (index.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? best = null;
        var bestOrder = (0, 0, 0);
        foreach (var release in index.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object
                || !release.TryGetProperty("version", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var version = versionElement.GetString()!.TrimStart('v', 'V');
            if (version != requested && !version.StartsWith(requested + ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (!release.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array
                || !files.EnumerateArray().Any(f => f.ValueKind == JsonValueKind.String && f.GetString() == platformKey))
            {
                continue;
            }

            var order = NodeVersionOrder.Parse(version);
            if (best is null || order.CompareTo(bestOrder) > 0)
            {
                best = version;
                bestOrder = order;
            }
        }

        return best;
    }

    // ---- fetching ---------------------------------------------------------------------------

    private async Task<string?> ReadExpectedHashAsync(string version, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"{_baseUrl}/v{version}/SHASUMS256.txt", cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseShasums(text, _platform!.ArchiveNameFor(version));
    }

    /// <summary>The hex digest for one file from a <c>SHASUMS256.txt</c> ("&lt;hex&gt;  &lt;name&gt;" per line), or null.</summary>
    public static string? ParseShasums(string text, string fileName)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var split = line.IndexOf(' ', StringComparison.Ordinal);
            if (split <= 0)
            {
                continue;
            }

            var name = line[(split + 1)..].Trim().TrimStart('*');
            if (string.Equals(name, fileName, StringComparison.Ordinal))
            {
                return line[..split].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// A hash as a message shows it: the first twelve characters when there are more, and the whole of it
    /// when there are not. The expected side comes from a published <c>SHASUMS256.txt</c>, i.e. off the
    /// network, so it can be any length at all and a fixed slice threw before the sentence was ever shown.
    /// </summary>
    public static string Abbreviate(string hash) => hash.Length switch
    {
        0 => "(none)",
        <= 12 => hash,
        _ => hash[..12] + "...",
    };

    /// <summary>Streams the tarball to <paramref name="archive"/>, hashing as it goes; returns the hex SHA-256.</summary>
    private async Task<string> DownloadAsync(
        string version, string archive, IProgress<NodeProvisionProgress>? progress, CancellationToken cancellationToken)
    {
        var url = $"{_baseUrl}/v{version}/{_platform!.ArchiveNameFor(version)}";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        progress?.Report(new NodeProvisionProgress(NodeProvisionPhase.Downloading, 0, total, version));

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[1 << 16];
        long received = 0;
        var lastReported = 0L;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            sha.AppendData(buffer, 0, read);
            received += read;
            // Every 256 KB rather than every read: a report re-renders two circuits.
            if (received - lastReported >= (1 << 18) || received == total)
            {
                lastReported = received;
                progress?.Report(new NodeProvisionProgress(NodeProvisionPhase.Downloading, received, total, version));
            }
        }

        progress?.Report(new NodeProvisionProgress(NodeProvisionPhase.Downloading, received, total ?? received, version));
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    // ---- unpacking --------------------------------------------------------------------------

    /// <summary>
    /// Unpacks the gzipped tarball under <paramref name="root"/>, refusing any entry that would land outside
    /// it (a tarball is untrusted input until its hash matched, and even then a path check costs nothing).
    /// Returns how many symbolic links could not be created.
    /// </summary>
    private int Unpack(string archive, string root)
    {
        Directory.CreateDirectory(root);
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var skippedLinks = 0;

        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip, leaveOpen: true);

        while (tar.GetNextEntry() is { } entry)
        {
            var destination = Path.GetFullPath(Path.Combine(root, entry.Name.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(rootFull, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The archive entry '{entry.Name}' points outside the unpack folder.");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(destination);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: false);
                    break;

                case TarEntryType.SymbolicLink:
                    var linkTarget = Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(destination)!, entry.LinkName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!linkTarget.StartsWith(rootFull, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"The archive link '{entry.Name}' points outside the unpack folder.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    try
                    {
                        // TarEntry.ExtractToFile refuses link entries; the link is made by hand, with the
                        // archive's own relative target so the tree stays relocatable.
                        File.CreateSymbolicLink(destination, entry.LinkName);
                    }
                    catch (Exception ex) when (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                        && ex is IOException or UnauthorizedAccessException)
                    {
                        skippedLinks++;
                    }

                    break;

                default:
                    // Hard links, devices, fifos: the official tarball has none, and none is needed to run node.
                    break;
            }
        }

        return skippedLinks;
    }

    /// <summary>The official tarball wraps everything in one <c>node-v{version}-{os}-{arch}</c> folder.</summary>
    private static string? FindUnpackedTop(string unpackRoot)
    {
        var tops = Directory.EnumerateDirectories(unpackRoot).ToList();
        return tops.Count == 1 ? tops[0] : null;
    }

    // ---- housekeeping -----------------------------------------------------------------------

    /// <summary>
    /// Nothing valid ever OUTLIVES an install under the staging folder: a leftover is a crash mid-install,
    /// and it is swept as the next one starts so a crash cannot fill the volume. A folder an install is
    /// filling right now (<see cref="LiveStaging"/>) is not a leftover and is left alone.
    /// </summary>
    private void SweepStaleStaging()
    {
        var staging = Path.Combine(_nvsHome!, StagingFolder);
        if (!Directory.Exists(staging))
        {
            return;
        }

        foreach (var leftover in Directory.EnumerateDirectories(staging))
        {
            if (LiveStaging.ContainsKey(Path.GetFullPath(leftover)))
            {
                continue;
            }

            RemoveQuietly(leftover);
        }
    }

    private void RemoveQuietly(string? directory)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove the staging folder {Staging}; it is swept on the next install.", directory);
        }
    }
}
