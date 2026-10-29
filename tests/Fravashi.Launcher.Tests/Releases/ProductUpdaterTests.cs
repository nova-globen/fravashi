using System.Text;
using Fravashi.Launcher;
using Fravashi.Launcher.Releases;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests.Releases;

/// <summary>
/// The <c>fravashi</c> product self-update flow (Type B / ADR 0006 / D03-7), driven with a fake manifest source,
/// downloader, and container runtime - no network. Asserts the state write, the sha256 gate, the container swap,
/// the launcher.json version bump, and the honest no-channel/critical classification.
/// </summary>
public sealed class ProductUpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fravashi-updater-" + Guid.NewGuid().ToString("N"));

    public ProductUpdaterTests() => Directory.CreateDirectory(_dir);

    private LauncherConfig Config() => new()
    {
        Port = 9099,
        DataDir = _dir,
        ContainerName = "fravashi-test",
        Image = "fravashi:latest",
        Version = "1.0.0",
        Variant = "full",
    };

    private static readonly byte[] TarBytes = Encoding.UTF8.GetBytes("fake image tar payload");
    private static string TarSha => FileIntegrity.Sha256Hex(TarBytes);

    private static ReleaseManifest Manifest(string version, bool critical = false, string sha = "", string arch = "arm64") => new()
    {
        Product = "fravashi",
        Version = version,
        Critical = critical,
        ReleaseNotesUrl = "https://example/notes",
        Variants =
        [
            new ReleaseVariant
            {
                Name = "full",
                Images =
                [
                    new ReleaseImage
                    {
                        Os = "linux", Arch = arch, Asset = $"fravashi-{version}-full-{arch}.tar",
                        AssetUrl = "https://example/asset.tar", SizeBytes = TarBytes.Length,
                        Sha256 = sha, ImageDigest = "sha256:deadbeef", ImageRef = $"fravashi:{version}-full",
                    },
                ],
            },
        ],
    };

    private (ProductUpdater Updater, FakeContainerRuntime Runtime, LauncherConfigStore Store, List<string> Log) Build(
        ReleaseManifest? manifest, bool downloadOk = true, BridgeLaunch? bridge = null, IReleaseAssetDownloader? downloader = null)
    {
        var runtime = new FakeContainerRuntime { ContainerPresent = true, ContainerIsRunning = true, ImageDigestAfterLoadValue = "sha256:newimage" };
        var store = new LauncherConfigStore(Path.Combine(_dir, "launcher.json"));
        var log = new List<string>();
        var updater = new ProductUpdater(
            new FakeManifestSource(manifest),
            runtime,
            downloader ?? new FakeAssetDownloader(downloadOk ? TarBytes : null),
            store,
            new StudioContainer(runtime, store, bridge),
            log.Add,
            arch: "arm64");
        return (updater, runtime, store, log);
    }

    private string StatePath => Path.Combine(_dir, UpdateStateStore.FileName);

    [Fact]
    public async Task Evaluate_writes_available_state_when_a_newer_version_exists()
    {
        var (updater, _, _, _) = Build(Manifest("1.1.0", sha: TarSha));

        var availability = await updater.EvaluateAsync(Config());

        availability.Kind.Should().Be(UpdateKind.Available);
        availability.TargetVersion.Should().Be("1.1.0");
        File.Exists(StatePath).Should().BeTrue();
        File.ReadAllText(StatePath).Should().Contain("Available").And.Contain("1.1.0");
    }

    [Fact]
    public async Task Evaluate_writes_critical_state_when_the_manifest_is_critical()
    {
        var (updater, _, _, _) = Build(Manifest("2.0.0", critical: true, sha: TarSha));

        var availability = await updater.EvaluateAsync(Config());

        availability.Kind.Should().Be(UpdateKind.Critical);
        File.ReadAllText(StatePath).Should().Contain("Critical");
    }

    [Fact]
    public async Task Evaluate_writes_nothing_when_the_channel_could_not_be_checked()
    {
        // Backlog 230: this test used to assert "None" was written, which is the write that lifted a critical lock.
        var (updater, _, _, _) = Build(manifest: null);

        var availability = await updater.EvaluateAsync(Config());

        availability.Kind.Should().Be(UpdateKind.Unknown);
        File.Exists(StatePath).Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_writes_none_when_the_manifest_offers_nothing_newer()
    {
        var (updater, _, _, _) = Build(Manifest("1.0.0", sha: TarSha));

        var availability = await updater.EvaluateAsync(Config());

        availability.Kind.Should().Be(UpdateKind.None);
        File.ReadAllText(StatePath).Should().Contain("None");
    }

    [Fact]
    public async Task Apply_downloads_verifies_swaps_the_container_and_bumps_the_version()
    {
        var (updater, runtime, store, _) = Build(Manifest("1.1.0", sha: TarSha));
        var config = Config();
        var availability = await updater.EvaluateAsync(config);

        var ok = await updater.ApplyAsync(config, availability);

        ok.Should().BeTrue();
        runtime.LoadedTars.Should().ContainSingle();
        runtime.Stopped.Should().Contain("fravashi-test");
        runtime.Removed.Should().Contain("fravashi-test");
        runtime.RunSpecs.Should().ContainSingle();
        runtime.RunSpecs[0].Image.Should().Be("fravashi:1.1.0-full");
        runtime.RunSpecs[0].HostPort.Should().Be(9099);
        runtime.RunSpecs[0].DataDir.Should().Be(_dir);

        store.Load()!.Version.Should().Be("1.1.0");
        File.ReadAllText(StatePath).Should().Contain("None"); // state cleared after a successful update
    }

    // ---- backlog 160: an update creates the container a start would have created ---------------------------

    [Fact]
    public async Task Apply_in_a_launcher_that_holds_a_bridge_gives_the_new_container_the_bridge_and_the_app_runner()
    {
        // The defect: the updater built its own run spec, so an updated studio had no FRAVASHI_BRIDGE_URL at
        // all. No workspace opened, nothing synced, and no agent session could run until the container was
        // recreated by hand.
        var (updater, runtime, store, _) = Build(
            Manifest("1.1.0", sha: TarSha),
            bridge: new BridgeLaunch("http://172.23.160.1:9091", "bridge-secret", "http://172.23.160.1:9095", "runner-secret"));
        var config = Config();
        var availability = await updater.EvaluateAsync(config);

        (await updater.ApplyAsync(config, availability)).Should().BeTrue();

        var spec = runtime.RunSpecs.Should().ContainSingle().Subject;
        spec.BridgeUrl.Should().Be("http://172.23.160.1:9091");
        spec.BridgeToken.Should().Be("bridge-secret");
        spec.AppRunnerUrl.Should().Be("http://172.23.160.1:9095");
        spec.AppRunnerToken.Should().Be("runner-secret");
        spec.WindowToken.Should().Be(WindowAccessToken.TryRead(_dir), "ADR 0017 still holds after an update");

        // And it records what it baked, so the next start compares against the truth. Fingerprints only.
        var saved = store.Load()!;
        saved.BridgeUrl.Should().Be("http://172.23.160.1:9091");
        saved.BridgeTokenFingerprint.Should().NotBeNullOrWhiteSpace().And.NotContain("bridge-secret");
        saved.AppRunnerBaked.Should().StartWith("http://172.23.160.1:9095|").And.NotContain("runner-secret");
    }

    [Fact]
    public async Task Apply_in_vpn_mode_keeps_the_studio_inside_the_sidecars_netns()
    {
        // The corp-vpn variant's whole point is that egress flows only through the sidecar. A container that
        // does not join its netns publishes its own port and reaches the internet directly, so an update used
        // to remove the structural kill switch without saying anything.
        var (updater, runtime, _, _) = Build(Manifest("1.1.0", sha: TarSha));
        var config = Config() with
        {
            Vpn = new VpnConfig { Enabled = true, ContainerName = "fravashi-vpn", ControlServerPort = 8000 },
        };
        var availability = await updater.EvaluateAsync(config);

        (await updater.ApplyAsync(config, availability)).Should().BeTrue();

        var spec = runtime.RunSpecs.Should().ContainSingle().Subject;
        spec.NetworkContainer.Should().Be("fravashi-vpn");
        spec.VpnControlServerUrl.Should().Be("http://127.0.0.1:8000");
    }

    [Fact]
    public async Task Apply_from_the_console_verb_records_that_it_baked_no_bridge()
    {
        // The console verb holds no bridge. Saving the PREVIOUS container's bridge record is what made this
        // unrecoverable: the next start compared that, saw nothing stale, and started the bridgeless container.
        var (updater, _, store, _) = Build(Manifest("1.1.0", sha: TarSha));
        var config = Config() with { BridgeUrl = "http://172.23.160.1:9091", BridgeTokenFingerprint = "abc123" };
        var availability = await updater.EvaluateAsync(config);

        (await updater.ApplyAsync(config, availability)).Should().BeTrue();

        var saved = store.Load()!;
        saved.BridgeUrl.Should().BeNull();
        saved.BridgeTokenFingerprint.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_refuses_to_load_on_a_checksum_mismatch()
    {
        var (updater, runtime, _, log) = Build(Manifest("1.1.0", sha: "0000000000000000000000000000000000000000000000000000000000000000"));
        var config = Config();
        var availability = await updater.EvaluateAsync(config);

        var ok = await updater.ApplyAsync(config, availability);

        ok.Should().BeFalse();
        runtime.LoadedTars.Should().BeEmpty();
        runtime.RunSpecs.Should().BeEmpty();
        log.Should().Contain(l => l.Contains("checksum"));
    }

    [Fact]
    public async Task Apply_aborts_when_the_download_fails_leaving_the_current_version()
    {
        var (updater, runtime, store, _) = Build(Manifest("1.1.0", sha: TarSha), downloadOk: false);
        var config = Config();
        var availability = await updater.EvaluateAsync(config);

        var ok = await updater.ApplyAsync(config, availability);

        ok.Should().BeFalse();
        runtime.LoadedTars.Should().BeEmpty();
        store.Load().Should().BeNull(); // never saved a new version
    }

    // ---- backlog 157: a release's image is attached as 64 MB slices, so the update joins them ---------------

    private static readonly string[] SliceUrls =
        ["https://example/image.tar.part00.gz", "https://example/image.tar.part01.gz", "https://example/image.tar.part02.gz"];

    /// <summary>The three slices that join into <see cref="TarBytes"/>, keyed by URL.</summary>
    private static Dictionary<string, byte[]?> Slices() => new(StringComparer.Ordinal)
    {
        [SliceUrls[0]] = Encoding.UTF8.GetBytes("fake image "),
        [SliceUrls[1]] = Encoding.UTF8.GetBytes("tar "),
        [SliceUrls[2]] = Encoding.UTF8.GetBytes("payload"),
    };

    /// <summary>The shape release.yml writes: no single asset URL, the slices in order, the WHOLE file's hash.</summary>
    private static ReleaseManifest SlicedManifest(string version) => Manifest(version) with
    {
        Variants =
        [
            new ReleaseVariant
            {
                Name = "full",
                Images =
                [
                    new ReleaseImage
                    {
                        Os = "linux", Arch = "arm64", Asset = $"fravashi-v{version}-image-arm64.tar.gz",
                        SizeBytes = TarBytes.Length, Sha256 = TarSha, PartUrls = SliceUrls,
                    },
                ],
            },
        ],
    };

    [Fact]
    public async Task Apply_joins_a_sliced_image_in_order_and_loads_the_whole_once_its_hash_matches()
    {
        var downloader = new MapDownloader(Slices());
        var (updater, runtime, store, _) = Build(SlicedManifest("1.1.0"), downloader: downloader);
        var config = Config();
        var availability = await updater.EvaluateAsync(config);

        (await updater.ApplyAsync(config, availability)).Should().BeTrue();

        downloader.Requested.Should().Equal(SliceUrls);
        runtime.LoadedBytes.Should().ContainSingle().Which.Should().Equal(TarBytes);
        store.Load()!.Version.Should().Be("1.1.0");
    }

    [Fact]
    public async Task Apply_loads_nothing_when_a_slice_cannot_be_downloaded()
    {
        var slices = Slices();
        slices[SliceUrls[1]] = null;
        var (updater, runtime, store, log) = Build(SlicedManifest("1.1.0"), downloader: new MapDownloader(slices));
        var config = Config();
        var availability = await updater.EvaluateAsync(config);

        (await updater.ApplyAsync(config, availability)).Should().BeFalse();

        runtime.LoadedTars.Should().BeEmpty();
        runtime.RunSpecs.Should().BeEmpty();
        store.Load().Should().BeNull();
        log.Should().Contain(l => l.Contains("current version is unchanged"));
        File.Exists(Path.Combine(Path.GetTempPath(), "fravashi-v1.1.0-image-arm64.tar.gz")).Should().BeFalse(
            "a half-joined image is not left behind");
    }

    [Fact]
    public async Task Apply_refuses_a_sliced_image_whose_join_does_not_match_the_whole_files_hash()
    {
        var slices = Slices();
        slices[SliceUrls[1]] = Encoding.UTF8.GetBytes("TAR ");
        var (updater, runtime, _, log) = Build(SlicedManifest("1.1.0"), downloader: new MapDownloader(slices));
        var config = Config();
        var availability = await updater.EvaluateAsync(config);

        (await updater.ApplyAsync(config, availability)).Should().BeFalse();

        runtime.LoadedTars.Should().BeEmpty();
        log.Should().Contain(l => l.Contains("checksum"));
    }

    [Fact]
    public void The_update_channel_is_the_one_the_infrastructure_hosts()
    {
        // One channel per host, on the build's service host, which redirects to the public repository's
        // GitHub release assets. A plain build is production's; there is no second, older address to fall back to.
        LauncherDefaults.ManifestUrl.Should().Be("https://nova-globen.se/fravashi/updates/release-manifest.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Serves each URL its own bytes (null fails that one) and records the order they were asked for.</summary>
    private sealed class MapDownloader(Dictionary<string, byte[]?> content) : IReleaseAssetDownloader
    {
        public List<string> Requested { get; } = [];

        public Task<AssetDownload> DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
        {
            Requested.Add(url);
            if (!content.TryGetValue(url, out var bytes) || bytes is null)
            {
                return Task.FromResult(AssetDownload.Failed("it answered HTTP 404", retryable: false));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllBytes(destinationPath, bytes);
            return Task.FromResult(AssetDownload.Done(destinationPath));
        }
    }

    /// <summary>A null manifest is a channel that could not be checked (backlog 230).</summary>
    private sealed class FakeManifestSource(ReleaseManifest? manifest) : IReleaseManifestSource
    {
        public Task<ManifestFetch> FetchAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(manifest is null ? ManifestFetch.Failed("name not resolved") : ManifestFetch.Read(manifest));
    }

    private sealed class FakeAssetDownloader(byte[]? content) : IReleaseAssetDownloader
    {
        public Task<AssetDownload> DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
        {
            if (content is null)
            {
                return Task.FromResult(AssetDownload.Failed("it answered HTTP 404", retryable: false));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllBytes(destinationPath, content);
            return Task.FromResult(AssetDownload.Done(destinationPath));
        }
    }
}
