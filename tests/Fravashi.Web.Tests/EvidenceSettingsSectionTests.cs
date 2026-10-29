using Bunit;
using Fravashi.Application.Evidence;
using Fravashi.Application.Notifications;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 75: Settings &gt; Data &gt; Evidence retention lists every evidence class with its size and policy, says
/// that the export is the backup and that nothing is synced to a provider yet, and deletes a class only on a
/// second, explicit click.
/// </summary>
public class EvidenceSettingsSectionTests
{
    private static BunitContext Context(IEvidenceLifecycle evidence)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IEvidenceLifecycle>(evidence);
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        return ctx;
    }

    [Fact]
    public void Every_class_is_shown_with_its_size_retention_and_redaction()
    {
        using var ctx = Context(new FakeEvidence());

        var cut = ctx.Render<EvidenceSettingsSection>();

        cut.WaitForAssertion(() =>
        {
            foreach (var evidenceClass in Enum.GetValues<EvidenceClass>())
            {
                var slug = EvidenceSettingsSection.Slug(evidenceClass);
                Assert.NotNull(cut.Find($"[data-testid=evidence-{slug}]"));
                Assert.Contains("kept for test", cut.Find($"[data-testid=evidence-{slug}-retention]").TextContent, StringComparison.Ordinal);
                Assert.Contains("redacted for test", cut.Find($"[data-testid=evidence-{slug}-redaction]").TextContent, StringComparison.Ordinal);
            }

            Assert.Contains("3 items, 2.0 KB", cut.Find("[data-testid=evidence-logs-size]").TextContent, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Work item #356: the page took long to load with no sign of life, because every class was measured (folder walks
    /// and a sum over every transcript payload) before the first render. It renders at once now, each class saying it
    /// is being measured until its answer arrives, and a class that cannot be measured says so in words.
    /// </summary>
    [Fact]
    public void The_page_renders_at_once_with_each_class_measuring_and_fills_in_as_answers_arrive()
    {
        var evidence = new GatedEvidence();
        using var ctx = Context(evidence);

        var cut = ctx.Render<EvidenceSettingsSection>();

        Assert.Contains("Measuring...", cut.Find("[data-testid=evidence-logs-loading]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Logs", cut.Find("[data-testid=evidence-logs]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=evidence-logs-size]"));

        evidence.Release.Set();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("3 items", cut.Find("[data-testid=evidence-logs-size]").TextContent, StringComparison.Ordinal);
            Assert.Contains("This could not be measured: the disk went away",
                cut.Find("[data-testid=evidence-backups-error]").TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=evidence-logs-loading]"));
        });
    }

    private sealed class GatedEvidence : IEvidenceLifecycle
    {
        public ManualResetEventSlim Release { get; } = new();

        public IReadOnlyList<EvidenceClassReport> Inventory() => throw new InvalidOperationException("measured class by class");

        public EvidenceClassReport ReportFor(EvidenceClass evidenceClass)
        {
            Release.Wait(TimeSpan.FromSeconds(10));
            if (evidenceClass == EvidenceClass.Backups)
            {
                throw new IOException("the disk went away");
            }

            return new EvidenceClassReport(evidenceClass, evidenceClass.ToString(), "/data", 3, 10, "kept", "redacted", "effect", null);
        }

        public EvidenceDeletionResult Delete(EvidenceClass evidenceClass) =>
            new(evidenceClass, true, 0, 0, "Deleted.");
    }

    [Fact]
    public void The_page_says_the_export_is_the_backup_and_that_provider_deletion_waits_for_item_83()
    {
        using var ctx = Context(new FakeEvidence());

        var cut = ctx.Render<EvidenceSettingsSection>();

        Assert.Contains("the export is the backup", cut.Find("[data-testid=evidence-export]").TextContent, StringComparison.Ordinal);
        Assert.Contains("/settings/backup", cut.Find("[data-testid=evidence-export]").InnerHtml, StringComparison.Ordinal);
        var provider = cut.Find("[data-testid=evidence-provider]").TextContent;
        Assert.Contains("none of this is synced", provider, StringComparison.Ordinal);
        Assert.Contains("83", provider, StringComparison.Ordinal);
    }

    [Fact]
    public void A_class_is_deleted_only_after_the_second_click()
    {
        var evidence = new FakeEvidence();
        using var ctx = Context(evidence);
        var cut = ctx.Render<EvidenceSettingsSection>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=evidence-trash-archives-delete]")));
        cut.InvokeAsync(() => cut.Find("[data-testid=evidence-trash-archives-delete]").Click());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=evidence-trash-archives-confirm]")));
        Assert.Empty(evidence.Deleted);

        cut.InvokeAsync(() => cut.Find("[data-testid=evidence-trash-archives-confirm]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal([EvidenceClass.TrashArchives], evidence.Deleted);
            Assert.Contains("Deleted-file archives: Deleted 3 item(s)", cut.Find("[data-testid=evidence-message]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Cancel_backs_out_without_deleting()
    {
        var evidence = new FakeEvidence();
        using var ctx = Context(evidence);
        var cut = ctx.Render<EvidenceSettingsSection>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=evidence-backups-delete]")));
        cut.InvokeAsync(() => cut.Find("[data-testid=evidence-backups-delete]").Click());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=evidence-backups-cancel]")));
        cut.InvokeAsync(() => cut.Find("[data-testid=evidence-backups-cancel]").Click());

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=evidence-backups-delete]")));
        Assert.Empty(evidence.Deleted);
    }

    [Fact]
    public void A_class_blocked_by_a_running_session_shows_why_and_offers_no_delete()
    {
        var evidence = new FakeEvidence { Blocked = EvidenceClass.AgentTranscripts };
        using var ctx = Context(evidence);

        var cut = ctx.Render<EvidenceSettingsSection>();

        cut.WaitForAssertion(() => Assert.Contains("session is running", cut.Find("[data-testid=evidence-agent-transcripts-blocked]").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid=evidence-agent-transcripts-delete]"));
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    public void Sizes_read_at_a_glance_in_an_invariant_format(long bytes, string expected)
    {
        Assert.Equal(expected, EvidenceSettingsSection.Bytes(bytes));
    }

    internal sealed class FakeEvidence : IEvidenceLifecycle
    {
        public List<EvidenceClass> Deleted { get; } = [];

        public EvidenceClass? Blocked { get; init; }

        public IReadOnlyList<EvidenceClassReport> Inventory() =>
        [
            .. Enum.GetValues<EvidenceClass>().Select(c => new EvidenceClassReport(
                c,
                c == EvidenceClass.TrashArchives ? "Deleted-file archives" : c.ToString(),
                "/data/" + c,
                Deleted.Contains(c) ? 0 : 3,
                Deleted.Contains(c) ? 0 : 2048,
                "kept for test",
                "redacted for test",
                "effect for test",
                c == Blocked ? "An agent session is running and still writing here." : null)),
        ];

        public EvidenceDeletionResult Delete(EvidenceClass evidenceClass)
        {
            Deleted.Add(evidenceClass);
            return new EvidenceDeletionResult(evidenceClass, true, 3, 2048, "Deleted 3 item(s), 2048 bytes.");
        }
    }
}
