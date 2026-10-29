using System.Globalization;
using System.Security.Cryptography;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.WorkItems;

/// <summary>
/// Work item attachments (work item #359): rows in <c>work_item_attachments</c> (schema step 46), bytes in
/// <c>&lt;data dir&gt;/work-item-attachments/&lt;item&gt;/&lt;id&gt;.&lt;ext&gt;</c>. The folder is in every backup
/// (<c>StudioBackupWriter</c>) and moved aside by a restore like the key ring and the agent sessions.
/// <para>
/// Nothing a caller supplies becomes a path: the item folder is named by the item's own id with every character that
/// is not a letter, digit or hyphen refused, the file by a fresh id and the extension of the format sniffed from the
/// bytes. A read checks the row, the file and the sniffed format again, so a file swapped underneath is never served
/// as the image the row says.
/// </para>
/// </summary>
public sealed class FileWorkItemAttachments : IWorkItemAttachments
{
    /// <summary>The folder under the data directory the files live in.</summary>
    public const string FolderName = "work-item-attachments";

    private readonly ISqliteConnectionFactory _connections;
    private readonly string _root;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    public FileWorkItemAttachments(
        ISqliteConnectionFactory connections,
        DataDirectory dataDirectory,
        Func<DateTimeOffset>? clock = null,
        ILogger<FileWorkItemAttachments>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _root = Path.Combine(dataDirectory.Path, FolderName);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _logger = logger ?? NullLogger<FileWorkItemAttachments>.Instance;
    }

    /// <inheritdoc />
    public event Action<WorkItemId>? Changed;

    /// <inheritdoc />
    public WorkItemAttachmentResult AddImage(WorkItemId item, string? fileName, byte[] bytes, string addedBy)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(bytes);

        if (FolderFor(item) is not { } folder)
        {
            return WorkItemAttachmentResult.Refused("This item's id cannot name a folder, so nothing was attached.");
        }

        if (bytes.Length == 0)
        {
            return WorkItemAttachmentResult.Refused("The image was empty.");
        }

        if (ImageFormats.Sniff(bytes) is not { } format)
        {
            return WorkItemAttachmentResult.Refused("Only PNG, JPEG, GIF and WebP images can be attached to a work item.");
        }

        if (bytes.Length > AgentImageAttachment.MaxBytes)
        {
            return WorkItemAttachmentResult.Refused(AgentImageAttachment.TooLarge(bytes.Length));
        }

        WorkItemAttachment attachment;
        lock (_gate)
        {
            if (For(item).Count >= IWorkItemAttachments.MaxPerItem)
            {
                return WorkItemAttachmentResult.Refused(
                    $"This item already carries {IWorkItemAttachments.MaxPerItem} files, the most one item may.");
            }

            var id = Guid.NewGuid().ToString("N");
            attachment = new WorkItemAttachment(
                id,
                item,
                DisplayName(fileName, id, format),
                format.MediaType,
                bytes.Length,
                _clock(),
                string.IsNullOrWhiteSpace(addedBy) ? "user" : addedBy.Trim());

            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, id + "." + format.Extension);
            File.WriteAllBytes(path, bytes);

            try
            {
                using var connection = _connections.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO work_item_attachments (id, item_id, file_name, media_type, size_bytes, sha256, added_utc, added_by) "
                    + "VALUES ($id, $item, $name, $type, $size, $hash, $added, $by);";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$item", item.Value);
                command.Parameters.AddWithValue("$name", attachment.FileName);
                command.Parameters.AddWithValue("$type", attachment.MediaType);
                command.Parameters.AddWithValue("$size", attachment.SizeBytes);
                command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(bytes)));
                command.Parameters.AddWithValue("$added", attachment.AddedUtc.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$by", attachment.AddedBy);
                command.ExecuteNonQuery();
            }
            catch
            {
                // A row that could not be written leaves no orphan file behind it.
                TryDelete(path);
                throw;
            }
        }

        Changed?.Invoke(item);
        return new WorkItemAttachmentResult(attachment, null);
    }

    /// <inheritdoc />
    public IReadOnlyList<WorkItemAttachment> For(WorkItemId item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            using var connection = _connections.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, file_name, media_type, size_bytes, added_utc, added_by FROM work_item_attachments "
                + "WHERE item_id = $item ORDER BY added_utc, id;";
            command.Parameters.AddWithValue("$item", item.Value);
            using var reader = command.ExecuteReader();
            var found = new List<WorkItemAttachment>();
            while (reader.Read())
            {
                found.Add(new WorkItemAttachment(
                    reader.GetString(0),
                    item,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetString(5)));
            }

            return found;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or FormatException)
        {
            _logger.LogWarning(ex, "Could not read the attachments of work item {Item}.", item.Value);
            return [];
        }
    }

    /// <inheritdoc />
    public byte[]? Read(WorkItemId item, string attachmentId)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(attachmentId) || !attachmentId.All(char.IsAsciiLetterOrDigit))
        {
            return null;
        }

        if (FolderFor(item) is not { } folder
            || For(item).FirstOrDefault(a => string.Equals(a.Id, attachmentId, StringComparison.Ordinal)) is not { } row
            || ImageFormats.SupportedMediaTypes.Contains(row.MediaType, StringComparer.Ordinal) is false)
        {
            return null;
        }

        var extension = new[] { ImageFormats.Png, ImageFormats.Jpeg, ImageFormats.Gif, ImageFormats.Webp }
            .First(f => string.Equals(f.MediaType, row.MediaType, StringComparison.Ordinal)).Extension;
        var path = Path.Combine(folder, attachmentId + "." + extension);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            return ImageFormats.Sniff(bytes) is { } format
                && string.Equals(format.MediaType, row.MediaType, StringComparison.Ordinal)
                && bytes.Length <= AgentImageAttachment.MaxBytes
                    ? bytes
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read attachment {Attachment} of work item {Item}.", attachmentId, item.Value);
            return null;
        }
    }

    /// <summary>The item's own folder, or null when its id is not one that can safely name a folder.</summary>
    private string? FolderFor(WorkItemId item) =>
        item.Value.Length is > 0 and <= 64 && item.Value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? Path.Combine(_root, item.Value)
            : null;

    /// <summary>A display name only: path parts and odd characters are dropped, and a missing one is made up.</summary>
    private static string DisplayName(string? fileName, string id, ImageFormat format)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        var cleaned = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        if (cleaned.Length > 120)
        {
            cleaned = cleaned[..120];
        }

        return cleaned.Length == 0 ? $"image-{id[..8]}.{format.Extension}" : cleaned;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: an orphan file is dead weight, never served (a read needs its row).
        }
    }
}
