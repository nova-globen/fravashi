using Fravashi.Application.Updates;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Updates;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Updates;

/// <summary>
/// The app-side reader of the launcher-written product-update state (Type B / D03-2): a critical flag locks, an
/// available one does not, an absent file degrades to None, and a malformed file never locks/crashes.
/// </summary>
public sealed class FileProductUpdateStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fravashi-updstate-" + Guid.NewGuid().ToString("N"));

    public FileProductUpdateStatusTests() => Directory.CreateDirectory(_dir);

    private DataDirectory DataDir => new(_dir);

    private FileProductUpdateStatus Reader(TimeProvider? clock = null) =>
        new(DataDir, NullLogger<FileProductUpdateStatus>.Instance, clock);

    private void WriteState(string json) =>
        File.WriteAllText(Path.Combine(_dir, FileProductUpdateStatus.FileName), json);

    [Fact]
    public void Absent_file_is_None_and_never_locked()
    {
        var state = Reader().Current;
        state.Severity.Should().Be(ProductUpdateSeverity.None);
        state.IsLocked.Should().BeFalse();
    }

    [Fact]
    public void Critical_state_locks_and_surfaces_versions()
    {
        WriteState("""
        { "kind": "Critical", "currentVersion": "1.0.0", "targetVersion": "2.0.0",
          "releaseNotesUrl": "https://example/notes", "checkedUtc": "2026-07-16T10:00:00Z" }
        """);

        var state = Reader().Current;
        state.Severity.Should().Be(ProductUpdateSeverity.Critical);
        state.IsLocked.Should().BeTrue();
        state.CurrentVersion.Should().Be("1.0.0");
        state.TargetVersion.Should().Be("2.0.0");
        state.ReleaseNotesUrl.Should().Be("https://example/notes");
    }

    [Fact]
    public void Available_state_does_not_lock()
    {
        WriteState("""{ "kind": "Available", "currentVersion": "1.0.0", "targetVersion": "1.1.0" }""");

        var state = Reader().Current;
        state.Severity.Should().Be(ProductUpdateSeverity.Available);
        state.IsLocked.Should().BeFalse();
    }

    [Fact]
    public void Malformed_file_is_treated_as_None()
    {
        WriteState("{ this is not json ");
        Reader().Current.Severity.Should().Be(ProductUpdateSeverity.None);
    }

    [Fact]
    public void Value_is_cached_within_the_ttl_then_refreshed()
    {
        var clock = new MutableClock { Now = DateTimeOffset.Parse("2026-07-16T10:00:00Z") };
        var reader = Reader(clock);

        WriteState("""{ "kind": "Critical" }""");
        reader.Current.Severity.Should().Be(ProductUpdateSeverity.Critical);

        // Within the TTL the cached value is returned even though the file changed.
        WriteState("""{ "kind": "None" }""");
        reader.Current.Severity.Should().Be(ProductUpdateSeverity.Critical);

        // After the TTL it re-reads and reflects the new state.
        clock.Now = clock.Now.AddMinutes(1);
        reader.Current.Severity.Should().Be(ProductUpdateSeverity.None);
    }

    [Fact]
    public void A_channel_the_launcher_could_not_check_is_shown_and_never_lowers_the_lock()
    {
        // Backlog 230: the launcher records failed checks in update-check.json and leaves update-state.json alone.
        WriteState("""{ "kind": "Critical", "currentVersion": "1.0.0", "targetVersion": "2.0.0" }""");
        File.WriteAllText(Path.Combine(_dir, FileProductUpdateStatus.CheckFailureFileName),
            """{ "failingSinceUtc": "2026-10-07T14:02:00Z", "lastTriedUtc": "2026-10-08T09:00:00Z", "reason": "name not resolved" }""");

        var state = Reader().Current;

        state.IsLocked.Should().BeTrue();
        state.TargetVersion.Should().Be("2.0.0");
        state.ChannelUnreachableSince.Should().Be(DateTimeOffset.Parse("2026-10-07T14:02:00Z"));
        state.ChannelUnreachableReason.Should().Be("name not resolved");
    }

    [Fact]
    public void An_unreadable_check_record_changes_nothing()
    {
        WriteState("""{ "kind": "Critical" }""");
        File.WriteAllText(Path.Combine(_dir, FileProductUpdateStatus.CheckFailureFileName), "{ not json");

        var state = Reader().Current;

        state.IsLocked.Should().BeTrue();
        state.ChannelUnreachableSince.Should().BeNull();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
