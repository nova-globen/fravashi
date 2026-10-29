using Fravashi.Infrastructure.Repository;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fravashi.Infrastructure.Tests;

public sealed class LocalRepositoryReaderTests : IDisposable
{
    private readonly string _root;

    public LocalRepositoryReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fravashi-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "README.md"), "# Hello");
        File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "class A { }");
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "config"), "ignored");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private LocalRepositoryReader Reader() =>
        new(Options.Create(new RepositoryReaderOptions { Root = _root }));

    [Fact]
    public async Task Root_tree_lists_entries_dirs_first_and_hides_denied_directories()
    {
        var tree = await Reader().GetTreeAsync(string.Empty);

        tree.IsDirectory.Should().BeTrue();
        tree.Children.Select(c => c.Name).Should().Contain(["src", "README.md"]);
        tree.Children.Select(c => c.Name).Should().NotContain(".git");
        tree.Children[0].IsDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task Subdirectory_lists_its_children()
    {
        var node = await Reader().GetTreeAsync("src");
        node.Children.Should().ContainSingle(c => c.Name == "Program.cs");
    }

    [Fact]
    public async Task Read_file_returns_content_and_language_hint()
    {
        var file = await Reader().ReadFileAsync("src/Program.cs");

        file.Should().NotBeNull();
        file!.Content.Should().Contain("class A");
        file.Language.Should().Be("csharp");
    }

    [Fact]
    public async Task Traversal_outside_the_root_is_blocked()
    {
        (await Reader().ReadFileAsync("../escape.txt")).Should().BeNull();
        (await Reader().GetTreeAsync("../..")).Children.Should().BeEmpty();
    }
}
