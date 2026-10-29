using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fravashi.Launcher;
using Fravashi.Launcher.Releases;
using Fravashi.Testing.Network;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests.Releases;

/// <summary>
/// Backlog 230 (slice C of 219, plan @56): an update channel that could not be asked is never "nothing newer", a
/// critical flag only a successful check can set or clear, and a broken download is retried and never installed.
/// Every network here is the scripted one of backlog 228, on a fake clock; nothing waits in real time.
/// </summary>
public sealed class UpdateChannelOutageTests : IDisposable
{
    private const string ManifestUrl = "https://example.test/fravashi/updates/release-manifest.json";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fravashi-outage-" + Guid.NewGuid().ToString("N"));
    private readonly string _asset = $"fravashi-outage-{Guid.NewGuid():N}.tar.gz";
    private readonly FakeNetworkClock _clock = new();
    private readonly List<TimeSpan> _waits = [];
    private readonly List<string> _log = [];

    public UpdateChannelOutageTests() => Directory.CreateDirectory(_dir);

    private string StatePath => Path.Combine(_dir, UpdateStateStore.FileName);

    private string FailurePath => Path.Combine(_dir, UpdateCheckFailureStore.FileName);

    private LauncherConfig Config() => new()
    {
        Port = 9099, DataDir = _dir, ContainerName = "fravashi-test", Image = "fravashi:latest", Version = "1.0.0", Variant = "full",
    };

    private const string Critical =
        """{ "kind": "Critical", "currentVersion": "1.0.0", "targetVersion": "2.0.0", "checkedUtc": "2026-10-07T09:00:00Z" }""";

    private const string Available =
        """{ "kind": "Available", "currentVersion": "1.0.0", "targetVersion": "1.1.0", "checkedUtc": "2026-10-07T09:00:00Z" }""";

    private static readonly byte[][] SliceBytes =
        [Encoding.UTF8.GetBytes("fake image "), Encoding.UTF8.GetBytes("tar "), Encoding.UTF8.GetBytes("payload")];

    private static readonly byte[] Whole = [.. SliceBytes.SelectMany(b => b)];

    private static readonly string[] SliceUrls =
        ["https://example/image.tar.part00.gz", "https://example/image.tar.part01.gz", "https://example/image.tar.part02.gz"];

    private ReleaseManifest Manifest(string version, bool critical = false, string? sha = null) => new()
    {
        Product = "fravashi", Version = version, Critical = critical, ReleaseNotesUrl = "https://example/notes",
        Variants =
        [
            new ReleaseVariant
            {
                Name = "full",
                Images =
                [
                    new ReleaseImage
                    {
                        Os = "linux", Arch = "amd64", Asset = _asset, SizeBytes = Whole.Length,
                        Sha256 = sha ?? FileIntegrity.Sha256Hex(Whole), PartUrls = SliceUrls, ImageRef = $"fravashi:{version}-full",
                    },
                ],
            },
        ],
    };

    /// <summary>A stand-in for the release key (backlog 242): every healthy manifest here is signed with it.</summary>
    private static readonly ECDsa TestReleaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>
    /// The real manifest source over the scripted network; a healthy call is answered with <paramref name="healthy"/>,
    /// and its signature beside it (backlog 242) is a signature by <see cref="TestReleaseKey"/> over those bytes, so what
    /// these tests exercise is the outage and never the signature.
    /// </summary>
    private static (HttpReleaseManifestSource Source, ScriptedHttpHandler Handler) Channel(NetworkScript script, Func<HttpResponseMessage> healthy)
    {
        var handler = new ScriptedHttpHandler(script, request =>
        {
            var answer = healthy();
            if (!request.RequestUri!.AbsolutePath.EndsWith("/" + ReleaseManifestSignature.SidecarName, StringComparison.Ordinal)
                || !answer.IsSuccessStatusCode)
            {
                return answer;
            }

            var bytes = answer.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var signature = TestReleaseKey.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Convert.ToBase64String(signature)) };
        });
        return (new HttpReleaseManifestSource(ManifestUrl, new HttpClient(handler), publicKeyPem: TestReleaseKey.ExportSubjectPublicKeyInfoPem()), handler);
    }

    private static HttpResponseMessage Json(ReleaseManifest manifest) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(manifest, Web)) };

    /// <summary>Serves each slice URL its bytes when the scripted network lets the call through.</summary>
    private static HttpResponseMessage Slice(HttpRequestMessage request)
    {
        var index = Array.IndexOf(SliceUrls, request.RequestUri!.ToString());
        return index < 0
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(SliceBytes[index]) };
    }

    private (ProductUpdater Updater, FakeContainerRuntime Runtime) Updater(IReleaseManifestSource source, IReleaseAssetDownloader? downloader = null)
    {
        var runtime = new FakeContainerRuntime { ContainerPresent = true, ContainerIsRunning = true, ImageDigestAfterLoadValue = "sha256:newimage" };
        var store = new LauncherConfigStore(Path.Combine(_dir, "launcher.json"));
        var updater = new ProductUpdater(
            source, runtime, downloader ?? new HttpReleaseAssetDownloader(new HttpClient(new ScriptedHttpHandler(new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy)), Slice))),
            store, new StudioContainer(runtime, store, null), _log.Add, arch: "amd64", clock: _clock,
            delay: (wait, _) =>
            {
                _waits.Add(wait);
                _clock.Advance(wait);
                return Task.CompletedTask;
            });
        return (updater, runtime);
    }

    private string Local(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, _clock.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);

    // ---- the fail-open -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_check_does_not_lift_a_recorded_critical_update()
    {
        // Red against the code before backlog 230: ProductUpdater wrote "None" here, which the studio reads as unlocked.
        File.WriteAllText(StatePath, Critical);
        var (source, _) = Channel(NetworkScript.F1(_clock), () => Json(Manifest("1.0.0")));
        var (updater, _) = Updater(source);

        await updater.EvaluateAsync(Config());

        File.ReadAllText(StatePath).Should().Be(Critical, "a check that could not reach the channel knows nothing newer than the last good one");
    }

    // ---- "could not check", with its reason and since when ------------------------------------------------------

    public static TheoryData<string, string> Faults() => new()
    {
        { "F1", "name not resolved" },
        { "F2", "no answer came back, and the request may have arrived" },
        { "F3", "no answer came back, and the request may have arrived" },
        { "F4", "no answer came back, and the request may have arrived" },
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task An_unreachable_channel_is_could_not_check_with_its_reason_and_leaves_a_recorded_offer_alone(string fault, string reason)
    {
        File.WriteAllText(StatePath, Available);
        var script = fault switch
        {
            "F1" => NetworkScript.F1(_clock),
            "F2" => NetworkScript.F2(_clock),
            "F3" => NetworkScript.F3(_clock),
            _ => NetworkScript.F4(_clock),
        };
        var startedAt = _clock.GetUtcNow();
        var (source, _) = Channel(script, () => Json(Manifest("1.1.0")));
        var (updater, _) = Updater(source);

        var availability = await updater.EvaluateAsync(Config());

        availability.Kind.Should().Be(UpdateKind.Unknown);
        availability.CouldNotCheck!.Reason.Should().Be(reason);
        availability.CouldNotCheck.FailingSince.Should().Be(startedAt);
        availability.CouldNotCheck.Recorded!.TargetVersion.Should().Be("1.1.0");
        File.ReadAllText(StatePath).Should().Be(Available);
        File.ReadAllText(FailurePath).Should().Contain(reason);
    }

    [Fact]
    public async Task A_refusing_or_garbled_channel_is_could_not_check_too_never_nothing_newer()
    {
        File.WriteAllText(StatePath, Critical);
        var healthy = new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy));

        var (unavailable, _) = Channel(healthy, () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var first = await Updater(unavailable).Updater.EvaluateAsync(Config());
        var (garbled, _) = Channel(healthy, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>captive portal</html>") });
        var second = await Updater(garbled).Updater.EvaluateAsync(Config());

        first.CouldNotCheck!.Reason.Should().Be("it answered HTTP 503");
        second.CouldNotCheck!.Reason.Should().Be("its manifest could not be read");
        File.ReadAllText(StatePath).Should().Be(Critical);
    }

    [Fact]
    public async Task The_failing_since_moment_is_kept_across_failed_checks_and_cleared_by_an_answer()
    {
        // The link goes down with a check in flight (it hangs for the client's whole timeout), stays down, then returns.
        var (source, _) = Channel(
            new NetworkScript(_clock,
                new NetworkPhase(NetworkBehaviour.Hangs, Calls: 1),
                new NetworkPhase(NetworkBehaviour.NoRoute, For: TimeSpan.FromMinutes(10)),
                new NetworkPhase(NetworkBehaviour.Healthy)),
            () => Json(Manifest("1.0.0")));
        var (updater, _) = Updater(source);
        var startedAt = _clock.GetUtcNow();

        var hung = await updater.EvaluateAsync(Config());
        _clock.Advance(TimeSpan.FromSeconds(5));
        var noRoute = await updater.EvaluateAsync(Config());

        hung.CouldNotCheck!.FailingSince.Should().Be(startedAt, "a hung check failed when it began, not when it gave up");
        noRoute.CouldNotCheck!.Reason.Should().Be("no network route");
        noRoute.CouldNotCheck.FailingSince.Should().Be(startedAt, "since when is the FIRST failure");

        _clock.Advance(TimeSpan.FromMinutes(10));
        var answered = await updater.EvaluateAsync(Config());

        answered.Kind.Should().Be(UpdateKind.None);
        File.Exists(FailurePath).Should().BeFalse();
    }

    // ---- a critical flag only a successful check sets or clears ----------------------------------------------------

    [Fact]
    public async Task A_critical_flag_survives_any_number_of_failed_checks_and_clears_only_when_a_good_check_says_so()
    {
        File.WriteAllText(StatePath, Critical);
        // A fresh answer per call: the manifest and its signature beside it (backlog 242) are two requests.
        var healthy = Manifest("2.0.0", critical: true);
        var (source, _) = Channel(
            new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.NoRoute, For: TimeSpan.FromDays(10.5)), new NetworkPhase(NetworkBehaviour.Healthy)),
            () => Json(healthy));
        var (updater, _) = Updater(source);

        for (var day = 0; day < 10; day++)
        {
            (await updater.EvaluateAsync(Config())).Kind.Should().Be(UpdateKind.Unknown);
            File.ReadAllText(StatePath).Should().Be(Critical, $"failed check {day + 1} must not touch it");
            _clock.Advance(TimeSpan.FromDays(1));
        }

        // Eleven days on, the channel answers and still says critical: it stays.
        _clock.Advance(TimeSpan.FromDays(1));
        (await updater.EvaluateAsync(Config())).Kind.Should().Be(UpdateKind.Critical);
        File.ReadAllText(StatePath).Should().Contain("Critical");

        // Then a good check that finds nothing newer for this install clears it.
        healthy = Manifest("1.0.0");
        (await updater.EvaluateAsync(Config())).Kind.Should().Be(UpdateKind.None);
        File.ReadAllText(StatePath).Should().Contain("\"kind\": \"None\"");
    }

    [Fact]
    public async Task A_failed_check_never_sets_a_flag_either()
    {
        var (source, _) = Channel(NetworkScript.F1(_clock), () => Json(Manifest("2.0.0", critical: true)));

        await Updater(source).Updater.EvaluateAsync(Config());

        File.Exists(StatePath).Should().BeFalse();
    }

    [Fact]
    public async Task The_studio_reads_what_the_launcher_wrote_a_kept_lock_and_the_failed_check_beside_it()
    {
        // The two halves of the file contract, writer to reader, the way the backup round trip is held.
        File.WriteAllText(StatePath, Critical);
        var startedAt = _clock.GetUtcNow();
        var (source, _) = Channel(NetworkScript.F1(_clock), () => Json(Manifest("2.0.0", critical: true)));
        await Updater(source).Updater.EvaluateAsync(Config());

        var studio = new Fravashi.Infrastructure.Updates.FileProductUpdateStatus(
            new Fravashi.Infrastructure.Persistence.DataDirectory(_dir),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Fravashi.Infrastructure.Updates.FileProductUpdateStatus>.Instance).Current;

        studio.IsLocked.Should().BeTrue();
        studio.ChannelUnreachableSince.Should().Be(startedAt);
        studio.ChannelUnreachableReason.Should().Be("name not resolved");
    }

    // ---- what it is said as -------------------------------------------------------------------------------------

    private LauncherApp App(ReleaseManifest manifest, NetworkScript script, FakeContainerRuntime runtime)
    {
        var store = new LauncherConfigStore(Path.Combine(_dir, "launcher.json"));
        store.Save(Config() with { Port = 9090, ContainerName = "fravashi", Image = "fravashi:latest" });
        return new LauncherApp(
            runtime, store, launcherDirectory: _dir, log: _log.Add, openBrowser: _ => { }, isPortFree: _ => true,
            tray: new FakeTrayControl(),
            manifestSourceFactory: _ => Channel(script, () => Json(manifest)).Source,
            assetDownloader: new HttpReleaseAssetDownloader(new HttpClient(new ScriptedHttpHandler(script, Slice))),
            confirm: _ => true, defaultDataDir: () => _dir, clock: _clock);
    }

    [Fact]
    public void The_upgrade_operation_says_unknown_with_the_reason_and_since_when_never_newest()
    {
        File.WriteAllText(StatePath, Critical);
        var startedAt = _clock.GetUtcNow();
        var app = App(Manifest("2.0.0", critical: true), NetworkScript.F1(_clock), new FakeContainerRuntime { Available = true });

        var (installed, offered, critical, said) = app.CheckForUpgrade();

        installed.Should().Be("1.0.0");
        offered.Should().BeNull("nothing can be installed without a manifest");
        critical.Should().BeFalse();
        said.Should().StartWith($"Could not check the update channel since {Local(startedAt)} (name not resolved). ")
            .And.Contain("Fravashi 1.0.0 is installed; whether a newer one exists is unknown.")
            .And.Contain("found a REQUIRED update to 2.0.0")
            .And.NotContain("newest");
        app.ApplyUpgrade("2.0.0").Should().Be((false, said!));
    }

    [Fact]
    public void Status_verb_and_fravashi_update_say_unknown_never_up_to_date()
    {
        var runtime = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true };
        var app = App(Manifest("1.1.0"), NetworkScript.F1(_clock), runtime);

        app.Status().Should().Be(ExitCodes.Success);
        app.Update().Should().Be(ExitCodes.UpdateChannelUnreachable);

        _log.Should().Contain(l => l.Contains("Update    : unknown - Could not check the update channel since"));
        _log.Should().Contain(l => l.StartsWith("Could not check the update channel since", StringComparison.Ordinal));
        _log.Should().NotContain(l => l.Contains("up to date"));
        runtime.LoadedTars.Should().BeEmpty();
    }

    // ---- downloads: retried, and never installed broken ---------------------------------------------------------

    private async Task<(bool Applied, FakeContainerRuntime Runtime)> ApplyAsync(ReleaseManifest manifest, IReleaseAssetDownloader downloader)
    {
        var source = new FixedSource(manifest);
        var (updater, runtime) = Updater(source, downloader);
        var config = Config();
        var availability = await updater.EvaluateAsync(config);
        return (await updater.ApplyAsync(config, availability), runtime);
    }

    private void NothingLeftInTemp()
    {
        File.Exists(Path.Combine(Path.GetTempPath(), _asset)).Should().BeFalse("a half-joined image is not left behind");
        Directory.Exists(Path.Combine(Path.GetTempPath(), _asset + ".slices")).Should().BeFalse();
    }

    [Fact]
    public async Task A_slice_reset_once_is_retried_and_the_slices_already_joined_are_kept()
    {
        var handler = new ScriptedHttpHandler(
            new NetworkScript(_clock,
                new NetworkPhase(NetworkBehaviour.Healthy, Calls: 1),
                new NetworkPhase(NetworkBehaviour.ResetAfterWrite, Calls: 1),
                new NetworkPhase(NetworkBehaviour.Healthy)),
            Slice);

        var (applied, runtime) = await ApplyAsync(Manifest("1.1.0"), new HttpReleaseAssetDownloader(new HttpClient(handler)));

        applied.Should().BeTrue();
        runtime.LoadedBytes.Should().ContainSingle().Which.Should().Equal(Whole);
        handler.Journal.Select(c => c.Uri!.ToString()).Should().Equal(SliceUrls[0], SliceUrls[1], SliceUrls[1], SliceUrls[2]);
        _waits.Should().Equal(ProductUpdater.FirstRetryWait);
        NothingLeftInTemp();
    }

    [Fact]
    public async Task A_slice_that_fails_three_times_fails_the_apply_with_nothing_swapped_or_left()
    {
        var handler = new ScriptedHttpHandler(
            new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy, Calls: 1), new NetworkPhase(NetworkBehaviour.NoRoute)),
            Slice);

        var (applied, runtime) = await ApplyAsync(Manifest("1.1.0"), new HttpReleaseAssetDownloader(new HttpClient(handler)));

        applied.Should().BeFalse();
        handler.Journal.Should().HaveCount(1 + ProductUpdater.DownloadAttempts);
        _waits.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        runtime.LoadedTars.Should().BeEmpty();
        runtime.Stopped.Should().BeEmpty();
        runtime.RunSpecs.Should().BeEmpty();
        _log.Should().Contain(l => l.Contains("part 2 of 3: no network route") && l.Contains("current version is unchanged"));
        NothingLeftInTemp();
    }

    [Fact]
    public async Task A_refused_slice_is_not_retried()
    {
        var urls = new[] { SliceUrls[0], "https://example/missing.part01.gz" };
        var manifest = Manifest("1.1.0") with
        {
            Variants = [new ReleaseVariant { Name = "full", Images = [Manifest("1.1.0").Variants[0].Images[0] with { PartUrls = urls }] }],
        };
        var handler = new ScriptedHttpHandler(new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy)), Slice);

        var (applied, _) = await ApplyAsync(manifest, new HttpReleaseAssetDownloader(new HttpClient(handler)));

        applied.Should().BeFalse();
        handler.Journal.Should().HaveCount(2);
        _waits.Should().BeEmpty();
        NothingLeftInTemp();
    }

    [Fact]
    public async Task A_short_body_is_refused_by_the_downloader_and_never_installed()
    {
        // The server says 100 bytes and sends 4: what a connection cut mid-body looks like when nothing throws.
        var handler = new ScriptedHttpHandler(new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy)), request =>
        {
            var response = Slice(request);
            if (request.RequestUri!.ToString() == SliceUrls[1])
            {
                response.Content.Headers.ContentLength = 100;
            }

            return response;
        });

        var (applied, runtime) = await ApplyAsync(Manifest("1.1.0"), new HttpReleaseAssetDownloader(new HttpClient(handler)));

        applied.Should().BeFalse();
        _log.Should().Contain(l => l.Contains("only 4 of 100 bytes arrived"));
        runtime.LoadedTars.Should().BeEmpty();
        runtime.Stopped.Should().BeEmpty();
        NothingLeftInTemp();
    }

    [Fact]
    public async Task A_truncated_join_that_claims_success_or_a_wrong_hash_is_refused_before_anything_swaps()
    {
        var (truncated, truncatedRuntime) = await ApplyAsync(Manifest("1.1.0"), new ClaimsSuccess(bytes => bytes[..^2]));
        var (wrongHash, wrongRuntime) = await ApplyAsync(Manifest("1.1.0", sha: new string('0', 64)), new ClaimsSuccess(bytes => bytes));

        truncated.Should().BeFalse();
        wrongHash.Should().BeFalse();
        foreach (var runtime in new[] { truncatedRuntime, wrongRuntime })
        {
            runtime.LoadedTars.Should().BeEmpty();
            runtime.Stopped.Should().BeEmpty();
            runtime.Removed.Should().BeEmpty();
            runtime.RunSpecs.Should().BeEmpty();
        }

        _log.Where(l => l.Contains("failed its checksum check")).Should().HaveCount(2);
        NothingLeftInTemp();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FixedSource(ReleaseManifest manifest) : IReleaseManifestSource
    {
        public Task<ManifestFetch> FetchAsync(CancellationToken cancellationToken = default) => Task.FromResult(ManifestFetch.Read(manifest));
    }

    /// <summary>A downloader that reports every slice as whole, after <paramref name="mangle"/> has had its way with it.</summary>
    private sealed class ClaimsSuccess(Func<byte[], byte[]> mangle) : IReleaseAssetDownloader
    {
        public Task<AssetDownload> DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllBytes(destinationPath, mangle(SliceBytes[Array.IndexOf(SliceUrls, url)]));
            return Task.FromResult(AssetDownload.Done(destinationPath));
        }
    }
}
