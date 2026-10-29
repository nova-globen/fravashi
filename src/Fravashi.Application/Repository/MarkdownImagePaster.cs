using System.Globalization;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Repository;

/// <summary>What pasting an image into a markdown file did: the link to insert, or why nothing was saved.</summary>
/// <param name="Markdown">The markdown image link to insert at the cursor, relative to the file.</param>
/// <param name="SavedPath">The repository-relative path the image was saved to.</param>
public sealed record MarkdownImagePasteResult(bool Succeeded, string? Markdown = null, string? SavedPath = null, string? Error = null)
{
    public static MarkdownImagePasteResult Fail(string error) => new(false, Error: error);
}

/// <summary>
/// Pasting an image into a markdown document (backlog 26): the image is saved BESIDE the document through the
/// root-bounded <see cref="IRepositoryWriter"/> (so it lands in the working tree under the same containment and
/// write gate as every editor save) and the caller gets a relative link to insert.
/// <para>
/// The file is created first and filled second, because creating is the writer's only operation that refuses to
/// overwrite: a name that is already taken moves on to the next candidate instead of replacing someone's image.
/// </para>
/// </summary>
public sealed class MarkdownImagePaster(IRepositoryWriter writer, Func<DateTimeOffset>? clock = null)
{
    /// <summary>The largest image saved into the tree. Generous: this is a file in a repository, not a model turn.</summary>
    public const int MaxBytes = 25_000_000;

    private const int MaxCandidates = 20;

    private static readonly string[] MarkdownExtensions = [".md", ".markdown", ".mdx"];

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    /// <summary>True for the documents an image paste saves beside; everywhere else a paste is left to the editor.</summary>
    public static bool IsMarkdown(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && MarkdownExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    public async Task<MarkdownImagePasteResult> PasteAsync(
        string markdownPath, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (!IsMarkdown(markdownPath))
        {
            return MarkdownImagePasteResult.Fail("Images can be pasted into markdown files only.");
        }

        if (bytes.Length == 0)
        {
            return MarkdownImagePasteResult.Fail("The pasted image was empty.");
        }

        if (bytes.Length > MaxBytes)
        {
            return MarkdownImagePasteResult.Fail("The pasted image is larger than 25 MB.");
        }

        if (ImageFormats.Sniff(bytes) is not { } format)
        {
            return MarkdownImagePasteResult.Fail("Only PNG, JPEG, GIF and WebP images can be pasted.");
        }

        var normalized = markdownPath.Replace('\\', '/').Trim('/');
        var slash = normalized.LastIndexOf('/');
        var directory = slash < 0 ? string.Empty : normalized[..slash];
        var stem = "pasted-image-" + _clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        string? lastError = null;
        for (var attempt = 1; attempt <= MaxCandidates; attempt++)
        {
            var name = attempt == 1
                ? $"{stem}.{format.Extension}"
                : $"{stem}-{attempt.ToString(CultureInfo.InvariantCulture)}.{format.Extension}";

            var created = await writer.CreateFileAsync(directory, name, cancellationToken).ConfigureAwait(false);
            if (!created.Succeeded)
            {
                lastError = created.Error;
                if (created.Error?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
                {
                    continue;
                }

                return MarkdownImagePasteResult.Fail(created.Error ?? "The image could not be saved.");
            }

            var path = directory.Length == 0 ? name : $"{directory}/{name}";
            using var content = new MemoryStream(bytes, writable: false);
            var saved = await writer.SaveFileAsync(path, content, expectedLastWriteUtc: null, cancellationToken)
                .ConfigureAwait(false);
            return saved.Succeeded
                ? new MarkdownImagePasteResult(true, $"![{stem}]({name})", path)
                : MarkdownImagePasteResult.Fail(saved.Error ?? "The image could not be saved.");
        }

        return MarkdownImagePasteResult.Fail(lastError ?? "No free name was found for the image.");
    }
}
