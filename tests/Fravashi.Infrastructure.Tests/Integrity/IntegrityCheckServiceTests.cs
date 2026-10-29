using System.Text.Json;
using Fravashi.Infrastructure.Integrity;
using Fravashi.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Integrity;

/// <summary>
/// The fail-closed integrity <c>BackgroundService</c> (F3 / ADR 0007 #9), BOTH directions: on a genuine mismatch
/// while enforcing it publishes a failed status AND stops the application (the terminate path); on a legitimate
/// shape it publishes all-pass and does NOT stop. The grace is set to 0 so the test does not wait.
/// </summary>
public sealed class IntegrityCheckServiceTests : IDisposable
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fravashi-intsvc-" + Guid.NewGuid().ToString("N"));

    public IntegrityCheckServiceTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("FRAVASHI_INTEGRITY_TERMINATE_GRACE_SECONDS", "0");
    }

    private static IntegrityRecordModel Record() => new()
    {
        SchemaVersion = 1,
        Runtime = "wslc",
        RecordedUtc = DateTimeOffset.UnixEpoch,
        Components =
        [
            new IntegrityComponentModel
            {
                Id = "studio", Kind = "app", ImageRef = "fravashi:latest", ImageDigest = "sha256:good",
                Variant = "full", Version = "1.0.0", ContainerName = "fravashi",
                HostPort = 9090, ContainerPort = 8080, Loopback = true, NonRootUid = 1000,
                Mounts = [new IntegrityMountModel("/data", "bind")],
                Env = new Dictionary<string, string> { ["FRAVASHI_VARIANT"] = "full" },
            },
        ],
    };

    private void WriteSignedRecord()
    {
        var signed = Record() with { Hmac = IntegrityCanonical.Sign(Record(), Key) };
        File.WriteAllText(
            Path.Combine(_dir, FileIntegrityRecordReader.FileName),
            JsonSerializer.Serialize(signed, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private FakeProbe InContainerProbe() => new()
    {
        Env =
        {
            ["FRAVASHI_IN_CONTAINER"] = "1",
            ["FRAVASHI_INTEGRITY_KEY"] = Convert.ToBase64String(Key),
            ["FRAVASHI_IMAGE_DIGEST"] = "sha256:good",
            ["FRAVASHI_VARIANT"] = "full",
            ["ASPNETCORE_HTTP_PORTS"] = "8080",
        },
        Uid = 1000,
        Mounts = { ["/data"] = new MountObservation(true, true, "ext4") },
    };

    private (IntegrityCheckService Service, IntegrityStatusProvider Status, FakeLifetime Lifetime) Build(FakeProbe probe)
    {
        var reader = new FileIntegrityRecordReader(new DataDirectory(_dir), NullLogger<FileIntegrityRecordReader>.Instance);
        var status = new IntegrityStatusProvider();
        var lifetime = new FakeLifetime();
        var service = new IntegrityCheckService(
            reader, probe, status, new FakeEnvironment("Production"), lifetime,
            new Fravashi.Application.Vpn.UnconfiguredVpnConnectivity(),
            NullLogger<IntegrityCheckService>.Instance);
        return (service, status, lifetime);
    }

    [Fact]
    public async Task Legit_shape_publishes_all_pass_and_does_not_stop_the_app()
    {
        WriteSignedRecord();
        var (service, status, lifetime) = Build(InContainerProbe());

        var failed = await service.EvaluateAndMaybeFailClosedAsync(CancellationToken.None);

        failed.Should().BeFalse();
        lifetime.Stopped.Should().BeFalse();
        status.Current.IsFailClosed.Should().BeFalse();
        status.Current.FailCount.Should().Be(0);
    }

    [Fact]
    public async Task A_tampered_shape_fails_closed_and_stops_the_app()
    {
        WriteSignedRecord();
        var probe = InContainerProbe();
        probe.Env["FRAVASHI_VARIANT"] = "corp-vpn"; // the tamper

        var (service, status, lifetime) = Build(probe);

        var failed = await service.EvaluateAndMaybeFailClosedAsync(CancellationToken.None);

        failed.Should().BeTrue();
        lifetime.Stopped.Should().BeTrue("fail-closed must stop the application");
        status.Current.IsFailClosed.Should().BeTrue();
    }

    [Fact]
    public async Task No_record_is_report_only_and_never_stops_the_app()
    {
        // No record written ⇒ unprovisioned ⇒ report-only even though the probe says in-container.
        var (service, status, lifetime) = Build(InContainerProbe());

        var failed = await service.EvaluateAndMaybeFailClosedAsync(CancellationToken.None);

        failed.Should().BeFalse();
        lifetime.Stopped.Should().BeFalse();
        status.Current.Mode.Should().Be(Application.Integrity.IntegrityMode.ReportOnly);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FRAVASHI_INTEGRITY_TERMINATE_GRACE_SECONDS", null);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeProbe : IRuntimeShapeProbe
    {
        public Dictionary<string, string> Env { get; } = new(StringComparer.Ordinal);
        public int? Uid { get; set; }
        public Dictionary<string, MountObservation> Mounts { get; } = new(StringComparer.Ordinal);

        public string? GetEnv(string name) => Env.TryGetValue(name, out var v) ? v : null;
        public int? TryGetEffectiveUid() => Uid;
        public MountObservation ObserveMount(string path) =>
            Mounts.TryGetValue(path, out var m) ? m : new MountObservation(false, false, null);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopped = true;
    }

    private sealed class FakeEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Fravashi";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
