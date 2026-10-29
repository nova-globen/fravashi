namespace Fravashi.Application.Embeddings;

/// <summary>
/// Turns text into vectors whose closeness means closeness of MEANING (backlog 40). A port, because the
/// model is a file baked into the image and is not there on a developer's host or in most tests: an
/// embedder that is not available is a legitimate state that callers say out loud and work without,
/// never an exception.
/// </summary>
public interface ITextEmbedder
{
    /// <summary>Which model produced the vectors. Stored beside each one, so a changed model re-indexes.</summary>
    string Model { get; }

    /// <summary>Whether the model could be loaded.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// One unit-length vector per chunk of the text, in order, or empty when unavailable. A long text is
    /// cut into overlapping chunks because the model reads a bounded window, and a document that is about
    /// something only in its third screen has to be findable by that.
    /// </summary>
    IReadOnlyList<float[]> Embed(string text);
}
