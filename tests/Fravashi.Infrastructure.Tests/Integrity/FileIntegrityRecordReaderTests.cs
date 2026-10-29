using Fravashi.Infrastructure.Integrity;
using Fravashi.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Integrity;

/// <summary>The app-side reader of the launcher-written integrity record: parses a real file, and degrades to null
/// (⇒ report-only) on an absent or malformed file rather than crashing.</summary>
public sealed class FileIntegrityRecordReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fravashi-integrec-" + Guid.NewGuid().ToString("N"));

    public FileIntegrityRecordReaderTests() => Directory.CreateDirectory(_dir);

    private FileIntegrityRecordReader Reader() =>
        new(new DataDirectory(_dir), NullLogger<FileIntegrityRecordReader>.Instance);

    private void Write(string json) => File.WriteAllText(Path.Combine(_dir, FileIntegrityRecordReader.FileName), json);

    [Fact]
    public void Absent_file_reads_as_null()
    {
        Reader().Read().Should().BeNull();
    }

    [Fact]
    public void Malformed_file_reads_as_null()
    {
        Write("{ not json");
        Reader().Read().Should().BeNull();
    }

    [Fact]
    public void A_valid_record_parses_its_components_and_hmac()
    {
        Write("""
        {
          "schemaVersion": 1,
          "runtime": "wslc",
          "recordedUtc": "2026-07-17T12:00:00+00:00",
          "components": [
            {
              "id": "studio", "kind": "app", "imageRef": "fravashi:latest", "imageDigest": "sha256:x",
              "variant": "full", "version": "1.0.0", "containerName": "fravashi",
              "hostPort": 9090, "containerPort": 8080, "loopback": true, "nonRootUid": 1000,
              "mounts": [ { "path": "/data", "kind": "bind" } ],
              "env": { "FRAVASHI_VARIANT": "full" }
            }
          ],
          "hmac": "abcdef"
        }
        """);

        var record = Reader().Read();

        record.Should().NotBeNull();
        record!.Runtime.Should().Be("wslc");
        record.Hmac.Should().Be("abcdef");
        record.Components.Should().ContainSingle();
        record.Components[0].NonRootUid.Should().Be(1000);
        record.Components[0].Mounts.Should().ContainSingle(m => m.Path == "/data" && m.Kind == "bind");
        record.Components[0].Env.Should().Contain(kv => kv.Key == "FRAVASHI_VARIANT" && kv.Value == "full");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
