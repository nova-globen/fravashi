using Bunit;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Backup;
using Fravashi.Application.Notifications;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 79: Settings &gt; Data &gt; Backup and restore. Three things are held, and each one is a sentence the
/// owner cannot work out from the button. Where the file LANDS, because a backup nobody can find is not
/// one. That restoring is a launcher command and not a button here, because the process holding the
/// database open cannot replace it. And that the archive carries the key ring, which is what makes it an
/// export at all and also what makes it as sensitive as a credential.
/// </summary>
public class BackupSettingsSectionTests
{
    private static BunitContext Context(out RecordingBackupWriter backups)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        backups = new RecordingBackupWriter();
        ctx.Services.AddSingleton<IStudioBackupWriter>(backups);
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        return ctx;
    }

    [Fact]
    public void The_section_names_the_folder_backups_land_in_before_one_is_taken()
    {
        using var ctx = Context(out _);

        var cut = ctx.Render<BackupSettingsSection>();

        Assert.Contains("/data/backups", cut.Find("[data-testid=backup-folder]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=backup-result]"));
    }

    [Fact]
    public void Taking_a_backup_shows_the_path_the_size_and_the_schema_version()
    {
        using var ctx = Context(out var backups);
        var cut = ctx.Render<BackupSettingsSection>();

        cut.InvokeAsync(() => cut.Find("[data-testid=backup-write]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(
                "/data/backups/fravashi-backup-20260913-070509.zip",
                cut.Find("[data-testid=backup-path]").TextContent);
            var result = cut.Find("[data-testid=backup-result]").TextContent;
            Assert.Contains("4096", result, StringComparison.Ordinal);
            Assert.Contains("schema 10", result, StringComparison.Ordinal);
        });
        Assert.Equal(1, backups.Writes);
    }

    [Fact]
    public void The_configured_providers_are_listed_so_the_owner_knows_what_to_check_after_a_move()
    {
        using var ctx = Context(out _);
        var cut = ctx.Render<BackupSettingsSection>();

        cut.InvokeAsync(() => cut.Find("[data-testid=backup-write]").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "Wiki 'wiki-main'",
            cut.Find("[data-testid=backup-providers]").TextContent,
            StringComparison.Ordinal));
    }

    [Fact]
    public void A_backup_that_could_not_be_written_says_so_instead_of_reporting_a_path()
    {
        using var ctx = Context(out var backups);
        backups.Failure = new IOException("the disk is full");
        var cut = ctx.Render<BackupSettingsSection>();

        cut.InvokeAsync(() => cut.Find("[data-testid=backup-write]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(
                "the disk is full",
                cut.Find("[data-testid=backup-error]").TextContent,
                StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=backup-result]"));
        });
    }

    [Fact]
    public void Restoring_is_explained_as_a_launcher_command_that_keeps_the_previous_state()
    {
        using var ctx = Context(out _);

        var cut = ctx.Render<BackupSettingsSection>();
        var help = cut.Find("[data-testid=backup-restore-help]").TextContent;

        Assert.Contains("fravashi restore", help, StringComparison.Ordinal);
        Assert.Contains("pre-restore", help, StringComparison.Ordinal);
        Assert.Contains("never deletes", help, StringComparison.Ordinal);
        // The section must not pretend a button here could do it: the studio holds the database open.
        Assert.Empty(cut.FindAll("[data-testid=backup-restore-button]"));
    }

    [Fact]
    public void The_export_text_warns_that_the_file_carries_the_key_ring()
    {
        using var ctx = Context(out _);

        var cut = ctx.Render<BackupSettingsSection>();

        Assert.Contains(
            "reconnected",
            cut.Find("[data-testid=backup-export-help]").TextContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "like a credential",
            cut.Find("[data-testid=backup-sensitivity]").TextContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_section_says_that_the_agent_CLI_sign_ins_are_not_in_the_archive()
    {
        // The archive leaves the container's HOME out (it is gigabytes of caches around a few credential
        // files). That is a defensible choice and an indefensible silence: an owner reading "this is your
        // export" has to be told here, not on the new machine when an agent asks them to sign in.
        using var ctx = Context(out _);

        var cut = ctx.Render<BackupSettingsSection>();
        var excluded = cut.Find("[data-testid=backup-excluded]").TextContent;

        Assert.Contains("sign in to the agent CLIs again", excluded, StringComparison.Ordinal);
        Assert.Contains("home", excluded, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_names_no_runtime_the_catalog_does_not_offer()
    {
        // Backlog 180: Codex and Copilot were parked for v2 and the page went on telling people to sign in to
        // them again. The retired ones are found through the enum, which keeps their values so stored rows
        // still parse, so this needs no edit when a runtime is parked or brought back.
        using var ctx = Context(out _);

        var page = ctx.Render<BackupSettingsSection>().Markup;

        var offered = AgentRuntimeCatalog.All.Select(runtime => runtime.Tool).ToHashSet();
        var retired = Enum.GetValues<AgentTool>().Where(tool => !offered.Contains(tool)).ToList();
        foreach (var tool in retired)
        {
            Assert.DoesNotContain(tool.ToString(), page, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_sign_ins_to_redo_name_every_runtime_the_catalog_offers()
    {
        using var ctx = Context(out _);

        var excluded = ctx.Render<BackupSettingsSection>().Find("[data-testid=backup-excluded]").TextContent;

        Assert.All(
            AgentRuntimeCatalog.All,
            runtime => Assert.Contains(runtime.DisplayName, excluded, StringComparison.Ordinal));
    }

    // ---- doubles ---------------------------------------------------------------------------------

    internal sealed class RecordingBackupWriter : IStudioBackupWriter
    {
        public int Writes { get; private set; }

        public Exception? Failure { get; set; }

        public string Folder => "/data/backups";

        public StudioBackup Write()
        {
            Writes++;
            if (Failure is { } failure)
            {
                throw failure;
            }

            return new StudioBackup(
                "/data/backups/fravashi-backup-20260913-070509.zip",
                4096,
                12,
                10,
                ["Wiki 'wiki-main' (connected, credential stored)"]);
        }
    }
}
