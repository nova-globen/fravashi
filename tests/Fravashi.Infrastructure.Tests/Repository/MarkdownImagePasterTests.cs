using AwesomeAssertions;
using Fravashi.Application.Repository;
using Fravashi.Infrastructure.Repository;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fravashi.Infrastructure.Tests;

/// <summary>
/// Backlog 26, the editor half: an image pasted into a markdown file is saved beside it through the REAL
/// root-bounded writer and a relative link comes back to insert.
/// </summary>
public sealed class MarkdownImagePasterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 21, 4, 5, TimeSpan.Zero);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xEF, 0xBB, 0xBF, 0xFF];

    private readonly string _root;
    private readonly MarkdownImagePaster _paster;

    public MarkdownImagePasterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fravashi-mdpaste-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "docs", "guide"));
        File.WriteAllText(Path.Combine(_root, "docs", "guide", "setup.md"), "# Setup\n");
        var writer = new LocalRepositoryWriter(
            Options.Create(new RepositoryReaderOptions { Root = _root }), new RepositoryWriteGate());
        _paster = new MarkdownImagePaster(writer, () => Now);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup only.
        }
    }

    [Fact]
    public async Task The_image_is_saved_beside_the_file_byte_for_byte_and_the_link_is_relative()
    {
        var result = await _paster.PasteAsync("docs/guide/setup.md", Png);

        result.Succeeded.Should().BeTrue(result.Error);
        result.SavedPath.Should().Be("docs/guide/pasted-image-20260915-210405.png");
        result.Markdown.Should().Be("![pasted-image-20260915-210405](pasted-image-20260915-210405.png)");
        File.ReadAllBytes(Path.Combine(_root, "docs", "guide", "pasted-image-20260915-210405.png"))
            .Should().Equal(Png, "binary bytes must not gain a BOM or any text treatment");
    }

    [Fact]
    public async Task A_second_paste_in_the_same_second_never_overwrites_the_first()
    {
        var first = await _paster.PasteAsync("docs/guide/setup.md", Png);
        var second = await _paster.PasteAsync("docs/guide/setup.md", [0xFF, 0xD8, 0xFF, 0xE0, 1]);
        var third = await _paster.PasteAsync("docs/guide/setup.md", Png);

        second.SavedPath.Should().Be("docs/guide/pasted-image-20260915-210405.jpg", "a different format is a different name");
        third.SavedPath.Should().Be("docs/guide/pasted-image-20260915-210405-2.png");
        File.ReadAllBytes(Path.Combine(_root, first.SavedPath!)).Should().Equal(Png);
    }

    [Fact]
    public async Task A_file_at_the_repository_root_gets_its_image_at_the_root()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), "hi");

        var result = await _paster.PasteAsync("README.md", Png);

        result.SavedPath.Should().Be("pasted-image-20260915-210405.png");
        File.Exists(Path.Combine(_root, "pasted-image-20260915-210405.png")).Should().BeTrue();
    }

    [Theory]
    [InlineData("src/Program.cs", "markdown files only")]
    [InlineData("../outside.md", "outside")]
    public async Task Nothing_is_written_for_a_file_that_is_not_markdown_or_not_in_the_repository(string path, string error)
    {
        var result = await _paster.PasteAsync(path, Png);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(error);
        Directory.GetFiles(Path.GetDirectoryName(_root)!, "pasted-image-*", SearchOption.TopDirectoryOnly).Should().BeEmpty();
    }

    [Fact]
    public async Task Bytes_that_are_not_an_image_are_refused()
    {
        var result = await _paster.PasteAsync("docs/guide/setup.md", "<svg/>"u8.ToArray());

        result.Succeeded.Should().BeFalse();
        Directory.GetFiles(Path.Combine(_root, "docs", "guide")).Should().ContainSingle();
    }

    [Theory]
    [InlineData("a.md", true)]
    [InlineData("docs/B.MARKDOWN", true)]
    [InlineData("page.mdx", true)]
    [InlineData("notes.txt", false)]
    [InlineData(null, false)]
    public void Markdown_is_recognised_by_extension(string? path, bool expected) =>
        MarkdownImagePaster.IsMarkdown(path).Should().Be(expected);
}
