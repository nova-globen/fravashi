namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// An image the user pasted into a turn (backlog 26): sent to the agent as an image content block and kept
/// with the transcript so the conversation still shows it after a reload.
/// <para>
/// The media type is NEVER taken from the browser. It is read off the bytes' own signature, because the CLI
/// forwards <c>media_type</c> to the model provider as stated and a mislabelled image is refused there, and
/// because the clipboard's type is whatever the copying application claimed.
/// </para>
/// </summary>
/// <param name="MediaType">One of <see cref="ImageFormats.SupportedMediaTypes"/>, as sniffed from the bytes.</param>
/// <param name="Base64">The image bytes, base64 encoded exactly as the CLI's image block carries them.</param>
public sealed record AgentImageAttachment(string MediaType, string Base64)
{
    /// <summary>
    /// The largest image a turn may carry, in raw bytes. The provider's per-image ceiling is 5 MB of base64,
    /// which is 3.75 MB raw; this stays under it with margin so an image the composer accepted is never one the
    /// provider refuses mid-turn. It also bounds what a transcript row holds.
    /// </summary>
    public const int MaxBytes = 3_500_000;

    /// <summary>How many images one turn may carry: enough for a before-and-after, few enough to stay a turn.</summary>
    public const int MaxPerTurn = 4;

    /// <summary>The decoded size in bytes, without decoding.</summary>
    public long ByteCount
    {
        get
        {
            if (Base64.Length == 0)
            {
                return 0;
            }

            var padding = Base64.EndsWith("==", StringComparison.Ordinal) ? 2 : Base64.EndsWith('=') ? 1 : 0;
            return (Base64.Length / 4 * 3) - padding;
        }
    }

    /// <summary>A <c>data:</c> URI for rendering the image in the studio's own markup.</summary>
    public string DataUri => $"data:{MediaType};base64,{Base64}";

    /// <summary>
    /// Builds an attachment from pasted bytes, or says in the user's words why it cannot be one: an empty
    /// paste, a format the model cannot read, or an image over <see cref="MaxBytes"/>.
    /// </summary>
    public static AgentImageAttachment? TryCreate(ReadOnlySpan<byte> bytes, out string? error)
    {
        if (bytes.IsEmpty)
        {
            error = "The pasted image was empty.";
            return null;
        }

        if (ImageFormats.Sniff(bytes) is not { } format)
        {
            error = "Only PNG, JPEG, GIF and WebP images can be sent to the agent.";
            return null;
        }

        if (bytes.Length > MaxBytes)
        {
            error = TooLarge(bytes.Length);
            return null;
        }

        error = null;
        return new AgentImageAttachment(format.MediaType, Convert.ToBase64String(bytes));
    }

    /// <summary>
    /// Reads a stored attachment back, or null when the row is not one this build can trust: an unsupported
    /// type, data that is not base64, or bytes whose signature does not match the stated type.
    /// </summary>
    public static AgentImageAttachment? FromStored(string? mediaType, string? base64)
    {
        if (string.IsNullOrEmpty(mediaType) || string.IsNullOrEmpty(base64))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }

        return ImageFormats.Sniff(bytes) is { } format && string.Equals(format.MediaType, mediaType, StringComparison.Ordinal)
            ? new AgentImageAttachment(format.MediaType, base64)
            : null;
    }

    /// <summary>The refusal for an image over <see cref="MaxBytes"/>, in one wording wherever it is checked.</summary>
    public static string TooLarge(long bytes) =>
        $"The image is {Megabytes(bytes)} and the limit is {Megabytes(MaxBytes)}.";

    private static string Megabytes(long bytes) =>
        (bytes / 1_000_000d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
}

/// <summary>An image format the studio accepts, with the extension a saved copy gets.</summary>
public sealed record ImageFormat(string MediaType, string Extension);

/// <summary>Recognises the image formats the model provider reads, by their file signatures.</summary>
public static class ImageFormats
{
    public static readonly ImageFormat Png = new("image/png", "png");
    public static readonly ImageFormat Jpeg = new("image/jpeg", "jpg");
    public static readonly ImageFormat Gif = new("image/gif", "gif");
    public static readonly ImageFormat Webp = new("image/webp", "webp");

    public static IReadOnlyList<string> SupportedMediaTypes { get; } =
        [Png.MediaType, Jpeg.MediaType, Gif.MediaType, Webp.MediaType];

    /// <summary>The format the bytes announce, or null when they are none of the supported ones.</summary>
    public static ImageFormat? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return Png;
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return Jpeg;
        }

        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
        {
            return Gif;
        }

        return bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)
            ? Webp
            : null;
    }
}
