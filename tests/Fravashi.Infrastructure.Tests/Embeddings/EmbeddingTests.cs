using AwesomeAssertions;
using Fravashi.Infrastructure.Embeddings;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Embeddings;

/// <summary>
/// Runs only when <c>FRAVASHI_EMBED_MODEL_DIR</c> names a folder holding the pinned model's
/// <c>model.onnx</c> and <c>vocab.txt</c>, and says so when it does not. The model is 90 MB of weights
/// baked into the image, so it is not in the repository and a plain test run has none.
/// </summary>
internal sealed class RealEmbeddingModelFactAttribute : FactAttribute
{
    public RealEmbeddingModelFactAttribute()
    {
        var directory = Environment.GetEnvironmentVariable(OnnxTextEmbedder.DirectoryKey);
        if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "model.onnx")))
        {
            Skip = $"No embedding model: set {OnnxTextEmbedder.DirectoryKey} to a folder with model.onnx and vocab.txt.";
        }
    }

    public static string Directory => Environment.GetEnvironmentVariable(OnnxTextEmbedder.DirectoryKey)!;
}

public sealed class WordPieceTokenizerTests
{
    // A vocabulary small enough to read, with the five markers where a BERT vocabulary has them.
    private static readonly string[] Vocabulary =
        ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", "un", "##att", "##ended", "change", "changes", ",", "cafe", "a"];

    private static WordPieceTokenizer Tokenizer() => new(Vocabulary);

    [Fact]
    public void A_word_is_spelled_from_the_longest_pieces_the_vocabulary_has() =>
        Tokenizer().Pieces("unattended").Should().Equal(5, 6, 7);

    [Fact]
    public void Case_accents_and_punctuation_are_handled_the_way_the_model_was_trained() =>
        Tokenizer().Pieces("Café, CHANGE").Should().Equal(11, 10, 8);

    [Fact]
    public void A_word_with_one_unspellable_piece_is_unknown_as_a_whole() =>
        Tokenizer().Pieces("unknowable change").Should().Equal(1, 8);

    [Fact]
    public void An_input_is_wrapped_in_the_two_markers_and_cut_to_the_window()
    {
        var tokenizer = Tokenizer();
        var pieces = tokenizer.Pieces("change changes change changes");

        tokenizer.Encode(pieces, 0, pieces.Count, maxLength: 4).Should().Equal(2, 8, 9, 3);
    }

    [Fact]
    public void A_file_that_is_not_a_bert_vocabulary_is_refused_rather_than_tokenized_wrongly()
    {
        var act = () => new WordPieceTokenizer(["hello", "world"]);

        act.Should().Throw<InvalidDataException>();
    }

    [RealEmbeddingModelFact]
    public void The_real_vocabulary_produces_the_reference_ids()
    {
        // The ids the model's own tokenizer gives for this sentence, which is the only thing that can show
        // this tokenizer and that one agree. 101 and 102 are the markers.
        var tokenizer = new WordPieceTokenizer(
            File.ReadLines(Path.Combine(RealEmbeddingModelFactAttribute.Directory, "vocab.txt")));
        var pieces = tokenizer.Pieces("Hello, world!");

        tokenizer.VocabularySize.Should().Be(30522);
        tokenizer.Encode(pieces, 0, pieces.Count, 256).Should().Equal(101, 7592, 1010, 2088, 999, 102);
    }
}

public sealed class OnnxTextEmbedderTests
{
    [Fact]
    public void With_no_model_it_is_unavailable_and_embeds_nothing_instead_of_throwing()
    {
        using var embedder = new OnnxTextEmbedder(Path.Combine(Path.GetTempPath(), "no-such-model"), "test");

        embedder.IsAvailable.Should().BeFalse();
        embedder.Embed("anything").Should().BeEmpty();
    }

    [RealEmbeddingModelFact]
    public void Meaning_is_closer_than_wording()
    {
        using var embedder = new OnnxTextEmbedder(RealEmbeddingModelFactAttribute.Directory, "all-MiniLM-L6-v2");
        embedder.IsAvailable.Should().BeTrue();

        // Item 42's measured failure, as a test: the question shares no phrase with the right document.
        var question = embedder.Embed("what happens to an unattended change")[0];
        var right = embedder.Embed("When nobody is watching, an edit made by an autonomous agent is held until a person reviews it.")[0];
        var wrong = embedder.Embed("The brand colours are blue and grey, and the logo is an SVG file.")[0];

        question.Should().HaveCount(384);
        Dot(question, question).Should().BeApproximately(1f, 1e-3f, "vectors are unit length");
        Dot(question, right).Should().BeGreaterThan(Dot(question, wrong) + 0.15f);
    }

    [RealEmbeddingModelFact]
    public void A_long_text_becomes_several_chunks_and_never_more_than_the_cap()
    {
        using var embedder = new OnnxTextEmbedder(RealEmbeddingModelFactAttribute.Directory, "all-MiniLM-L6-v2");
        var paragraph = string.Join(' ', Enumerable.Repeat("The rollout plan covers the staging environment first.", 40));

        embedder.Embed(paragraph).Count.Should().BeInRange(2, OnnxTextEmbedder.MaxChunks);
        embedder.Embed(string.Join(' ', Enumerable.Repeat(paragraph, 20))).Should().HaveCount(OnnxTextEmbedder.MaxChunks);
    }

    private static float Dot(float[] a, float[] b)
    {
        float sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}
