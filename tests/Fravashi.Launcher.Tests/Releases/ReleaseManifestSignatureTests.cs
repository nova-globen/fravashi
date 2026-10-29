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
/// Backlog 242: the update manifest is used only when its sidecar is a signature by the release key over exactly the
/// bytes that arrived. A test keypair stands in for the release key; the committed public key is held equal to the one
/// fravashi.exe embeds. Each case is driven through the real HTTP source and the real updater, so "refused" is shown as
/// what it must mean: no update state written, no critical lock raised or lifted, nothing installed.
/// </summary>
public sealed class ReleaseManifestSignatureTests : IDisposable
{
    private const string ChannelUrl = "https://example.test/fravashi/updates/release-manifest.json";

    // Where a GitHub release asset's redirect ends: the address names no file, so nothing can be resolved beside it.
    private const string LandedUrl = "https://release-assets.example.test/production-release-asset/1234/5f3c?signed=1";
    private const string SidecarUrl = "https://example.test/fravashi/updates/release-manifest.sig";
    private const string SliceUrl = "https://example/image.tar.part00.gz";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly byte[] ImageBytes = Encoding.UTF8.GetBytes("fake image payload");

    private readonly ECDsa _releaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fravashi-sig-" + Guid.NewGuid().ToString("N"));
    private readonly string _asset = $"fravashi-sig-{Guid.NewGuid():N}.tar.gz";
    private readonly FakeNetworkClock _clock = new();
    private readonly List<string> _log = [];
    private readonly List<string> _requested = [];

    public ReleaseManifestSignatureTests() => Directory.CreateDirectory(_dir);

    private string PublicPem => _releaseKey.ExportSubjectPublicKeyInfoPem();

    private string StatePath => Path.Combine(_dir, UpdateStateStore.FileName);

    private string FailurePath => Path.Combine(_dir, UpdateCheckFailureStore.FileName);

    private const string RecordedCritical =
        """{ "kind": "Critical", "currentVersion": "1.0.0", "targetVersion": "1.5.0", "checkedUtc": "2026-10-07T09:00:00Z", "manifestVerified": true }""";

    private LauncherConfig Config() => new()
    {
        Port = 9099, DataDir = _dir, ContainerName = "fravashi-test", Image = "fravashi:latest", Version = "1.0.0", Variant = "full",
    };

    private byte[] ManifestBytes(bool critical = true, string? sha = null) => JsonSerializer.SerializeToUtf8Bytes(new ReleaseManifest
    {
        Product = "fravashi", Version = "2.0.0", Critical = critical, ReleaseNotesUrl = "https://example/notes",
        Variants =
        [
            new ReleaseVariant
            {
                Name = "full",
                Images =
                [
                    new ReleaseImage
                    {
                        Os = "linux", Arch = "amd64", Asset = _asset, SizeBytes = ImageBytes.Length,
                        Sha256 = sha ?? FileIntegrity.Sha256Hex(ImageBytes), PartUrls = [SliceUrl], ImageRef = "fravashi:2.0.0-full",
                    },
                ],
            },
        ],
    }, Web);

    private static string Sign(ECDsa key, byte[] bytes) =>
        Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) + "\n";

    /// <summary>
    /// The channel as it really is: the manifest URL lands (after the redirect) on a release asset whose address names no
    /// file, the sidecar is asked beside the channel's own address, and the image slice is downloadable. A null sidecar
    /// is a 404.
    /// </summary>
    private HttpReleaseManifestSource Channel(byte[] manifest, string? sidecar, HttpStatusCode sidecarStatus = HttpStatusCode.OK)
    {
        var handler = new ScriptedHttpHandler(new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy)), request =>
        {
            var url = request.RequestUri!.ToString();
            _requested.Add(url);
            if (url == ChannelUrl)
            {
                // What following the channel's redirect looks like to the caller: the answer names where it landed.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(manifest),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, LandedUrl),
                };
            }

            if (url == SidecarUrl)
            {
                return sidecar is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(sidecarStatus) { Content = new StringContent(sidecar) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        return new HttpReleaseManifestSource(ChannelUrl, new HttpClient(handler), _log.Add, PublicPem);
    }

    private (ProductUpdater Updater, FakeContainerRuntime Runtime) Updater(IReleaseManifestSource source)
    {
        var runtime = new FakeContainerRuntime { ContainerPresent = true, ContainerIsRunning = true, ImageDigestAfterLoadValue = "sha256:newimage" };
        var store = new LauncherConfigStore(Path.Combine(_dir, "launcher.json"));
        var slices = new HttpReleaseAssetDownloader(new HttpClient(new ScriptedHttpHandler(
            new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy)),
            request => request.RequestUri!.ToString() == SliceUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageBytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound))));
        var updater = new ProductUpdater(
            source, runtime, slices, store, new StudioContainer(runtime, store, null), _log.Add, arch: "amd64", clock: _clock,
            delay: (_, _) => Task.CompletedTask);
        return (updater, runtime);
    }

    // ---- the key -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_embedded_release_key_is_the_committed_public_key()
    {
        var committed = File.ReadAllText(Path.Combine(RepositoryRoot(), "setup", "release", "manifest-signing-public.pem"));

        using var fromFile = ECDsa.Create();
        fromFile.ImportFromPem(committed);
        using var embedded = ECDsa.Create();
        embedded.ImportFromPem(ReleaseManifestSignature.ReleaseKeyPem);

        embedded.ExportSubjectPublicKeyInfo().Should().Equal(fromFile.ExportSubjectPublicKeyInfo());
        Normalize(ReleaseManifestSignature.ReleaseKeyPem).Should().Be(Normalize(committed));
        fromFile.ExportParameters(false).Curve.Oid.Value.Should().Be(ECCurve.NamedCurves.nistP256.Oid.Value, "the ruling is ECDSA P-256");
        committed.Should().NotContain("PRIVATE", "only the public half is ever committed");
    }

    // ---- the check itself ------------------------------------------------------------------------------------------

    [Fact]
    public void Only_a_signature_by_the_key_over_exactly_these_bytes_verifies()
    {
        var bytes = ManifestBytes();
        var signature = Sign(_releaseKey, bytes);
        var changed = (byte[])bytes.Clone();
        changed[changed.Length / 2] ^= 0x01;

        ReleaseManifestSignature.Verify(bytes, signature, PublicPem).Should().Be(ManifestSignature.Verified);
        ReleaseManifestSignature.Verify(changed, signature, PublicPem).Should().Be(ManifestSignature.BadSignature);
        ReleaseManifestSignature.Verify(bytes, Sign(_otherKey, bytes), PublicPem).Should().Be(ManifestSignature.BadSignature);
        ReleaseManifestSignature.Verify(bytes, null, PublicPem).Should().Be(ManifestSignature.Unsigned);
        ReleaseManifestSignature.Verify(bytes, "not base64 at all!", PublicPem).Should().Be(ManifestSignature.BadSignature);
        ReleaseManifestSignature.Verify(bytes, "", PublicPem).Should().Be(ManifestSignature.BadSignature);
        ReleaseManifestSignature.Verify(bytes, Convert.ToBase64String([0x30, 0x02, 0x01]), PublicPem).Should().Be(ManifestSignature.BadSignature);
    }

    [Fact]
    public void The_release_key_refuses_a_manifest_signed_by_any_other_key()
    {
        // No test key can be the release key, so the embedded key must refuse a test-signed manifest.
        var bytes = ManifestBytes();
        ReleaseManifestSignature.Verify(bytes, Sign(_releaseKey, bytes)).Should().Be(ManifestSignature.BadSignature);
    }

    // ---- through the channel and the updater -------------------------------------------------------------------------

    [Fact]
    public async Task A_correctly_signed_manifest_is_used_and_its_sidecar_is_asked_beside_the_channel_not_where_it_landed()
    {
        var bytes = ManifestBytes(critical: false);
        var (updater, _) = Updater(Channel(bytes, Sign(_releaseKey, bytes)));

        var availability = await updater.EvaluateAsync(Config());

        availability.Kind.Should().Be(UpdateKind.Available);
        availability.TargetVersion.Should().Be("2.0.0");
        availability.Signature.Should().Be(ManifestSignature.Verified);
        _requested.Should().Equal(ChannelUrl, SidecarUrl);
        File.ReadAllText(StatePath).Should().Contain("\"manifestVerified\": true");
        File.Exists(FailurePath).Should().BeFalse();
    }

    public static TheoryData<string, string> Refusals() => new()
    {
        { "one byte changed", "the signature beside it does not match it" },
        { "unsigned", "no signature was published beside it" },
        { "another key", "the signature beside it does not match it" },
    };

    private HttpReleaseManifestSource Refused(string how)
    {
        var bytes = ManifestBytes(critical: true);
        switch (how)
        {
            case "one byte changed":
                var signature = Sign(_releaseKey, bytes);
                var changed = (byte[])bytes.Clone();
                var at = Array.IndexOf(changed, (byte)'2'); // the version 2.0.0, the very field a forger would move
                changed[at] = (byte)'3';
                return Channel(changed, signature);
            case "unsigned":
                return Channel(bytes, null);
            default:
                return Channel(bytes, Sign(_otherKey, bytes));
        }
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_manifest_not_signed_by_the_release_key_is_refused_in_words_and_raises_no_lock_and_installs_nothing(string how, string why)
    {
        var (updater, runtime) = Updater(Refused(how));
        var config = Config();

        var availability = await updater.EvaluateAsync(config);
        var applied = await updater.ApplyAsync(config, availability);

        availability.Kind.Should().Be(UpdateKind.Unknown);
        availability.TargetVersion.Should().BeNull("nothing from the manifest is used, its version included");
        availability.Image.Should().BeNull();
        availability.CouldNotCheck!.Reason.Should().Be(
            $"the update channel's manifest is not signed by this product's release key, so nothing from it is used ({why})");
        File.Exists(StatePath).Should().BeFalse("its critical flag must not reach the state the studio locks on");
        File.ReadAllText(FailurePath).Should().Contain(how == "unsigned" ? "\"signature\": \"unsigned\"" : "\"signature\": \"bad\"");
        applied.Should().BeFalse();
        runtime.LoadedTars.Should().BeEmpty();
        runtime.Stopped.Should().BeEmpty();
        _requested.Should().NotContain(SliceUrl, "no asset a refused manifest names is even downloaded");
        _log.Should().Contain(l => l.Contains("so nothing from it is used"));
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_refused_manifest_does_not_lift_a_lock_the_last_verified_check_recorded_either(string how, string why)
    {
        _ = why;
        File.WriteAllText(StatePath, RecordedCritical);
        var (updater, _) = Updater(Refused(how));

        var availability = await updater.EvaluateAsync(Config());

        File.ReadAllText(StatePath).Should().Be(RecordedCritical);
        availability.CouldNotCheck!.Recorded!.TargetVersion.Should().Be("1.5.0");
    }

    [Fact]
    public async Task A_signature_that_cannot_be_fetched_is_could_not_check_and_not_a_refusal()
    {
        var bytes = ManifestBytes();
        var (updater, _) = Updater(Channel(bytes, "unused", HttpStatusCode.ServiceUnavailable));

        var availability = await updater.EvaluateAsync(Config());

        availability.Kind.Should().Be(UpdateKind.Unknown);
        availability.Signature.Should().BeNull();
        availability.CouldNotCheck!.Reason.Should().Be("its signature answered HTTP 503");
        File.Exists(StatePath).Should().BeFalse();
    }

    [Fact]
    public async Task A_verified_manifest_still_has_each_asset_checked_against_its_hash()
    {
        var bytes = ManifestBytes(critical: false, sha: new string('0', 64));
        var (updater, runtime) = Updater(Channel(bytes, Sign(_releaseKey, bytes)));
        var config = Config();

        var availability = await updater.EvaluateAsync(config);
        var applied = await updater.ApplyAsync(config, availability);

        availability.Signature.Should().Be(ManifestSignature.Verified);
        applied.Should().BeFalse();
        _log.Should().Contain(l => l.Contains("failed its checksum check"));
        runtime.LoadedTars.Should().BeEmpty();
    }

    // ---- what it is said as -------------------------------------------------------------------------------------------

    private LauncherApp App(HttpReleaseManifestSource source)
    {
        var store = new LauncherConfigStore(Path.Combine(_dir, "launcher.json"));
        store.Save(Config() with { Port = 9090, ContainerName = "fravashi", Image = "fravashi:latest" });
        return new LauncherApp(
            new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true },
            store, launcherDirectory: _dir, log: _log.Add, openBrowser: _ => { }, isPortFree: _ => true,
            tray: new FakeTrayControl(), manifestSourceFactory: _ => source,
            confirm: _ => true, defaultDataDir: () => _dir, clock: _clock);
    }

    [Fact]
    public void Status_verb_says_verified_for_a_manifest_signed_by_the_release_key()
    {
        var bytes = ManifestBytes(critical: false);

        App(Channel(bytes, Sign(_releaseKey, bytes))).Status().Should().Be(ExitCodes.Success);

        _log.Should().Contain("  Manifest  : verified - signed by this product's release key");
        _log.Should().Contain(l => l.StartsWith("  Update    : available - 2.0.0", StringComparison.Ordinal));
        _log.Should().NotContain(l => l.Contains("deferred"));
    }

    [Theory]
    [InlineData("unsigned", "no signature was published beside it")]
    [InlineData("another key", "the signature beside it does not match it")]
    public void Status_verb_says_not_used_for_an_unsigned_or_badly_signed_manifest(string how, string why)
    {
        App(Refused(how)).Status().Should().Be(ExitCodes.Success);

        _log.Should().Contain(
            $"  Manifest  : NOT USED - the update channel's manifest is not signed by this product's release key, so nothing from it is used ({why})");
        _log.Should().Contain(l => l.StartsWith(
            "  Update    : unknown - The update channel's manifest is not signed by this product's release key, so nothing from it is used", StringComparison.Ordinal));
        _log.Should().NotContain(l => l.Contains("REQUIRED"), "the refused manifest's critical flag is said nowhere");
    }

    [Theory]
    [InlineData("unsigned", Application.Updates.ManifestSignatureRefusal.Unsigned)]
    [InlineData("another key", Application.Updates.ManifestSignatureRefusal.BadSignature)]
    public async Task The_studio_reads_the_refusal_the_launcher_recorded(string how, Application.Updates.ManifestSignatureRefusal expected)
    {
        await Updater(Refused(how)).Updater.EvaluateAsync(Config());

        var studio = StudioView();

        studio.IsLocked.Should().BeFalse();
        studio.ManifestRefused.Should().Be(expected);
        studio.ManifestVerified.Should().BeFalse();
    }

    [Fact]
    public async Task The_studio_reads_that_the_recorded_state_came_from_a_verified_manifest()
    {
        var bytes = ManifestBytes(critical: false);
        await Updater(Channel(bytes, Sign(_releaseKey, bytes))).Updater.EvaluateAsync(Config());

        var studio = StudioView();

        studio.Severity.Should().Be(Application.Updates.ProductUpdateSeverity.Available);
        studio.ManifestVerified.Should().BeTrue();
        studio.ManifestRefused.Should().BeNull();
    }

    private Application.Updates.ProductUpdateState StudioView() =>
        new Fravashi.Infrastructure.Updates.FileProductUpdateStatus(
            new Fravashi.Infrastructure.Persistence.DataDirectory(_dir),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Fravashi.Infrastructure.Updates.FileProductUpdateStatus>.Instance).Current;

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fravashi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (Fravashi.sln) was not found above the test output.");
    }

    private static string Normalize(string pem) => pem.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    public void Dispose()
    {
        _releaseKey.Dispose();
        _otherKey.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
