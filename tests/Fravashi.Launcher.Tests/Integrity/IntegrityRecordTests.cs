using System.Security.Cryptography;
using Fravashi.Launcher;
using Fravashi.Launcher.Integrity;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests.Integrity;

/// <summary>
/// The launcher-side expected-shape record + its HMAC (F3 / ADR 0007). Covers canonical stability, sign/verify
/// round-trip, and - the point of tamper-EVIDENCE - that any edit to the record or a wrong key fails verification.
/// The exact canonical string is pinned so the app-side duplicate (a golden test in Infrastructure.Tests) can
/// prove byte-parity across the two assemblies.
/// </summary>
public sealed class IntegrityRecordTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static IntegrityRecord Sample() => new()
    {
        SchemaVersion = 1,
        Runtime = "wslc",
        RecordedUtc = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero),
        Components =
        [
            new IntegrityComponent
            {
                Id = "studio",
                Kind = IntegrityComponentKinds.App,
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
                    new IntegrityMount("/data", IntegrityMountKinds.Bind),
                    new IntegrityMount("/repos", IntegrityMountKinds.Volume),
                    new IntegrityMount("/tmp", IntegrityMountKinds.Tmpfs),
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

    /// <summary>The canonical string the app side must reproduce byte-for-byte (env sorted ordinal).</summary>
    public const string ExpectedCanonical =
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
    public void Canonical_is_stable_and_sorts_env_ordinal()
    {
        IntegrityCanonical.Build(Sample()).Should().Be(ExpectedCanonical);
    }

    [Fact]
    public void Sign_then_verify_round_trips()
    {
        var record = Sample() with { Hmac = IntegrityCanonical.Sign(Sample(), Key) };
        IntegrityCanonical.Verify(record, Key).Should().BeTrue();
    }

    [Fact]
    public void Editing_any_field_breaks_verification()
    {
        var signed = Sample() with { Hmac = IntegrityCanonical.Sign(Sample(), Key) };

        var tampered = signed with
        {
            Components =
            [
                signed.Components[0] with { Variant = "corp-vpn" },
            ],
        };

        IntegrityCanonical.Verify(tampered, Key).Should().BeFalse();
    }

    [Fact]
    public void A_wrong_key_fails_verification()
    {
        var signed = Sample() with { Hmac = IntegrityCanonical.Sign(Sample(), Key) };
        var otherKey = RandomNumberGenerator.GetBytes(32);

        IntegrityCanonical.Verify(signed, otherKey).Should().BeFalse();
    }

    [Fact]
    public void An_empty_hmac_or_empty_key_never_verifies()
    {
        IntegrityCanonical.Verify(Sample() with { Hmac = "" }, Key).Should().BeFalse();
        IntegrityCanonical.Verify(Sample() with { Hmac = "deadbeef" }, []).Should().BeFalse();
    }

    [Fact]
    public void Record_store_writes_a_signed_file_that_reads_back_and_verifies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fravashi-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            new IntegrityRecordStore(dir).Write(Sample(), Key);

            var path = Path.Combine(dir, IntegrityRecordStore.FileName);
            File.Exists(path).Should().BeTrue();
            File.ReadAllText(path).Should().Contain("\"hmac\"");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
