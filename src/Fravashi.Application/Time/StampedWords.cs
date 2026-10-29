using System.Globalization;
using System.Text;

namespace Fravashi.Application.Time;

/// <summary>One piece of a <see cref="StampedWords"/> sentence: text, or an instant.</summary>
public abstract record WordPart;

/// <summary>Text, said as it is.</summary>
public sealed record TextPart(string Text) : WordPart;

/// <summary>
/// An instant in the sentence (backlog 246). A page formats it in the PERSON's time zone; <see cref="StampedWords.Plain"/>
/// formats it the way the sentence always was for a log line or an agent.
/// </summary>
/// <param name="At">The instant.</param>
/// <param name="Seconds">True when the sentence says it to the second ("14:04:15").</param>
/// <param name="PlainAsWritten">
/// True when the plain words format the instant in its own offset (<c>{Since:HH:mm}</c>) rather than converting it to
/// the process's zone first (<c>ToLocalTime()</c>), which is how each sentence happened to be written before 246.
/// </param>
public sealed record InstantPart(DateTimeOffset At, bool Seconds = false, bool PlainAsWritten = false) : WordPart;

/// <summary>
/// A sentence that carries instants (backlog 246, plan @70). The server cannot know the person's time zone, so a sentence
/// that names a time is kept as parts: a page renders each instant in the person's zone, and <see cref="Plain"/> is the
/// sentence a log line or an agent tool returns, unchanged from what it said before. Building both from the same parts
/// is what keeps everything but the time identical between the two.
/// </summary>
public sealed class StampedWords
{
    private StampedWords(IReadOnlyList<WordPart> parts) => Parts = parts;

    /// <summary>The pieces, in order.</summary>
    public IReadOnlyList<WordPart> Parts { get; }

    /// <summary>A sentence of the given pieces: a string is text, a <see cref="WordPart"/> or another sentence is spliced in.</summary>
    public static StampedWords Of(params object?[] pieces)
    {
        var parts = new List<WordPart>();
        foreach (var piece in pieces)
        {
            switch (piece)
            {
                case null:
                    break;
                case string text when text.Length > 0:
                    parts.Add(new TextPart(text));
                    break;
                case string:
                    break;
                case WordPart part:
                    parts.Add(part);
                    break;
                case StampedWords words:
                    parts.AddRange(words.Parts);
                    break;
                default:
                    throw new ArgumentException($"A sentence piece is text, a part or a sentence, not {piece.GetType().Name}.", nameof(pieces));
            }
        }

        return new StampedWords(parts);
    }

    /// <summary>An instant said to the minute.</summary>
    public static InstantPart At(DateTimeOffset at, bool plainAsWritten = false) => new(at, false, plainAsWritten);

    /// <summary>An instant said to the second.</summary>
    public static InstantPart AtSeconds(DateTimeOffset at, bool plainAsWritten = false) => new(at, true, plainAsWritten);

    /// <summary>The same sentence with <paramref name="characters"/> trimmed off the end of its last text.</summary>
    public StampedWords TrimEnd(params char[] characters)
    {
        if (Parts.Count == 0 || Parts[^1] is not TextPart last)
        {
            return this;
        }

        var trimmed = last.Text.TrimEnd(characters);
        var parts = Parts.Take(Parts.Count - 1).ToList();
        if (trimmed.Length > 0)
        {
            parts.Add(new TextPart(trimmed));
        }

        return new StampedWords(parts);
    }

    /// <summary>
    /// The sentence for a log line or an agent: each instant as it was always formatted, "HH:mm" (or "HH:mm:ss") in the
    /// process's own zone, which in the studio's container is UTC.
    /// </summary>
    public string Plain()
    {
        var text = new StringBuilder();
        foreach (var part in Parts)
        {
            text.Append(part switch
            {
                TextPart t => t.Text,
                InstantPart i => (i.PlainAsWritten ? i.At : i.At.ToLocalTime())
                    .ToString(i.Seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture),
                _ => string.Empty,
            });
        }

        return text.ToString();
    }

    /// <inheritdoc />
    public override string ToString() => Plain();
}
