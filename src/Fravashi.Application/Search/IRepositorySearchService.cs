namespace Fravashi.Application.Search;

/// <summary>
/// A find/replace query over the configured working copy, VS Code semantics (ADR 0015):
/// literal by default, whole-word and regex toggles, case toggle, and VS-Code-style
/// comma-separated include/exclude glob lists.
/// </summary>
public sealed record SearchQuery(
    string Pattern,
    bool CaseSensitive = false,
    bool WholeWord = false,
    bool IsRegex = false,
    string? IncludeGlobs = null,
    string? ExcludeGlobs = null,
    int MaxResults = SearchDefaults.MaxResults);

/// <summary>One match: 1-based line/column, match length in characters, and the full line's text.</summary>
public sealed record SearchMatch(int Line, int Column, int Length, string LineText);

public sealed record SearchFileResult(string RelativePath, IReadOnlyList<SearchMatch> Matches);

/// <summary>
/// <see cref="Engine"/> names what actually ran ("ripgrep" or "managed"); <see cref="Truncated"/> is
/// true when the result cap cut the search short (surfaced in the UI, never silent).
/// </summary>
public sealed record SearchResultSet(
    IReadOnlyList<SearchFileResult> Files,
    int TotalMatches,
    bool Truncated,
    string Engine,
    string? Error = null)
{
    public static SearchResultSet Empty(string engine) => new([], 0, false, engine);

    public static SearchResultSet Failed(string engine, string error) => new([], 0, false, engine, error);
}

/// <summary>A file rewritten by a replace: its pre-image enables the one-level undo.</summary>
public sealed record ReplacedFile(string Path, string PreviousContent, DateTimeOffset? NewLastWriteUtc);

/// <summary>
/// Outcome of a replace: files touched and occurrences replaced (server-side writes only).
/// <see cref="Files"/> carries the pre-images for <see cref="IRepositorySearchService.RestoreAsync"/>.
/// </summary>
public sealed record ReplaceResult(
    int FilesChanged,
    int OccurrencesReplaced,
    string? Error = null,
    IReadOnlyList<ReplacedFile>? Files = null);

public interface IRepositorySearchService
{
    /// <summary>Find matches; honors the repository's .gitignore plus the settings-backed ignore rules.</summary>
    Task<SearchResultSet> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace occurrences of <paramref name="query"/> with <paramref name="replacement"/> in the given
    /// files (disk writes through the gated repository writer). Files whose buffers are open and dirty
    /// must be routed through the client instead; the caller owns that split (ADR 0015). When
    /// <paramref name="onlyAt"/> is given, only the single occurrence at that 1-based line/column is
    /// replaced (the per-match replace button).
    /// </summary>
    Task<ReplaceResult> ReplaceAsync(
        SearchQuery query,
        string replacement,
        IReadOnlyList<string> relativePaths,
        (int Line, int Column)? onlyAt = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The one-level undo of a disk replace: write each pre-image back, refusing files whose disk
    /// content moved on since the replace (their timestamp no longer matches). Dirty open buffers
    /// were never disk-replaced; their undo is the editor's own Ctrl+Z.
    /// </summary>
    Task<ReplaceResult> RestoreAsync(IReadOnlyList<ReplacedFile> files, CancellationToken cancellationToken = default);
}

public static class SearchDefaults
{
    public const int MaxResults = 10_000;

    /// <summary>Settings-table key for the ignore rules (gitignore-like syntax, one pattern per line).</summary>
    public const string IgnoreRulesKey = "Search:IgnoreRules";

    /// <summary>
    /// The seeded well-known exclusions (ADR 0015). Users edit these in Settings > Advanced >
    /// Search ignore rules; search always applies the stored value (or this default when unset) on top of the
    /// repository's own .gitignore.
    /// </summary>
    public const string IgnoreRules = """
        # Paths excluded from search (gitignore syntax; lines starting with # are comments).
        .git/
        node_modules/
        bin/
        obj/
        dist/
        build/
        out/
        .vs/
        .idea/
        packages/
        wwwroot/vendor/
        *.min.js
        *.min.css
        *.map
        *.dll
        *.exe
        *.pdb
        *.png
        *.jpg
        *.jpeg
        *.gif
        *.ico
        *.woff
        *.woff2
        """;
}
