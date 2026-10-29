using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fravashi.Web.Components.Sessions;

/// <summary>A long text pasted into the composer, held as a compact chip instead of filling the box (work item #359).</summary>
/// <param name="Number">Its number in this turn: "Pasted text 1".</param>
/// <param name="Text">What was pasted, whole.</param>
public sealed record PastedText(int Number, string Text)
{
    /// <summary>The reference typed into the composer where it was pasted, and the name its block is sent under.</summary>
    public string Reference => ComposerPastes.TextReference(Number, Text.Length);
}

/// <summary>
/// Pasting into a composer (work item #359, folding in #355): an image is attached and "[Image N]" is typed where it
/// was pasted; a long text (<see cref="LargeCharacters"/> characters or <see cref="LargeLines"/> lines and more) becomes
/// a chip and "[Pasted text N, M characters]" is typed instead; a short paste is typed as it is. What is sent is the
/// person's own words with each pasted text appended as a block wrapped as pasted content and named by its reference,
/// so the agent can tell what the person wrote from what they pasted.
/// <para>
/// Images are numbered across the whole conversation (the composer is told how many the conversation already has), so
/// "[Image 3]" in a turn is the conversation's third image, which is what <c>work_item_attach_image</c> counts.
/// </para>
/// </summary>
public static partial class ComposerPastes
{
    /// <summary>A paste this long or longer, in characters, becomes a chip.</summary>
    public const int LargeCharacters = 1500;

    /// <summary>A paste with this many lines or more becomes a chip, however short its lines are.</summary>
    public const int LargeLines = 30;

    /// <summary>The longest text one paste may carry; anything longer is refused in words, never cut silently.</summary>
    public const int MaxCharacters = 400_000;

    /// <summary>True when a paste is long enough to become a chip rather than be typed.</summary>
    public static bool IsLarge(string? text) =>
        text is not null && (text.Length >= LargeCharacters || Lines(text) >= LargeLines);

    /// <summary>The reference of the conversation's Nth image.</summary>
    public static string ImageReference(int number) =>
        "[Image " + number.ToString(CultureInfo.InvariantCulture) + "]";

    /// <summary>The reference of a pasted text.</summary>
    public static string TextReference(int number, int characters) =>
        "[Pasted text " + number.ToString(CultureInfo.InvariantCulture) + ", "
        + characters.ToString("N0", CultureInfo.InvariantCulture) + " characters]";

    /// <summary>
    /// What is sent: the typed words, then each pasted text whose reference is still in them, wrapped as pasted content
    /// under its reference. A pasted text whose reference the person deleted is not sent: deleting the reference is how
    /// a person takes a paste back out.
    /// </summary>
    public static string Compose(string typed, IReadOnlyList<PastedText> pastes)
    {
        ArgumentNullException.ThrowIfNull(pastes);
        var text = typed ?? string.Empty;
        var kept = pastes.Where(p => text.Contains(p.Reference, StringComparison.Ordinal)).ToList();
        if (kept.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text);
        foreach (var paste in kept)
        {
            builder.Append("\n\n<pasted-content reference=\"").Append(paste.Reference).Append("\">\n")
                .Append(paste.Text.TrimEnd('\r', '\n'))
                .Append("\n</pasted-content>");
        }

        return builder.ToString();
    }

    /// <summary>
    /// The composer's text after the image with this conversation number was taken out of the turn: its reference is
    /// removed and every later image reference of the turn moves down by one, so the numbers keep matching the images.
    /// </summary>
    public static string RemoveImage(string text, int number)
    {
        var without = (text ?? string.Empty).Replace(ImageReference(number), string.Empty, StringComparison.Ordinal);
        return ImageRef().Replace(without, match =>
            int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > number
                ? ImageReference(n - 1)
                : match.Value);
    }

    /// <summary>The composer's text after a pasted text's chip was removed: its reference goes too.</summary>
    public static string RemoveText(string text, PastedText paste)
    {
        ArgumentNullException.ThrowIfNull(paste);
        return (text ?? string.Empty).Replace(paste.Reference, string.Empty, StringComparison.Ordinal);
    }

    private static int Lines(string text)
    {
        var count = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return count;
    }

    [GeneratedRegex(@"\[Image (\d+)\]")]
    private static partial Regex ImageRef();
}
