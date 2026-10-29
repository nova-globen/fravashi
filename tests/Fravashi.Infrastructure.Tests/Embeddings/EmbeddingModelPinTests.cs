using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Embeddings;

/// <summary>
/// The embedding model is redistributed inside the image, so it is pinned like the agent CLIs: an exact
/// revision and a checksum per file, in the real Dockerfile, read here (backlog 40 and 73). The second test is
/// what ties the evidence to the pin: every claim the live tests make about "the real model" is a claim about
/// the files they ran against, and it only transfers to the image if those are byte for byte the pinned ones.
/// </summary>
public sealed class EmbeddingModelPinTests
{
    private static string Dockerfile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fravashi.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests run from inside the repository");
        return File.ReadAllText(Path.Combine(directory!.FullName, "Dockerfile"));
    }

    private static string Arg(string name)
    {
        var match = Regex.Match(Dockerfile(), $@"^ARG {Regex.Escape(name)}=(\S+)\s*$", RegexOptions.Multiline);
        match.Success.Should().BeTrue($"the Dockerfile should declare ARG {name}");
        return match.Groups[1].Value;
    }

    [Fact]
    public void The_image_pins_the_model_to_an_exact_revision_and_checks_each_file()
    {
        Arg("EMBED_MODEL_REPO").Should().Be("sentence-transformers/all-MiniLM-L6-v2");
        Arg("EMBED_MODEL_REVISION").Should().MatchRegex("^[0-9a-f]{40}$", "a branch name is not a pin");
        Arg("EMBED_MODEL_SHA256").Should().MatchRegex("^[0-9a-f]{64}$");
        Arg("EMBED_VOCAB_SHA256").Should().MatchRegex("^[0-9a-f]{64}$");

        var text = Dockerfile();
        text.Should().Contain("resolve/${EMBED_MODEL_REVISION}/", "the download has to name the revision, not main");
        text.Should().Contain("\"${EMBED_MODEL_SHA256}  model.onnx\" | sha256sum -c -");
        text.Should().Contain("\"${EMBED_VOCAB_SHA256}  vocab.txt\" | sha256sum -c -");

        // The name stored beside every vector carries the revision, so re-pinning re-indexes by itself.
        text.Should().Contain("ENV FRAVASHI_EMBED_MODEL=all-MiniLM-L6-v2@${EMBED_MODEL_REVISION}");
    }

    [RealEmbeddingModelFact]
    public void The_model_the_live_tests_ran_against_is_byte_for_byte_the_pinned_one()
    {
        Hash(Path.Combine(RealEmbeddingModelFactAttribute.Directory, "model.onnx")).Should().Be(Arg("EMBED_MODEL_SHA256"));
        Hash(Path.Combine(RealEmbeddingModelFactAttribute.Directory, "vocab.txt")).Should().Be(Arg("EMBED_VOCAB_SHA256"));
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
