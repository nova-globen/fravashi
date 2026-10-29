using AwesomeAssertions;
using Fravashi.Application.Persistence;
using Fravashi.Application.Telemetry;
using Fravashi.Domain.Telemetry;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Telemetry;
using Fravashi.Infrastructure.Tests.Persistence;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Telemetry;

/// <summary>
/// Backlog 74: recording is <b>opt-in and off by default</b>, and the payload survives a trip through
/// SQLite unchanged. The opt-in half is the one that matters most: an install nobody configured must
/// write nothing at all, and a switch turned off must stop the very next event.
/// </summary>
public class ProductEventRecorderTests : IDisposable
{
    private readonly SqliteTestContext _db = new();

    public void Dispose() => _db.Dispose();

    private (ProductEventRecorder Recorder, IProductEventStore Store, IConfigurationStore Settings) Build()
    {
        var settings = new SqliteConfigurationStore(_db.Factory);
        var store = new SqliteProductEventStore(_db.Factory);
        return (new ProductEventRecorder(settings, store), store, settings);
    }

    private static ProductEvent Sample(DateTimeOffset at) =>
        ProductEvent.SessionStarted(at, "ws-1", TimeSpan.FromSeconds(3));

    // ---- the opt-in ------------------------------------------------------------------------------

    [Fact]
    public void A_fresh_install_records_nothing_because_the_switch_defaults_off()
    {
        var (recorder, store, _) = Build();

        recorder.IsEnabled.Should().BeFalse("no settings row exists on a fresh database");
        recorder.Record(Sample(DateTimeOffset.UtcNow));

        store.Count().Should().Be(0);
        store.Recent().Should().BeEmpty();
    }

    [Theory]
    // Everything that is not a parseable true leaves the switch OFF. An opt-in a typo could switch on
    // would not be an opt-in.
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("True-ish")]
    [InlineData("false")]
    public void Anything_that_is_not_a_parseable_true_leaves_recording_off(string stored)
    {
        var (recorder, store, settings) = Build();
        settings.Set(TelemetrySettings.EnabledKey, stored);

        recorder.IsEnabled.Should().BeFalse();
        recorder.Record(Sample(DateTimeOffset.UtcNow));

        store.Count().Should().Be(0);
    }

    [Fact]
    public void Switching_it_on_records_and_switching_it_off_again_stops_the_next_event()
    {
        var (recorder, store, settings) = Build();

        settings.Set(TelemetrySettings.EnabledKey, "true");
        recorder.IsEnabled.Should().BeTrue();
        recorder.Record(Sample(DateTimeOffset.UtcNow));
        store.Count().Should().Be(1);

        // The switch is read per record, not cached at construction: turning it off stops the NEXT
        // event, not the next restart.
        settings.Set(TelemetrySettings.EnabledKey, "false");
        recorder.IsEnabled.Should().BeFalse();
        recorder.Record(Sample(DateTimeOffset.UtcNow));

        store.Count().Should().Be(1, "the event after the switch went off must not have been written");
    }

    [Fact]
    public void Recording_never_throws_at_a_producer_even_when_the_store_is_broken()
    {
        // Telemetry exists to tell us whether the product works and must never be why it does not.
        var settings = new SqliteConfigurationStore(_db.Factory);
        settings.Set(TelemetrySettings.EnabledKey, "true");
        var recorder = new ProductEventRecorder(settings, new ThrowingStore());

        var record = () => recorder.Record(Sample(DateTimeOffset.UtcNow));

        record.Should().NotThrow();
    }

    // ---- the round trip --------------------------------------------------------------------------

    [Fact]
    public void Every_kind_and_outcome_round_trips_through_the_database_unchanged()
    {
        var (recorder, store, settings) = Build();
        settings.Set(TelemetrySettings.EnabledKey, "true");

        var at = new DateTimeOffset(2026, 9, 13, 8, 30, 15, TimeSpan.Zero);
        var written = new List<ProductEvent>();
        var kinds = Enum.GetValues<ProductEventKind>();
        var outcomes = Enum.GetValues<ProductEventOutcome>();
        for (var i = 0; i < kinds.Length; i++)
        {
            var e = new ProductEvent(
                kinds[i],
                at.AddMinutes(i),
                outcomes[i % outcomes.Length],
                i % 2 == 0 ? WorkspaceHash.Of("workspace-" + i) : WorkspaceHash.None,
                DurationMs: i % 3 == 0 ? null : i * 1000L,
                Count: i % 4 == 0 ? i : null);
            written.Add(e);
            recorder.Record(e);
        }

        // Newest first, so the read order is the reverse of the write order.
        store.Recent().Should().Equal(Enumerable.Reverse(written));
        store.Count().Should().Be(kinds.Length);
    }

    [Fact]
    public void A_stored_row_holds_only_the_schema_columns_and_no_text_from_the_workspace_id()
    {
        var (recorder, _, settings) = Build();
        settings.Set(TelemetrySettings.EnabledKey, "true");

        const string secretish = "C:/Users/someone/Secret Project";
        recorder.Record(ProductEvent.SessionStartFailed(DateTimeOffset.UtcNow, secretish));

        using var connection = _db.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, outcome, workspace_hash FROM product_events;";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();

        reader.GetString(0).Should().Be(nameof(ProductEventKind.SessionStart));
        reader.GetString(1).Should().Be(nameof(ProductEventOutcome.Failed));
        reader.GetString(2).Should().Be(WorkspaceHash.Of(secretish).Value);
        reader.GetString(2).Should().NotContain("Secret");
        reader.GetString(2).Should().NotContain("Users");
    }

    [Fact]
    public void A_row_hand_edited_to_hold_a_path_reads_back_as_no_workspace()
    {
        // The read path is the last line of defence: a bundle must not surface something someone wrote
        // straight into the table.
        var (recorder, store, settings) = Build();
        settings.Set(TelemetrySettings.EnabledKey, "true");
        recorder.Record(Sample(DateTimeOffset.UtcNow));

        using (var connection = _db.Factory.Open())
        {
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE product_events SET workspace_hash = '/home/me/secret-project';";
            update.ExecuteNonQuery();
        }

        store.Recent().Single().Workspace.HasValue.Should().BeFalse();
        store.Recent().Single().Workspace.Value.Should().BeEmpty();
    }

    [Fact]
    public void A_row_with_an_unknown_kind_is_skipped_rather_than_guessed_at()
    {
        var (recorder, store, settings) = Build();
        settings.Set(TelemetrySettings.EnabledKey, "true");
        recorder.Record(Sample(DateTimeOffset.UtcNow));

        using (var connection = _db.Factory.Open())
        {
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE product_events SET kind = 'SomethingFromTheFuture';";
            update.ExecuteNonQuery();
        }

        store.Recent().Should().BeEmpty();
        store.Count().Should().Be(1, "the row is still there; it is only unreadable by this build");
    }

    // ---- the windowed read the home surface needs (backlog 36) -----------------------------------

    [Fact]
    public void Since_returns_one_kind_inside_the_window_and_nothing_else()
    {
        var (recorder, store, settings) = Build();
        settings.Set(TelemetrySettings.EnabledKey, "true");
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

        recorder.Record(ProductEvent.ApprovalResolved(
            now.AddDays(-1), "ws-1", ProductEventOutcome.Approved, TimeSpan.FromSeconds(5)));
        recorder.Record(ProductEvent.ApprovalResolved(
            now.AddDays(-29), "ws-1", ProductEventOutcome.DeniedByUser, TimeSpan.FromSeconds(60)));
        // Older than the window, and a different kind inside it: neither may come back.
        recorder.Record(ProductEvent.ApprovalResolved(
            now.AddDays(-31), "ws-1", ProductEventOutcome.Approved, TimeSpan.FromHours(4)));
        recorder.Record(ProductEvent.SessionStarted(now.AddDays(-1), "ws-1", TimeSpan.FromSeconds(2)));

        var approvals = store.Since(ProductEventKind.ApprovalResolved, now.AddDays(-30));

        approvals.Should().HaveCount(2);
        approvals.Should().OnlyContain(e => e.Kind == ProductEventKind.ApprovalResolved);
        // Most recently RECORDED first, like Recent: the 60s approval was written second.
        approvals.Select(e => e.DurationMs).Should().Equal(60_000L, 5_000L);
    }

    [Fact]
    public void Since_is_not_crowded_out_by_events_of_other_kinds()
    {
        // The reason Since exists rather than a filter over Recent: a busy month of one kind must not
        // push the kind being summarised out of the read, or the median would be of the survivors.
        var (recorder, store, settings) = Build();
        settings.Set(TelemetrySettings.EnabledKey, "true");
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

        recorder.Record(ProductEvent.ApprovalResolved(
            now.AddDays(-20), "ws-1", ProductEventOutcome.Approved, TimeSpan.FromSeconds(7)));
        for (var i = 0; i < 50; i++)
        {
            recorder.Record(ProductEvent.SessionStarted(now.AddMinutes(-i), "ws-1", TimeSpan.FromSeconds(1)));
        }

        store.Recent(10).Should().NotContain(e => e.Kind == ProductEventKind.ApprovalResolved);
        store.Since(ProductEventKind.ApprovalResolved, now.AddDays(-30)).Should().ContainSingle();
    }

    private sealed class ThrowingStore : IProductEventStore
    {
        public void Append(ProductEvent productEvent) => throw new InvalidOperationException("disk is gone");

        public IReadOnlyList<ProductEvent> Recent(int max = 2000) => [];

        public IReadOnlyList<ProductEvent> Since(ProductEventKind kind, DateTimeOffset fromUtc, int max = 20000) => [];

        public long Count() => 0;
    }
}
