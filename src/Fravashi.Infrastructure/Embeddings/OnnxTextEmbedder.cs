using Fravashi.Application.Embeddings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Fravashi.Infrastructure.Embeddings;

/// <summary>
/// The pinned sentence model run through ONNX Runtime (backlog 40, decided 2026-09-19). The model
/// directory holds <c>model.onnx</c> and <c>vocab.txt</c>; the image bakes them in at a pinned revision and
/// checksum because the container runs offline. When the directory or either file is missing, or the
/// native runtime cannot load, this reports itself unavailable ONCE in the log and search falls back to
/// words and says so: a developer host has no model and that is not a fault.
/// <para>
/// Pooling is the model's own: the mean of the token vectors, then unit length, so a dot product between
/// two results is their cosine similarity.
/// </para>
/// </summary>
public sealed class OnnxTextEmbedder : ITextEmbedder, IDisposable
{
    /// <summary>The environment variable and configuration key that name the model directory.</summary>
    public const string DirectoryKey = "FRAVASHI_EMBED_MODEL_DIR";

    /// <summary>The model reads at most this many pieces at once, the two markers included.</summary>
    public const int Window = 256;

    /// <summary>Consecutive chunks share this many pieces, so a sentence cut by one boundary is whole in the next.</summary>
    public const int Overlap = 56;

    /// <summary>A document contributes at most this many chunks. Past it, the tail is not searchable by meaning.</summary>
    public const int MaxChunks = 8;

    private readonly string? _directory;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private InferenceSession? _session;
    private WordPieceTokenizer? _tokenizer;
    private bool _tried;

    public OnnxTextEmbedder(string? modelDirectory, string modelName, ILogger<OnnxTextEmbedder>? logger = null)
    {
        _directory = modelDirectory;
        Model = modelName;
        _logger = logger ?? NullLogger<OnnxTextEmbedder>.Instance;
    }

    /// <inheritdoc />
    public string Model { get; }

    /// <inheritdoc />
    public bool IsAvailable => Load();

    /// <inheritdoc />
    public IReadOnlyList<float[]> Embed(string text)
    {
        if (!Load() || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var pieces = _tokenizer!.Pieces(text);
        if (pieces.Count == 0)
        {
            return [];
        }

        var body = Window - 2;
        var vectors = new List<float[]>();
        for (var start = 0; start < pieces.Count && vectors.Count < MaxChunks; start += body - Overlap)
        {
            var count = Math.Min(body, pieces.Count - start);
            vectors.Add(Run(_tokenizer.Encode(pieces, start, count, Window)));
            if (start + count >= pieces.Count)
            {
                break;
            }
        }

        return vectors;
    }

    public void Dispose() => _session?.Dispose();

    private float[] Run(int[] ids)
    {
        var length = ids.Length;
        var inputIds = new DenseTensor<long>(new[] { 1, length });
        var mask = new DenseTensor<long>(new[] { 1, length });
        var types = new DenseTensor<long>(new[] { 1, length });
        for (var i = 0; i < length; i++)
        {
            inputIds[0, i] = ids[i];
            mask[0, i] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };
        if (_session!.InputMetadata.ContainsKey("token_type_ids"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));
        }

        using var results = _session.Run(inputs);
        var hidden = results.First().AsTensor<float>();
        var width = hidden.Dimensions[2];
        var pooled = new float[width];
        for (var t = 0; t < length; t++)
        {
            for (var d = 0; d < width; d++)
            {
                pooled[d] += hidden[0, t, d];
            }
        }

        double norm = 0;
        for (var d = 0; d < width; d++)
        {
            pooled[d] /= length;
            norm += pooled[d] * pooled[d];
        }

        var scale = norm > 0 ? (float)(1.0 / Math.Sqrt(norm)) : 0f;
        for (var d = 0; d < width; d++)
        {
            pooled[d] *= scale;
        }

        return pooled;
    }

    private bool Load()
    {
        if (_tried)
        {
            return _session is not null;
        }

        lock (_gate)
        {
            if (_tried)
            {
                return _session is not null;
            }

            try
            {
                var model = _directory is null ? null : Path.Combine(_directory, "model.onnx");
                var vocabulary = _directory is null ? null : Path.Combine(_directory, "vocab.txt");
                if (model is null || !File.Exists(model) || !File.Exists(vocabulary))
                {
                    _logger.LogInformation(
                        "No embedding model at '{Directory}': wiki search matches words, not meaning.", _directory);
                }
                else
                {
                    _tokenizer = new WordPieceTokenizer(File.ReadLines(vocabulary!));
                    _session = new InferenceSession(model);
                }
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or IOException
                or InvalidDataException or TypeInitializationException or BadImageFormatException)
            {
                _logger.LogWarning(ex, "The embedding model could not be loaded: wiki search matches words, not meaning.");
                _session = null;
            }

            _tried = true;
            return _session is not null;
        }
    }
}
