using AwesomeAssertions;
using Fravashi.Application.Search;
using Fravashi.Infrastructure.Repository;
using Fravashi.Infrastructure.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fravashi.Infrastructure.Tests;

public sealed class RepositorySearchServiceTests : IDisposable
{
    private readonly string _root;

    public RepositorySearchServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fravashi-search-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(_root, "src", "alpha.cs"),
            "class Alpha\n{\n    void AlphaOne() { }\n    void alphaTwo() { }\n}\n");
        File.WriteAllText(Path.Combine(_root, "notes.md"), "Alpha notes: alpha beta ALPHA\n");
        File.WriteAllText(Path.Combine(_root, "node_modules", "pkg", "index.js"), "alpha in dependency\n");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "ignored-by-git.txt\n");
        File.WriteAllText(Path.Combine(_root, "ignored-by-git.txt"), "alpha hidden by gitignore\n");
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

    private RepositorySearchService Service(bool forceManaged, string? ignoreRules = null)
    {
        var settings = new Dictionary<string, string?>();
        if (ignoreRules is not null)
        {
            settings[SearchDefaults.IgnoreRulesKey] = ignoreRules;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = Options.Create(new RepositoryReaderOptions { Root = _root });
        var writer = new LocalRepositoryWriter(options, new RepositoryWriteGate());
        return new RepositorySearchService(
            options, configuration, writer, NullLogger<RepositorySearchService>.Instance, forceManaged);
    }

    public static TheoryData<bool> Engines()
    {
        var data = new TheoryData<bool> { true };
        var onPath = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Any(d => File.Exists(Path.Combine(d.Trim(), "rg.exe")) || File.Exists(Path.Combine(d.Trim(), "rg")));
        if (onPath == true)
        {
            data.Add(false); // rg present on this host: exercise the real engine too
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Finds_literal_matches_case_insensitively_by_default(bool forceManaged)
    {
        var result = await Service(forceManaged).SearchAsync(new SearchQuery("alpha"));

        result.Error.Should().BeNull();
        result.Files.Should().Contain(f => f.RelativePath == "src/alpha.cs");
        var notes = result.Files.Single(f => f.RelativePath == "notes.md");
        notes.Matches.Should().HaveCount(3); // Alpha, alpha, ALPHA
        notes.Matches[0].Line.Should().Be(1);
        notes.Matches[0].LineText.Should().Contain("Alpha notes");
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Case_sensitive_and_whole_word_narrow_the_matches(bool forceManaged)
    {
        var sensitive = await Service(forceManaged).SearchAsync(new SearchQuery("ALPHA", CaseSensitive: true));
        sensitive.TotalMatches.Should().Be(1);

        var whole = await Service(forceManaged).SearchAsync(new SearchQuery("alpha", CaseSensitive: true, WholeWord: true));
        whole.Files.Should().ContainSingle(f => f.RelativePath == "notes.md");
        whole.Files.Single().Matches.Should().HaveCount(1); // "alpha" alone; alphaTwo and Alpha excluded
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Regex_mode_matches_patterns_and_reports_bad_patterns_honestly(bool forceManaged)
    {
        var result = await Service(forceManaged).SearchAsync(new SearchQuery(@"Alpha(One|Two)", IsRegex: true, CaseSensitive: true));
        result.TotalMatches.Should().Be(1);
        result.Files.Single().Matches[0].LineText.Should().Contain("AlphaOne");

        var invalid = await Service(forceManaged).SearchAsync(new SearchQuery("([unclosed", IsRegex: true));
        invalid.Error.Should().NotBeNull();
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Ignore_rules_and_gitignore_are_honored(bool forceManaged)
    {
        var result = await Service(forceManaged).SearchAsync(new SearchQuery("alpha"));

        result.Files.Should().NotContain(f => f.RelativePath.StartsWith("node_modules"));
        result.Files.Should().NotContain(f => f.RelativePath == "ignored-by-git.txt");
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Custom_ignore_rules_from_settings_replace_the_defaults(bool forceManaged)
    {
        // Custom rules that do NOT exclude node_modules: the dependency hit must now appear.
        var result = await Service(forceManaged, ignoreRules: "*.md\n").SearchAsync(new SearchQuery("alpha"));

        result.Files.Should().Contain(f => f.RelativePath == "node_modules/pkg/index.js");
        result.Files.Should().NotContain(f => f.RelativePath == "notes.md");
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Include_and_exclude_globs_scope_the_search(bool forceManaged)
    {
        var onlyCs = await Service(forceManaged).SearchAsync(new SearchQuery("alpha", IncludeGlobs: "*.cs"));
        onlyCs.Files.Should().OnlyContain(f => f.RelativePath.EndsWith(".cs"));

        var noCs = await Service(forceManaged).SearchAsync(new SearchQuery("alpha", ExcludeGlobs: "*.cs"));
        noCs.Files.Should().NotContain(f => f.RelativePath.EndsWith(".cs"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task The_result_cap_truncates_honestly(bool forceManaged)
    {
        var result = await Service(forceManaged).SearchAsync(new SearchQuery("alpha", MaxResults: 2));

        result.TotalMatches.Should().BeLessThanOrEqualTo(2);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Replace_all_rewrites_matching_files_through_the_writer()
    {
        var service = Service(forceManaged: true);

        var result = await service.ReplaceAsync(
            new SearchQuery("alpha", CaseSensitive: true, WholeWord: true), "omega", ["notes.md"]);

        result.Error.Should().BeNull();
        result.FilesChanged.Should().Be(1);
        result.OccurrencesReplaced.Should().Be(1);
        File.ReadAllText(Path.Combine(_root, "notes.md")).Should().Contain("omega beta").And.Contain("Alpha notes");
    }

    [Fact]
    public async Task Replace_at_a_single_position_touches_only_that_occurrence()
    {
        var service = Service(forceManaged: true);

        // "alpha" (lowercase, whole word) sits at line 1 column 14 of notes.md.
        var search = await service.SearchAsync(new SearchQuery("alpha", CaseSensitive: true, WholeWord: true));
        var match = search.Files.Single(f => f.RelativePath == "notes.md").Matches.Single();

        var result = await service.ReplaceAsync(
            new SearchQuery("alpha", CaseSensitive: true, WholeWord: true), "omega",
            ["notes.md"], (match.Line, match.Column));

        result.OccurrencesReplaced.Should().Be(1);
        var text = File.ReadAllText(Path.Combine(_root, "notes.md"));
        text.Should().Contain("omega beta");
        text.Should().Contain("Alpha notes"); // untouched
    }

    [Fact]
    public async Task Regex_replace_supports_group_substitutions()
    {
        var service = Service(forceManaged: true);

        var result = await service.ReplaceAsync(
            new SearchQuery(@"Alpha(One|Two)", IsRegex: true, CaseSensitive: true), "Beta$1", ["src/alpha.cs"]);

        result.OccurrencesReplaced.Should().Be(1);
        File.ReadAllText(Path.Combine(_root, "src", "alpha.cs")).Should().Contain("BetaOne");
    }

    [Fact]
    public async Task Literal_replacement_text_is_never_treated_as_a_substitution_pattern()
    {
        var service = Service(forceManaged: true);

        var result = await service.ReplaceAsync(
            new SearchQuery("beta"), "$1 literal", ["notes.md"]);

        result.OccurrencesReplaced.Should().Be(1);
        File.ReadAllText(Path.Combine(_root, "notes.md")).Should().Contain("$1 literal");
    }

    [Fact]
    public async Task Restore_undoes_a_replace_by_writing_the_pre_images_back()
    {
        var service = Service(forceManaged: true);
        var before = File.ReadAllText(Path.Combine(_root, "notes.md"));

        var replace = await service.ReplaceAsync(
            new SearchQuery("alpha", CaseSensitive: true, WholeWord: true), "omega", ["notes.md"]);
        replace.Files.Should().ContainSingle().Which.PreviousContent.Should().Be(before);

        var restore = await service.RestoreAsync(replace.Files!);

        restore.Error.Should().BeNull();
        restore.FilesChanged.Should().Be(1);
        File.ReadAllText(Path.Combine(_root, "notes.md")).Should().Be(before);
    }

    [Fact]
    public async Task Restore_refuses_a_file_that_changed_again_after_the_replace()
    {
        var service = Service(forceManaged: true);

        var replace = await service.ReplaceAsync(
            new SearchQuery("alpha", CaseSensitive: true, WholeWord: true), "omega", ["notes.md"]);

        // The file moves on after the replace; the undo must not clobber the newer content.
        var path = Path.Combine(_root, "notes.md");
        File.WriteAllText(path, "rewritten since the replace");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        var restore = await service.RestoreAsync(replace.Files!);

        restore.FilesChanged.Should().Be(0);
        restore.Error.Should().Contain("left as it is");
        File.ReadAllText(path).Should().Be("rewritten since the replace");
    }
}
