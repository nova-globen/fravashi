using Bunit;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.WorkItems;
using Fravashi.Web.Components.Sessions;
using Fravashi.Web.Components.WorkItems;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #359: pasting into the composer (folding in #355) and the images a work item carries. A pasted image is
/// attached and referenced as "[Image N]", numbered across the whole conversation; a long pasted text becomes a chip
/// referenced as "[Pasted text N, M characters]" and is sent as a block wrapped under that reference; a short paste is
/// typed normally. An item's images show as thumbnails on its page and in the reference preview, and open large.
/// </summary>
public class AssistantComposerAndAttachmentsTests
{
    internal static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3];

    private static BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // ---- the rules ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_paste_is_large_from_the_character_or_the_line_threshold_and_small_ones_are_typed()
    {
        Assert.False(ComposerPastes.IsLarge("a short sentence"));
        Assert.False(ComposerPastes.IsLarge(new string('x', ComposerPastes.LargeCharacters - 1)));
        Assert.True(ComposerPastes.IsLarge(new string('x', ComposerPastes.LargeCharacters)));
        Assert.True(ComposerPastes.IsLarge(string.Join('\n', Enumerable.Repeat("ab", ComposerPastes.LargeLines))));
        Assert.False(ComposerPastes.IsLarge(string.Join('\n', Enumerable.Repeat("ab", ComposerPastes.LargeLines - 1))));
    }

    [Fact]
    public void What_is_sent_carries_each_referenced_paste_wrapped_under_its_reference_and_drops_a_deleted_one()
    {
        var kept = new PastedText(1, "line one\nline two");
        var dropped = new PastedText(2, "never referenced");

        var sent = ComposerPastes.Compose("Here is the log " + kept.Reference + ", see?", [kept, dropped]);

        Assert.StartsWith("Here is the log [Pasted text 1, 17 characters], see?", sent, StringComparison.Ordinal);
        Assert.Contains("<pasted-content reference=\"[Pasted text 1, 17 characters]\">\nline one\nline two\n</pasted-content>", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("never referenced", sent, StringComparison.Ordinal);
        Assert.Equal("plain words", ComposerPastes.Compose("plain words", []));
    }

    [Fact]
    public void Removing_an_image_takes_its_reference_out_and_renumbers_the_later_ones()
    {
        Assert.Equal("a  b [Image 3] c [Image 1]", ComposerPastes.RemoveImage("a [Image 3] b [Image 4] c [Image 1]", 3));
    }

    // ---- the composer ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_long_paste_becomes_a_chip_whose_reference_is_typed_and_whose_text_is_sent_as_a_block()
    {
        using var ctx = Context();
        string? sent = null;
        var cut = ctx.Render<SessionComposer>(p => p.Add(c => c.Send, text => sent = text));
        var big = string.Join('\n', Enumerable.Range(1, 40).Select(i => "stack frame " + i));

        string? reference = null;
        await cut.InvokeAsync(() => reference = cut.Instance.AddPastedText(big));
        string? small = "x";
        await cut.InvokeAsync(() => small = cut.Instance.AddPastedText("short"));

        Assert.Equal($"[Pasted text 1, {big.Length:N0} characters]", reference);
        Assert.Null(small);
        var chip = Assert.Single(cut.FindAll("[data-testid=composer-pasted-text]"));
        Assert.Contains("Pasted text 1", chip.TextContent, StringComparison.Ordinal);

        cut.Render(p => p.Add(c => c.Text, "The crash: " + reference));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        Assert.NotNull(sent);
        Assert.StartsWith("The crash: " + reference, sent, StringComparison.Ordinal);
        Assert.Contains("<pasted-content reference=\"" + reference + "\">", sent, StringComparison.Ordinal);
        Assert.Contains("stack frame 40", sent, StringComparison.Ordinal);

        // The parent cleared the text once it took the turn, and the chip went with it.
        cut.Render(p => p.Add(c => c.Text, string.Empty));
        Assert.Empty(cut.FindAll("[data-testid=composer-pasted-text]"));
    }

    [Fact]
    public async Task A_pasted_image_is_attached_and_referenced_by_its_number_in_the_whole_conversation()
    {
        using var ctx = Context();
        IReadOnlyList<AgentImageAttachment> images = [];
        var cut = ctx.Render<SessionComposer>(p => p
            .Add(c => c.ImageOffset, 2)
            .Add(c => c.ImagesChanged, list => images = list));

        string? reference = null;
        await cut.InvokeAsync(async () => reference = await cut.Instance.AddImageAsync(Png));

        Assert.Equal("[Image 3]", reference);
        Assert.Single(images);
    }

    [Fact]
    public async Task Removing_an_attached_image_takes_its_reference_out_of_the_text()
    {
        using var ctx = Context();
        var first = AgentImageAttachment.TryCreate(Png, out _)!;
        var second = AgentImageAttachment.TryCreate(Png, out _)!;
        string? text = null;
        var cut = ctx.Render<SessionComposer>(p => p
            .Add(c => c.Text, "before [Image 1] middle [Image 2]")
            .Add(c => c.Images, new[] { first, second })
            .Add(c => c.TextChanged, t => text = t));

        await cut.InvokeAsync(() => cut.FindAll("[data-testid=remove-image]")[0].Click());

        Assert.Equal("before  middle [Image 1]", text);
    }

    [Fact]
    public void The_send_button_shows_it_is_busy_and_cannot_be_pressed_again()
    {
        using var ctx = Context();
        var cut = ctx.Render<SessionComposer>(p => p.Add(c => c.Text, "hello").Add(c => c.Busy, true));

        var button = cut.Find("[data-testid=send-prompt]");
        Assert.True(button.HasAttribute("disabled"));
        Assert.NotNull(button.QuerySelector("[data-testid=send-busy]"));
        Assert.Equal("true", button.GetAttribute("aria-busy"));
    }

    // ---- attachments -------------------------------------------------------------------------------------------------

    [Fact]
    public void An_items_images_show_as_thumbnails_and_open_large()
    {
        using var ctx = Context();
        var store = new InMemoryWorkItemAttachments();
        var item = new WorkItemId("item-1");
        store.AddImage(item, "save-button.png", Png, "user");
        store.AddImage(item, "after.png", Png, "user");
        ctx.Services.AddSingleton<IWorkItemAttachments>(store);

        var cut = ctx.Render<WorkItemAttachmentsView>(p => p.Add(c => c.Item, item).Add(c => c.AllowAdd, true).Add(c => c.Heading, "Images"));

        var thumbs = cut.FindAll("[data-testid=work-item-attachment]");
        Assert.Equal(2, thumbs.Count);
        Assert.StartsWith("data:image/png;base64,", thumbs[0].QuerySelector("img")!.GetAttribute("src"), StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=work-item-attach]"));
        Assert.Empty(cut.FindAll("[data-testid=work-item-attachment-viewer]"));

        cut.FindAll("[data-testid=work-item-attachment]")[0].Click();
        Assert.Contains("save-button.png", cut.Find("[data-testid=work-item-attachment-viewer]").TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid=work-item-attachment-close]").Click();
        Assert.Empty(cut.FindAll("[data-testid=work-item-attachment-viewer]"));
    }

    [Fact]
    public void An_image_attached_while_the_page_is_open_shows_at_once()
    {
        using var ctx = Context();
        var store = new InMemoryWorkItemAttachments();
        var item = new WorkItemId("item-1");
        ctx.Services.AddSingleton<IWorkItemAttachments>(store);
        var cut = ctx.Render<WorkItemAttachmentsView>(p => p.Add(c => c.Item, item).Add(c => c.AllowAdd, true));
        Assert.NotNull(cut.Find("[data-testid=work-item-no-attachments]"));

        store.AddImage(item, "new.png", Png, "session:abc");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=work-item-attachment]")));
    }

    [Fact]
    public void Without_the_store_or_without_images_the_preview_shows_nothing()
    {
        using var ctx = Context();
        var cut = ctx.Render<WorkItemAttachmentsView>(p => p.Add(c => c.Item, new WorkItemId("item-1")).Add(c => c.Compact, true));
        Assert.Empty(cut.FindAll("[data-testid=work-item-attachments]"));

        using var withStore = Context();
        withStore.Services.AddSingleton<IWorkItemAttachments>(new InMemoryWorkItemAttachments());
        var empty = withStore.Render<WorkItemAttachmentsView>(p => p.Add(c => c.Item, new WorkItemId("item-1")).Add(c => c.Compact, true));
        Assert.Empty(empty.FindAll("[data-testid=work-item-attachments]"));
    }
}

/// <summary>An in-memory <see cref="IWorkItemAttachments"/> for component tests (work item #359).</summary>
internal sealed class InMemoryWorkItemAttachments : IWorkItemAttachments
{
    private readonly List<(WorkItemAttachment Row, byte[] Bytes)> _rows = [];

    public event Action<WorkItemId>? Changed;

    public WorkItemAttachmentResult AddImage(WorkItemId item, string? fileName, byte[] bytes, string addedBy)
    {
        if (ImageFormats.Sniff(bytes) is not { } format)
        {
            return WorkItemAttachmentResult.Refused("Only PNG, JPEG, GIF and WebP images can be attached to a work item.");
        }

        var row = new WorkItemAttachment(
            Guid.NewGuid().ToString("N"), item, fileName ?? "image", format.MediaType, bytes.Length, DateTimeOffset.UtcNow, addedBy);
        _rows.Add((row, bytes));
        Changed?.Invoke(item);
        return new WorkItemAttachmentResult(row, null);
    }

    public IReadOnlyList<WorkItemAttachment> For(WorkItemId item) => [.. _rows.Where(r => r.Row.Item == item).Select(r => r.Row)];

    public byte[]? Read(WorkItemId item, string attachmentId) =>
        _rows.FirstOrDefault(r => r.Row.Item == item && r.Row.Id == attachmentId).Bytes;
}
