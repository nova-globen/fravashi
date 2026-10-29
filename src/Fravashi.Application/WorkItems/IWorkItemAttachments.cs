using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.WorkItems;

/// <summary>One file a work item carries (work item #359). Images only in this version.</summary>
/// <param name="Id">The attachment's own id, never reused.</param>
/// <param name="Item">The item it belongs to, by id, so a renumbered item keeps its files.</param>
/// <param name="FileName">A display name: what a person called it, or one the studio made up. Never a path.</param>
/// <param name="MediaType">Read off the bytes' own signature, never taken from a browser or an agent.</param>
/// <param name="SizeBytes">The stored size.</param>
/// <param name="AddedUtc">When it was attached.</param>
/// <param name="AddedBy">Who attached it: <c>user</c>, or the conversation that did (<c>session:{id}</c>).</param>
public sealed record WorkItemAttachment(
    string Id,
    WorkItemId Item,
    string FileName,
    string MediaType,
    long SizeBytes,
    DateTimeOffset AddedUtc,
    string AddedBy)
{
    /// <summary>True for an image the studio can render.</summary>
    public bool IsImage => ImageFormats.SupportedMediaTypes.Contains(MediaType, StringComparer.Ordinal);
}

/// <summary>What an attach did: the stored attachment, or a refusal in words.</summary>
public sealed record WorkItemAttachmentResult(WorkItemAttachment? Attachment, string? Refusal)
{
    /// <summary>A refusal, nothing stored.</summary>
    public static WorkItemAttachmentResult Refused(string why) => new(null, why);
}

/// <summary>
/// The files work items carry (work item #359). The bytes live in the studio's data directory under one folder per
/// item, never in the repository and never in a provider; the rows say what is there. Images only, size-capped and
/// type-checked by their content: the same formats and the same ceiling a conversation's pasted image has
/// (<see cref="AgentImageAttachment"/>), so anything attached can also be shown to an agent.
/// </summary>
public interface IWorkItemAttachments
{
    /// <summary>How many files one item may carry: enough for a report's screenshots, few enough to stay a report.</summary>
    const int MaxPerItem = 20;

    /// <summary>
    /// Stores an image on the item, or says why not: empty, not one of the supported image formats by its bytes,
    /// over the size limit, or the item already carries <see cref="MaxPerItem"/> files.
    /// </summary>
    WorkItemAttachmentResult AddImage(WorkItemId item, string? fileName, byte[] bytes, string addedBy);

    /// <summary>The item's attachments, oldest first. Empty when it has none or the store cannot be read.</summary>
    IReadOnlyList<WorkItemAttachment> For(WorkItemId item);

    /// <summary>
    /// The bytes of one of the item's attachments, or null when there is no such attachment on that item or its file
    /// no longer matches the row (missing, or not the image the row says).
    /// </summary>
    byte[]? Read(WorkItemId item, string attachmentId);

    /// <summary>Raised with the item whose attachments changed, so an open item page shows it.</summary>
    event Action<WorkItemId>? Changed;
}
