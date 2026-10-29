using Fravashi.Launcher;
using Fravashi.Launcher.Integrity;
using Fravashi.Launcher.Tests;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests.Integrity;

/// <summary>
/// The launcher's F3 digest pin/verify + expected-shape provisioning on the real <see cref="LauncherApp"/> run
/// path (ADR 0007 #6/#8). Both directions: a first run PINS the digest, records the expected shape, and hands the
/// container the integrity env; a later run whose resolved digest differs from the pinned one REFUSES to start;
/// a runtime that cannot resolve a digest degrades to report-only (no record, no enforcement) rather than blocking.
/// A temp data dir is pre-seeded so nothing touches the real install.
/// </summary>
public sealed class LauncherIntegrityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fravashi-li-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDir;
    private readonly LauncherConfigStore _configStore;

    public LauncherIntegrityTests()
    {
        Directory.CreateDirectory(_dir);
        _dataDir = Path.Combine(_dir, "data");
        Directory.CreateDirectory(_dataDir);
        _configStore = new LauncherConfigStore(Path.Combine(_dir, "launcher.json"));
        // Pre-seed so CreateInitialConfig (which would use the real LocalAppData) is never taken.
        _configStore.Save(new LauncherConfig { Port = 9090, DataDir = _dataDir, ContainerName = "fravashi-test" });
    }

    private LauncherApp Build(FakeContainerRuntime runtime) =>
        new(runtime, _configStore, launcherDirectory: _dir, log: _ => { },
            openBrowser: _ => { }, isPortFree: _ => true,
            manifestSourceFactory: _ => new NoManifestSource());

    private string RecordPath => Path.Combine(_dataDir, IntegrityRecordStore.FileName);

    [Fact]
    public void First_run_pins_the_digest_records_the_shape_and_injects_the_integrity_env()
    {
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, ImageDigestValue = "sha256:pinme" };

        Build(runtime).Run().Should().Be(ExitCodes.Success);

        _configStore.Load()!.PinnedImageDigest.Should().Be("sha256:pinme");
        File.Exists(RecordPath).Should().BeTrue();
        File.Exists(Path.Combine(_dir, IntegrityKeyStore.FileName)).Should().BeTrue();

        var spec = runtime.RunSpecs.Should().ContainSingle().Subject;
        spec.ImageDigest.Should().Be("sha256:pinme");
        spec.IntegrityKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_recorded_shape_verifies_against_the_stored_key()
    {
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, ImageDigestValue = "sha256:pinme" };
        Build(runtime).Run();

        var key = new IntegrityKeyStore(_dir).LoadOrCreate();
        var json = File.ReadAllText(RecordPath);
        var record = System.Text.Json.JsonSerializer.Deserialize<IntegrityRecord>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        IntegrityCanonical.Verify(record, key).Should().BeTrue();
        record.Components.Should().ContainSingle();
        record.Components[0].ImageDigest.Should().Be("sha256:pinme");
        record.Components[0].NonRootUid.Should().Be(1000);
    }

    [Fact]
    public void A_later_run_with_a_mismatched_digest_refuses_to_start()
    {
        // Pin a digest first.
        _configStore.Save(_configStore.Load()! with { PinnedImageDigest = "sha256:original" });
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, ContainerPresent = false, ImageDigestValue = "sha256:TAMPERED" };

        Build(runtime).Run().Should().Be(ExitCodes.IntegrityRefused);
        runtime.RunSpecs.Should().BeEmpty("a digest mismatch must refuse before any container is created");
    }

    [Fact]
    public void A_later_run_with_the_matching_digest_starts_normally()
    {
        _configStore.Save(_configStore.Load()! with { PinnedImageDigest = "sha256:same" });
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, ImageDigestValue = "sha256:same" };

        Build(runtime).Run().Should().Be(ExitCodes.Success);
        runtime.RunSpecs.Should().ContainSingle();
    }

    [Fact]
    public void A_runtime_that_cannot_resolve_a_digest_runs_report_only_without_a_record()
    {
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, ImageDigestValue = null };

        Build(runtime).Run().Should().Be(ExitCodes.Success);

        File.Exists(RecordPath).Should().BeFalse("no digest ⇒ no record ⇒ the app runs report-only");
        var spec = runtime.RunSpecs.Should().ContainSingle().Subject;
        spec.IntegrityKey.Should().BeNull();
        spec.ImageDigest.Should().BeNull();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class NoManifestSource : Fravashi.Launcher.Releases.IReleaseManifestSource
    {
        public Task<Fravashi.Launcher.Releases.ManifestFetch> FetchAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Fravashi.Launcher.Releases.ManifestFetch.Failed("no channel in this test"));
    }
}
