using Fravashi.Application.Sandbox;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sandbox;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sandbox;

/// <summary>
/// The app-side reader of the launcher-written sandbox posture (F4): it reflects the recorded per-control
/// active/unavailable truth, degrades honestly to "outside a container" when the file is absent, and never
/// crashes on a malformed file. Assertion-gap rule: what it reports must be exactly what the launcher recorded.
/// </summary>
public sealed class FileSandboxPostureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fravashi-posture-" + Guid.NewGuid().ToString("N"));

    public FileSandboxPostureTests() => Directory.CreateDirectory(_dir);

    private FileSandboxPosture Reader() => new(new DataDirectory(_dir), NullLogger<FileSandboxPosture>.Instance);

    private void Write(string json) => File.WriteAllText(Path.Combine(_dir, FileSandboxPosture.FileName), json);

    [Fact]
    public void Absent_file_reports_outside_a_container_and_is_not_known()
    {
        var posture = Reader().Current;
        posture.Known.Should().BeFalse();
        posture.Controls.Should().BeEmpty();
    }

    [Fact]
    public void Reads_the_runtime_and_per_control_statuses()
    {
        Write("""
        {
          "runtime": "wslc",
          "strongerRuntimeRecommended": true,
          "recommendation": "Use Docker or Podman for stronger isolation.",
          "checkedUtc": "2026-07-17T10:00:00Z",
          "controls": [
            { "id": "LoopbackPublish", "title": "Loopback-only port", "description": "d", "status": "Active", "detail": "127.0.0.1" },
            { "id": "ReadOnlyRootfs", "title": "Read-only root filesystem", "description": "d", "status": "Unavailable", "detail": "Not supported by wslc - use Docker." }
          ]
        }
        """);

        var posture = Reader().Current;
        posture.Known.Should().BeTrue();
        posture.Runtime.Should().Be("wslc");
        posture.StrongerRuntimeRecommended.Should().BeTrue();
        posture.ActiveCount.Should().Be(1);
        posture.UnavailableCount.Should().Be(1);

        var readOnly = posture.Controls.Single(c => c.Id == "ReadOnlyRootfs");
        readOnly.Status.Should().Be(SandboxProtectionStatus.Unavailable);
        readOnly.Detail.Should().Contain("Docker");
    }

    [Fact]
    public void Malformed_file_degrades_to_outside_a_container()
    {
        Write("{ not json ");
        Reader().Current.Known.Should().BeFalse();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
