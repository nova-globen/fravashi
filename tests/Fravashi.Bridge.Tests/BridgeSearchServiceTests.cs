using Fravashi.Bridge;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Bridge.Tests;

public sealed class BridgeSearchServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fravashi-bridge-tests", Guid.NewGuid().ToString("N"));
    private readonly WorkspaceRegistry _registry = new();
    private readonly string _id;

    public BridgeSearchServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "one.cs"), "var needle = 1;\nvar Needle = 2;\n");
        File.WriteAllText(Path.Combine(_root, "readme.md"), "no match here\n");
        File.WriteAllText(Path.Combine(_root, ".git", "config"), "needle inside git dir\n");
        _id = _registry.Register(_root).Id;
    }

    private BridgeSearchService Managed => new(_registry, ripgrepPath: null, forceManagedFallback: true);

    [Fact]
    public void ParseVimgrep_ParsesMatches_AndTruncates()
    {
        const string output = "src/one.cs:1:5:var needle = 1;\nsrc/one.cs:2:5:var Needle = 2;\nreadme.md:9:1:x\n";

        var matches = BridgeSearchService.ParseVimgrep(output, new BridgeSearchRequest("needle", MaxResults: 2));

        matches.Should().HaveCount(2);
        matches[0].Should().Be(new BridgeSearchMatch("src/one.cs", 1, 5, "needle".Length, "var needle = 1;"));
        matches[1].Line.Should().Be(2);
    }

    [Fact]
    public void ParseVimgrep_ReportsTheSamePathWhetherOrNotRipgrepWasToldWhereToLook()
    {
        // Backlog 99, 2026-09-17: the runner now passes an explicit search path, because without one
        // ripgrep reads STANDARD INPUT unless it is a terminal, and the bridge is hosted in a windowed
        // process that has none; it sat at zero CPU until the call timed out. Being told where to look
        // makes ripgrep prefix its output, and a caller must not be able to tell the difference.
        var told = BridgeSearchService.ParseVimgrep(
            "./src/one.cs:1:5:var needle = 1;\n", new BridgeSearchRequest("needle"));
        var untold = BridgeSearchService.ParseVimgrep(
            "src/one.cs:1:5:var needle = 1;\n", new BridgeSearchRequest("needle"));

        told.Should().BeEquivalentTo(untold);
        told.Should().ContainSingle().Which.Path.Should().Be("src/one.cs");
    }

    [Fact]
    public void ParseVimgrep_StripsTheWindowsFormOfThatPrefixToo()
    {
        var matches = BridgeSearchService.ParseVimgrep(
            @".\src\one.cs:1:5:var needle = 1;" + "\n", new BridgeSearchRequest("needle"));

        matches.Should().ContainSingle().Which.Path.Should().Be("src/one.cs");
    }

    [Fact]
    public void ParseVimgrep_InfersRegexMatchLengthFromThePreview()
    {
        const string output = "src/one.cs:1:5:var needle = 1;\n";

        var matches = BridgeSearchService.ParseVimgrep(
            output, new BridgeSearchRequest("n..dle = 1", Regex: true, MaxResults: 10));

        matches.Should().ContainSingle().Which.Length.Should().Be("needle = 1".Length);
    }

    [Fact]
    public async Task ManagedFallback_WholeWord_ExcludesSubstrings()
    {
        File.WriteAllText(Path.Combine(_root, "src", "two.cs"), "var needles = 3;\n");

        var matches = await Managed.SearchAsync(
            _id, new BridgeSearchRequest("needle", WholeWord: true), CancellationToken.None);

        matches.Should().HaveCount(2);
        matches.Should().OnlyContain(m => m.Path == "src/one.cs");
    }

    [Fact]
    public async Task ManagedFallback_IncludeAndExcludeGlobs()
    {
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "needle in notes\n");

        var onlyCs = await Managed.SearchAsync(
            _id, new BridgeSearchRequest("needle", IncludeGlobs: "*.cs"), CancellationToken.None);
        onlyCs.Should().OnlyContain(m => m.Path.EndsWith(".cs"));

        var noSrc = await Managed.SearchAsync(
            _id, new BridgeSearchRequest("needle", ExcludeGlobs: "src/"), CancellationToken.None);
        noSrc.Should().ContainSingle().Which.Path.Should().Be("notes.txt");
    }

    [Fact]
    public async Task ManagedFallback_FindsLiteralMatches_CaseInsensitiveByDefault()
    {
        var matches = await Managed.SearchAsync(_id, new BridgeSearchRequest("needle"), CancellationToken.None);

        matches.Should().HaveCount(2);
        matches.Should().OnlyContain(m => m.Path == "src/one.cs");
    }

    [Fact]
    public async Task ManagedFallback_RespectsCaseSensitivity()
    {
        var matches = await Managed.SearchAsync(
            _id, new BridgeSearchRequest("Needle", CaseSensitive: true), CancellationToken.None);

        matches.Should().ContainSingle().Which.Line.Should().Be(2);
    }

    [Fact]
    public async Task ManagedFallback_SupportsRegex()
    {
        var matches = await Managed.SearchAsync(
            _id, new BridgeSearchRequest("n..dle = 1", Regex: true), CancellationToken.None);

        matches.Should().ContainSingle().Which.Line.Should().Be(1);
    }

    [Fact]
    public async Task ManagedFallback_SkipsTheGitDirectory()
    {
        var matches = await Managed.SearchAsync(_id, new BridgeSearchRequest("needle"), CancellationToken.None);

        matches.Should().OnlyContain(m => !m.Path.StartsWith(".git"));
    }

    [Fact]
    public async Task UnknownWorkspace_Throws()
    {
        var act = () => Managed.SearchAsync("deadbeefdeadbeef", new BridgeSearchRequest("x"), CancellationToken.None);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
