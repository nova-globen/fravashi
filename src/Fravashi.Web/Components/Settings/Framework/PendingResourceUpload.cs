namespace Fravashi.Web.Components.Settings.Framework;

/// <summary>
/// An uploaded blob held in the skill form before the artifact is saved. On save the form persists each pending
/// upload as a <c>FrameworkResource</c> under the (now-existing) artifact id. Kept in memory only until then, so
/// a brand-new skill can carry an uploaded icon or resource without first having a row to attach it to.
/// </summary>
/// <param name="Id">A pre-generated resource id, embedded in the icon reference / resource list before save.</param>
/// <param name="FileName">The upload file name.</param>
/// <param name="ContentType">The MIME type, when provided.</param>
/// <param name="Content">The raw bytes.</param>
public sealed record PendingResourceUpload(
    string Id,
    string FileName,
    string? ContentType,
    byte[] Content)
{
    /// <summary>The blob size in bytes.</summary>
    public long SizeBytes => Content.LongLength;
}
