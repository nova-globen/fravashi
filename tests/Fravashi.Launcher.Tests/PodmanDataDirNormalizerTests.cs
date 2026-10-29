using System.Text;
using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Covers the host-side data-dir normalization the launcher runs for rootless Podman on Windows (the fix for
/// the "attempt to write a readonly database" crash): every file under the data dir is rewritten so it loses the
/// baked WSL/DrvFs ownership metadata a non-root container cannot otherwise write past. The $LX-attribute
/// stripping itself is a Windows/9p behaviour that cannot be reproduced on a plain test host, so these tests
/// pin the observable contract: content is preserved exactly, the run is marker-gated and idempotent, and
/// nested files are covered.
/// </summary>
public sealed class PodmanDataDirNormalizerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ddnorm-test-" + Guid.NewGuid().ToString("N"));

    public PodmanDataDirNormalizerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Normalize_RewritesTheDbAndKeyRing_PreservingContentExactly_AndCountsThem()
    {
        var dbBytes = Encoding.UTF8.GetBytes("SQLite format 3\0the-database-bytes");
        File.WriteAllBytes(Path.Combine(_dir, "fravashi.db"), dbBytes);
        File.WriteAllText(Path.Combine(_dir, "fravashi.db-wal"), "wal");
        Directory.CreateDirectory(Path.Combine(_dir, "keys"));
        File.WriteAllText(Path.Combine(_dir, "keys", "key-1.xml"), "<key>secret</key>");

        var rewritten = PodmanDataDirNormalizer.Normalize(_dir);

        rewritten.Should().Be(3); // fravashi.db + fravashi.db-wal (top level) + keys/key-1.xml
        File.ReadAllBytes(Path.Combine(_dir, "fravashi.db")).Should().Equal(dbBytes);
        File.ReadAllText(Path.Combine(_dir, "keys", "key-1.xml")).Should().Be("<key>secret</key>");
    }

    [Fact]
    public void Normalize_LeavesToolManagedSubtreesAlone_SoAgentCliJunkIsNotRewritten()
    {
        File.WriteAllText(Path.Combine(_dir, "fravashi.db"), "db");
        // HOME=/data/home and the npm prefix are filled by the agent CLIs with files we must not touch.
        Directory.CreateDirectory(Path.Combine(_dir, "home", ".codex"));
        File.WriteAllText(Path.Combine(_dir, "home", ".codex", "config.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_dir, ".npm-global"));
        File.WriteAllText(Path.Combine(_dir, ".npm-global", "marker"), "x");

        // Only the top-level DB is rewritten; the tool subtrees are skipped entirely.
        PodmanDataDirNormalizer.Normalize(_dir).Should().Be(1);
    }

    [Fact]
    public void Normalize_WritesTheMarker_AndAlreadyNormalizedThenReportsTrue()
    {
        PodmanDataDirNormalizer.AlreadyNormalized(_dir).Should().BeFalse();

        PodmanDataDirNormalizer.Normalize(_dir);

        File.Exists(Path.Combine(_dir, PodmanDataDirNormalizer.MarkerFileName)).Should().BeTrue();
        PodmanDataDirNormalizer.AlreadyNormalized(_dir).Should().BeTrue();
    }

    [Fact]
    public void Normalize_DoesNotRewriteItsOwnMarker()
    {
        File.WriteAllText(Path.Combine(_dir, "fravashi.db"), "db");

        // First pass rewrites the one data file and drops the marker.
        PodmanDataDirNormalizer.Normalize(_dir).Should().Be(1);

        // With the data file gone, only the marker remains - a further pass rewrites nothing (the marker is
        // skipped, never counted), proving the marker itself is never rewritten.
        File.Delete(Path.Combine(_dir, "fravashi.db"));
        PodmanDataDirNormalizer.Normalize(_dir).Should().Be(0);
    }

    [Fact]
    public void Normalize_OnMissingDirectory_IsANoOp()
    {
        var missing = Path.Combine(_dir, "does-not-exist");

        PodmanDataDirNormalizer.Normalize(missing).Should().Be(0);
    }
}
