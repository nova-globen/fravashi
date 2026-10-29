using System.Globalization;
using System.Text;

namespace Fravashi.Infrastructure.Embeddings;

/// <summary>
/// The uncased BERT tokenizer the pinned sentence model was trained with (backlog 40): basic tokenization
/// (clean, lower-case, strip accents, split punctuation, isolate CJK characters) followed by greedy
/// longest-match WordPiece over the model's own <c>vocab.txt</c>.
/// <para>
/// Written here rather than taken from a package because it is about a hundred lines, it has a reference
/// to be checked against (the ids the model's own tokenizer produces for a known sentence, held by a
/// test), and a second native-adjacent dependency would have had to clear the licence gate and be proven
/// in the image for no gain. A tokenizer that disagrees with the one the model was trained with does not
/// fail: it produces plausible vectors that retrieve worse, which is why the reference ids matter.
/// </para>
/// </summary>
public sealed class WordPieceTokenizer
{
    private const int MaxCharactersPerWord = 100;

    private readonly Dictionary<string, int> _vocabulary;

    public WordPieceTokenizer(IEnumerable<string> vocabularyLines)
    {
        ArgumentNullException.ThrowIfNull(vocabularyLines);
        _vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        foreach (var line in vocabularyLines)
        {
            // The id IS the line number, so a blank line still consumes one.
            _vocabulary.TryAdd(line.TrimEnd('\r', '\n'), index);
            index++;
        }

        Classification = Id("[CLS]");
        Separator = Id("[SEP]");
        Unknown = Id("[UNK]");
        Padding = Id("[PAD]");
    }

    /// <summary>The id that opens every sequence.</summary>
    public int Classification { get; }

    /// <summary>The id that closes every sequence.</summary>
    public int Separator { get; }

    /// <summary>The id of a word the vocabulary cannot spell.</summary>
    public int Unknown { get; }

    /// <summary>The padding id.</summary>
    public int Padding { get; }

    /// <summary>How many entries the vocabulary has.</summary>
    public int VocabularySize => _vocabulary.Count;

    /// <summary>The WordPiece ids of a text, WITHOUT the opening and closing markers.</summary>
    public IReadOnlyList<int> Pieces(string? text)
    {
        var ids = new List<int>();
        foreach (var word in BasicTokens(text ?? string.Empty))
        {
            AppendWordPieces(word, ids);
        }

        return ids;
    }

    /// <summary>A whole model input: opening marker, at most <paramref name="maxLength"/> minus two pieces, closing marker.</summary>
    public int[] Encode(IReadOnlyList<int> pieces, int start, int count, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        var take = Math.Clamp(count, 0, Math.Max(0, maxLength - 2));
        var ids = new int[take + 2];
        ids[0] = Classification;
        for (var i = 0; i < take; i++)
        {
            ids[i + 1] = pieces[start + i];
        }

        ids[^1] = Separator;
        return ids;
    }

    private int Id(string token) =>
        _vocabulary.TryGetValue(token, out var id)
            ? id
            : throw new InvalidDataException($"The vocabulary has no '{token}' entry, so it is not a BERT vocabulary.");

    private void AppendWordPieces(string word, List<int> ids)
    {
        if (word.Length > MaxCharactersPerWord)
        {
            ids.Add(Unknown);
            return;
        }

        var pieces = new List<int>();
        var start = 0;
        while (start < word.Length)
        {
            var end = word.Length;
            var found = -1;
            while (start < end)
            {
                var candidate = (start > 0 ? "##" : string.Empty) + word[start..end];
                if (_vocabulary.TryGetValue(candidate, out var id))
                {
                    found = id;
                    break;
                }

                end--;
            }

            if (found < 0)
            {
                // One unspellable piece makes the whole word unknown, as the reference does.
                ids.Add(Unknown);
                return;
            }

            pieces.Add(found);
            start = end;
        }

        ids.AddRange(pieces);
    }

    private static IEnumerable<string> BasicTokens(string text)
    {
        var cleaned = new StringBuilder(text.Length);
        foreach (var rune in text.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (rune.Value == 0 || rune.Value == 0xFFFD || category == UnicodeCategory.NonSpacingMark)
            {
                continue; // strip accents (after NFD) and the two characters the reference drops outright
            }

            if (Rune.IsWhiteSpace(rune))
            {
                cleaned.Append(' ');
            }
            else if (category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                continue;
            }
            else if (IsCjk(rune.Value) || IsPunctuation(rune, category))
            {
                cleaned.Append(' ').Append(rune.ToString()).Append(' ');
            }
            else
            {
                cleaned.Append(Rune.ToLowerInvariant(rune).ToString());
            }
        }

        return cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    // The reference treats every non-letter, non-digit ASCII symbol as punctuation ("$" and "^" included,
    // which Unicode files under symbols), plus everything Unicode calls punctuation.
    private static bool IsPunctuation(Rune rune, UnicodeCategory category)
    {
        var v = rune.Value;
        if ((v >= 33 && v <= 47) || (v >= 58 && v <= 64) || (v >= 91 && v <= 96) || (v >= 123 && v <= 126))
        {
            return true;
        }

        return category is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }

    private static bool IsCjk(int v) =>
        (v >= 0x4E00 && v <= 0x9FFF) || (v >= 0x3400 && v <= 0x4DBF) || (v >= 0x20000 && v <= 0x2A6DF)
        || (v >= 0x2A700 && v <= 0x2B73F) || (v >= 0x2B740 && v <= 0x2B81F) || (v >= 0x2B820 && v <= 0x2CEAF)
        || (v >= 0xF900 && v <= 0xFAFF) || (v >= 0x2F800 && v <= 0x2FA1F);
}
