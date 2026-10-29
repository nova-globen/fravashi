using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Infrastructure.AgentRuntimes;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentRuntimes;

/// <summary>
/// Backlog 10: the on-demand Node install, driven against an in-process stand-in for nodejs.org/dist (a
/// tarball the test builds, its hash list, the release index) and a real nvs root under a temp folder.
/// The invariant every failure case checks is the owner's: never half-installed.
/// </summary>
public sealed class NodeProvisionerTests : IDisposable
{
    private const string Version = "22.12.0";
    private static readonly NodeDistributionPlatform Platform = NodeDistributionPlatform.LinuxX64;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fravashi-node-prov", Guid.NewGuid().ToString("N"));
    private readonly string _nvsHome;
    private readonly FakeDistribution _dist = new();

    public NodeProvisionerTests()
    {
        _nvsHome = Path.Combine(_root, "nvs");
        Directory.CreateDirectory(_nvsHome);
        AddRelease(Version);
        _dist.Files["/index.json"] = Encoding.UTF8.GetBytes("""
            [
              {"version":"v23.1.0","files":["linux-x64","linux-arm64"]},
              {"version":"v22.12.0","files":["linux-x64","linux-arm64"]},
              {"version":"v22.9.0","files":["linux-x64"]},
              {"version":"v22.14.0","files":["win-x64"]}
            ]
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---- the stand-in distribution --------------------------------------------------------

    private sealed class FakeDistribution : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public Exception? Fault { get; set; }

        /// <summary>Awaited before a request is answered, so a test can hold one install at a known point.</summary>
        public Func<string, Task>? OnRequest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests)
            {
                Requests.Add(path);
            }

            if (OnRequest is { } hook)
            {
                await hook(path);
            }

            if (Fault is not null)
            {
                throw Fault;
            }

            if (!Files.TryGetValue(path, out var bytes))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentLength = bytes.Length;
            return response;
        }
    }

    private NodeProvisioner Provisioner(bool noPlatform = false, string? nvsHome = null) =>
        new(new HttpClient(_dist) { BaseAddress = new Uri("http://dist.test") }, nvsHome ?? _nvsHome, noPlatform ? null : Platform, "http://dist.test");

    /// <summary>A gzipped tarball shaped like the official one: one top folder, bin/node, a lib file and a link into it.</summary>
    private static byte[] BuildArchive(string version, string? extraEntry = null)
    {
        var top = $"node-v{version}-linux-x64";
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Gnu, leaveOpen: true))
        {
            tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, $"{top}/"));
            tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, $"{top}/bin/"));
            tar.WriteEntry(File(TarEntryType.RegularFile, $"{top}/bin/node", new byte[64 * 1024]));
            tar.WriteEntry(File(TarEntryType.RegularFile, $"{top}/lib/npm-cli.js", "#!/usr/bin/env node\n"u8.ToArray()));
            tar.WriteEntry(new GnuTarEntry(TarEntryType.SymbolicLink, $"{top}/bin/npm") { LinkName = "../lib/npm-cli.js" });
            if (extraEntry is not null)
            {
                tar.WriteEntry(File(TarEntryType.RegularFile, extraEntry, "evil"u8.ToArray()));
            }
        }

        return buffer.ToArray();

        static GnuTarEntry File(TarEntryType type, string name, byte[] content) =>
            new(type, name) { DataStream = new MemoryStream(content) };
    }

    /// <summary>Puts one release (its tarball and its hash list) into the stand-in distribution.</summary>
    private void AddRelease(string version)
    {
        var archive = BuildArchive(version);
        _dist.Files[$"/v{version}/{Platform.ArchiveNameFor(version)}"] = archive;
        _dist.Files[$"/v{version}/SHASUMS256.txt"] = Encoding.ASCII.GetBytes(Shasums(archive, Platform.ArchiveNameFor(version)));
    }

    private static string Shasums(byte[] archive, string fileName) =>
        $"{Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant()}  {fileName}\n"
        + "0000000000000000000000000000000000000000000000000000000000000000  node-v22.12.0-win-x64.zip\n";

    private string InstallDir => Path.Combine(_nvsHome, "node", Version, Platform.Arch);

    private void NothingIsInstalled()
    {
        var nodeRoot = Path.Combine(_nvsHome, "node");
        (Directory.Exists(nodeRoot) ? Directory.EnumerateFileSystemEntries(nodeRoot).ToList() : []).Should().BeEmpty();
        var staging = Path.Combine(_nvsHome, ".staging");
        (Directory.Exists(staging) ? Directory.EnumerateFileSystemEntries(staging).ToList() : []).Should().BeEmpty();
    }

    // ---- success ----------------------------------------------------------------------------

    [Fact]
    public async Task A_partial_request_resolves_through_the_index_and_lands_in_the_layout_the_lookup_reads()
    {
        var reports = new List<NodeProvisionProgress>();
        var progress = new RecordingProgress(reports);

        var result = await Provisioner().InstallAsync("22", progress);

        result.Succeeded.Should().BeTrue(result.Error);
        result.Version.Should().Be(Version);
        result.BinDirectory.Should().Be(Path.Combine(InstallDir, "bin"));
        File.Exists(Path.Combine(InstallDir, "bin", "node")).Should().BeTrue();
        new FileInfo(Path.Combine(InstallDir, "bin", "node")).Length.Should().Be(64 * 1024);
        File.Exists(Path.Combine(InstallDir, "lib", "npm-cli.js")).Should().BeTrue();

        // The lookup the session runner uses serves the install, so the next start leads its PATH with it.
        var lookup = new NodePathProvisioner(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["NVS_HOME"] = _nvsHome }).Build());
        lookup.PathPrefixFor("22").Should().Be(Path.Combine(InstallDir, "bin"));

        // Phases in order, and bytes with a total while downloading.
        reports.Select(r => r.Phase).Distinct().Should().ContainInOrder(
            NodeProvisionPhase.Resolving, NodeProvisionPhase.Downloading, NodeProvisionPhase.Verifying,
            NodeProvisionPhase.Unpacking, NodeProvisionPhase.Installing);
        var lastDownload = reports.Last(r => r.Phase == NodeProvisionPhase.Downloading);
        lastDownload.TotalBytes.Should().Be(_dist.Files[$"/v{Version}/{Platform.ArchiveNameFor(Version)}"].Length);
        lastDownload.BytesReceived.Should().Be(lastDownload.TotalBytes);

        // The staging folder is empty or gone.
        var staging = Path.Combine(_nvsHome, ".staging");
        (Directory.Exists(staging) ? Directory.EnumerateFileSystemEntries(staging).ToList() : []).Should().BeEmpty();
    }

    [Fact]
    public async Task An_exact_request_never_asks_the_index()
    {
        var result = await Provisioner().InstallAsync("v22.12.0");

        result.Succeeded.Should().BeTrue(result.Error);
        _dist.Requests.Should().NotContain("/index.json");
        _dist.Requests.Should().ContainInOrder($"/v{Version}/SHASUMS256.txt", $"/v{Version}/{Platform.ArchiveNameFor(Version)}");
    }

    [Fact]
    public async Task An_already_installed_version_is_a_success_without_a_download()
    {
        Directory.CreateDirectory(Path.Combine(InstallDir, "bin"));

        var result = await Provisioner().InstallAsync(Version);

        result.Succeeded.Should().BeTrue();
        _dist.Requests.Should().BeEmpty();
    }

    // ---- failures, none of them half-installed ------------------------------------------------

    [Fact]
    public async Task A_hash_mismatch_installs_nothing_and_says_so()
    {
        _dist.Files[$"/v{Version}/SHASUMS256.txt"] = Encoding.ASCII.GetBytes(
            $"{new string('a', 64)}  {Platform.ArchiveNameFor(Version)}\n");

        var result = await Provisioner().InstallAsync(Version);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("SHA-256").And.Contain("Nothing was installed");
        NothingIsInstalled();
    }

    [Fact]
    public async Task A_short_hash_in_the_release_list_is_a_mismatch_not_a_crash()
    {
        // The published list is network input, so an entry can be any length at all; the mismatch message
        // used to slice twelve characters off it and throw before the sentence was ever shown.
        _dist.Files[$"/v{Version}/SHASUMS256.txt"] = Encoding.ASCII.GetBytes($"abc  {Platform.ArchiveNameFor(Version)}\n");

        var result = await Provisioner().InstallAsync(Version);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("SHA-256").And.Contain("abc").And.Contain("Nothing was installed");
        NothingIsInstalled();
    }

    [Fact]
    public async Task A_release_whose_hash_list_lacks_this_platform_installs_nothing()
    {
        _dist.Files[$"/v{Version}/SHASUMS256.txt"] = Encoding.ASCII.GetBytes($"{new string('a', 64)}  node-v{Version}-win-x64.zip\n");

        var result = await Provisioner().InstallAsync(Version);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("SHASUMS256.txt");
        NothingIsInstalled();
    }

    [Fact]
    public async Task A_network_failure_installs_nothing_and_carries_the_reason()
    {
        _dist.Fault = new HttpRequestException("No route to host");

        var result = await Provisioner().InstallAsync("22");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("No route to host").And.Contain("Nothing was installed");
        NothingIsInstalled();
    }

    [Fact]
    public async Task A_missing_release_is_a_failure_not_an_exception()
    {
        _dist.Files.Remove($"/v{Version}/SHASUMS256.txt");

        var result = await Provisioner().InstallAsync(Version);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("404");
        NothingIsInstalled();
    }

    [Fact]
    public async Task A_version_the_index_has_no_build_for_is_refused_by_name()
    {
        var result = await Provisioner().InstallAsync("22.14");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("22.14").And.Contain("linux-x64");
        NothingIsInstalled();
    }

    [Fact]
    public async Task An_interrupted_unpack_leaves_nothing_behind()
    {
        // The bytes on the wire are exactly what the hash list promises, so verification passes, and
        // then the archive ends in the middle of an entry: the failure is the unpack's own.
        var whole = _dist.Files[$"/v{Version}/{Platform.ArchiveNameFor(Version)}"];
        var truncated = whole[..(whole.Length / 2)];
        _dist.Files[$"/v{Version}/{Platform.ArchiveNameFor(Version)}"] = truncated;
        _dist.Files[$"/v{Version}/SHASUMS256.txt"] = Encoding.ASCII.GetBytes(Shasums(truncated, Platform.ArchiveNameFor(Version)));
        var reports = new List<NodeProvisionProgress>();

        var result = await Provisioner().InstallAsync(Version, new RecordingProgress(reports));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Nothing was installed");
        reports.Should().Contain(r => r.Phase == NodeProvisionPhase.Unpacking);
        NothingIsInstalled();
    }

    [Fact]
    public async Task An_entry_that_climbs_out_of_the_unpack_folder_is_refused()
    {
        var evil = BuildArchive(Version, extraEntry: "../../escaped.txt");
        _dist.Files[$"/v{Version}/{Platform.ArchiveNameFor(Version)}"] = evil;
        _dist.Files[$"/v{Version}/SHASUMS256.txt"] = Encoding.ASCII.GetBytes(Shasums(evil, Platform.ArchiveNameFor(Version)));

        var result = await Provisioner().InstallAsync(Version);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("outside");
        File.Exists(Path.Combine(_nvsHome, "escaped.txt")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "escaped.txt")).Should().BeFalse();
        NothingIsInstalled();
    }

    [Fact]
    public async Task A_crashed_earlier_install_is_swept_before_the_next_one()
    {
        var leftover = Path.Combine(_nvsHome, ".staging", "deadbeef");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "partial.tar.gz"), "x");

        (await Provisioner().InstallAsync(Version)).Succeeded.Should().BeTrue();

        Directory.Exists(leftover).Should().BeFalse();
    }

    [Fact]
    public async Task Two_installs_of_different_versions_at_once_both_succeed()
    {
        // The coordinator serialises per requested version, not globally, so two workspaces asking for two
        // versions really do install side by side. The sweep used to remove EVERY folder under .staging,
        // including the one the other install was filling, and the victim then failed at its own unpack
        // with a file-not-found that was the studio's doing.
        const string other = "20.11.1";
        AddRelease(other);
        var firstIsStaged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dist.OnRequest = async path =>
        {
            // Hold the first install after it created its staging folder and before it downloads a byte,
            // so the second one's sweep provably runs while the first one's folder is there.
            if (string.Equals(path, $"/v{Version}/SHASUMS256.txt", StringComparison.Ordinal))
            {
                firstIsStaged.TrySetResult();
                await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        };

        var first = Provisioner().InstallAsync(Version);
        await firstIsStaged.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var secondResult = await Provisioner().InstallAsync(other);
        releaseFirst.TrySetResult();
        var firstResult = await first;

        secondResult.Succeeded.Should().BeTrue(secondResult.Error);
        firstResult.Succeeded.Should().BeTrue(firstResult.Error);
        File.Exists(Path.Combine(InstallDir, "bin", "node")).Should().BeTrue();
        File.Exists(Path.Combine(_nvsHome, "node", other, Platform.Arch, "bin", "node")).Should().BeTrue();
        var staging = Path.Combine(_nvsHome, ".staging");
        (Directory.Exists(staging) ? Directory.EnumerateFileSystemEntries(staging).ToList() : []).Should().BeEmpty();
    }

    [Fact]
    public async Task Outside_a_linux_container_the_install_refuses_with_the_reason()
    {
        var result = await Provisioner(noPlatform: true).InstallAsync(Version);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Linux container");
        _dist.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_data_directory_the_install_refuses_with_the_reason()
    {
        var result = await Provisioner(nvsHome: string.Empty).InstallAsync(Version);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("NVS_HOME");
    }

    // ---- the pure parts ------------------------------------------------------------------

    [Fact]
    public void The_index_yields_the_numerically_newest_release_that_ships_the_platform()
    {
        using var index = JsonDocument.Parse(_dist.Files["/index.json"]);

        NodeProvisioner.PickNewest(index.RootElement, "22", "linux-x64").Should().Be("22.12.0");
        NodeProvisioner.PickNewest(index.RootElement, "22", "linux-arm64").Should().Be("22.12.0");
        NodeProvisioner.PickNewest(index.RootElement, "22.9", "linux-x64").Should().Be("22.9.0");
        NodeProvisioner.PickNewest(index.RootElement, "22.14", "linux-x64").Should().BeNull();
        NodeProvisioner.PickNewest(index.RootElement, "2", "linux-x64").Should().BeNull();
    }

    [Fact]
    public void The_hash_list_is_read_by_file_name()
    {
        const string text = "abc  node-v22.12.0-linux-x64.tar.gz\r\ndef *node-v22.12.0-linux-arm64.tar.gz\n";

        NodeProvisioner.ParseShasums(text, "node-v22.12.0-linux-x64.tar.gz").Should().Be("abc");
        NodeProvisioner.ParseShasums(text, "node-v22.12.0-linux-arm64.tar.gz").Should().Be("def");
        NodeProvisioner.ParseShasums(text, "node-v22.12.0-darwin-x64.tar.gz").Should().BeNull();
    }

    [Theory]
    [InlineData("", "(none)")]
    [InlineData("abc", "abc")]
    [InlineData("0123456789ab", "0123456789ab")]
    [InlineData("0123456789abc", "0123456789ab...")]
    public void A_hash_is_shortened_in_a_message_only_when_there_is_something_to_shorten(string hash, string expected) =>
        NodeProvisioner.Abbreviate(hash).Should().Be(expected);

    [Fact]
    public void Detection_refuses_anything_that_is_not_a_linux_container()
    {
        var detected = NodeDistributionPlatform.Detect();
        if (OperatingSystem.IsLinux())
        {
            detected.Should().NotBeNull();
            detected!.Os.Should().Be("linux");
        }
        else
        {
            detected.Should().BeNull();
        }
    }

    private sealed class RecordingProgress(List<NodeProvisionProgress> sink) : IProgress<NodeProvisionProgress>
    {
        public void Report(NodeProvisionProgress value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
