using Fravashi.Infrastructure.Integrity;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Integrity;

/// <summary>
/// Pins the APP-side canonical form to the exact same golden string the LAUNCHER side pins
/// (<c>Fravashi.Launcher.Tests.Integrity.IntegrityRecordTests.ExpectedCanonical</c>). If the two ever drift, one of
/// these goldens changes and a record signed by the launcher would stop verifying in the app - this catches that at
/// unit level, before the live cross-container scenario does.
/// </summary>
public sealed class IntegrityCanonicalGoldenTests
{
    private const string ExpectedCanonical =
        "v1\n" +
        "runtime=wslc\n" +
        "recordedUtc=2026-07-17T12:00:00.0000000+00:00\n" +
        "component=studio\n" +
        " kind=app\n" +
        " imageRef=fravashi:latest\n" +
        " imageDigest=sha256:abc\n" +
        " variant=full\n" +
        " version=1.2.0\n" +
        " containerName=fravashi\n" +
        " hostPort=9090\n" +
        " containerPort=8080\n" +
        " loopback=true\n" +
        " nonRootUid=1000\n" +
        " mount=/data|bind\n" +
        " mount=/repos|volume\n" +
        " mount=/tmp|tmpfs\n" +
        " env=FRAVASHI_DATA_DIR=/data\n" +
        " env=FRAVASHI_IN_CONTAINER=1\n" +
        " env=FRAVASHI_VARIANT=full\n";

    [Fact]
    public void App_side_canonical_matches_the_launcher_golden_byte_for_byte()
    {
        var record = new IntegrityRecordModel
        {
            SchemaVersion = 1,
            Runtime = "wslc",
            RecordedUtc = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero),
            Components =
            [
                new IntegrityComponentModel
                {
                    Id = "studio",
                    Kind = "app",
                    ImageRef = "fravashi:latest",
                    ImageDigest = "sha256:abc",
                    Variant = "full",
                    Version = "1.2.0",
                    ContainerName = "fravashi",
                    HostPort = 9090,
                    ContainerPort = 8080,
                    Loopback = true,
                    NonRootUid = 1000,
                    Mounts =
                    [
                        new IntegrityMountModel("/data", "bind"),
                        new IntegrityMountModel("/repos", "volume"),
                        new IntegrityMountModel("/tmp", "tmpfs"),
                    ],
                    Env = new Dictionary<string, string>
                    {
                        ["FRAVASHI_VARIANT"] = "full",
                        ["FRAVASHI_DATA_DIR"] = "/data",
                        ["FRAVASHI_IN_CONTAINER"] = "1",
                    },
                },
            ],
        };

        IntegrityCanonical.Build(record).Should().Be(ExpectedCanonical);
    }
}
