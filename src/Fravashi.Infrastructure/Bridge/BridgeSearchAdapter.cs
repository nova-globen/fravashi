using System.Text;
using Fravashi.Application.Repository;
using Fravashi.Application.Search;
using Fravashi.Infrastructure.Search;
using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.Bridge;

/// <summary>
/// <see cref="IRepositorySearchService"/> over the host bridge (plan 2b-i): search runs host-side
/// (ripgrep at native speed), replace/restore run studio-side through the SAME
/// <see cref="SearchReplaceCore"/> the local engine uses, with pre-images read via the bridge and
/// writes flowing through <see cref="IRepositoryWriter"/> so the freshness guard applies. The
/// studio's Settings ignore rules travel as exclude globs.
/// </summary>
public sealed class BridgeSearchAdapter(
    BridgeClient client, IRepositoryWriter writer, IConfiguration configuration) : IRepositorySearchService
{
    public async Task<SearchResultSet> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.Pattern))
        {
            return SearchResultSet.Empty("bridge");
        }

        var excludes = MergeExcludes(query.ExcludeGlobs);
        IReadOnlyList<BridgeSearchMatchDto> matches;
        try
        {
            matches = await client.SearchAsync(new
            {
                pattern = query.Pattern,
                caseSensitive = query.CaseSensitive,
                regex = query.IsRegex,
                wholeWord = query.WholeWord,
                includeGlobs = query.IncludeGlobs,
                excludeGlobs = excludes,
                maxResults = query.MaxResults,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return SearchResultSet.Failed("bridge", ex.Message);
        }

        var files = matches
            .GroupBy(m => m.Path, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SearchFileResult(
                g.Key,
                [.. g.Select(m => new SearchMatch(m.Line, m.Column, m.Length, m.Preview))]))
            .ToList();
        return new SearchResultSet(files, matches.Count, matches.Count >= query.MaxResults, "bridge");
    }

    public async Task<ReplaceResult> ReplaceAsync(
        SearchQuery query,
        string replacement,
        IReadOnlyList<string> relativePaths,
        (int Line, int Column)? onlyAt = null,
        CancellationToken cancellationToken = default)
    {
        System.Text.RegularExpressions.Regex regex;
        try
        {
            regex = SearchReplaceCore.BuildRegex(query);
        }
        catch (ArgumentException ex)
        {
            return new ReplaceResult(0, 0, $"Invalid regular expression: {ex.Message}");
        }

        var substitution = SearchReplaceCore.Substitution(query, replacement);
        var filesChanged = 0;
        var occurrences = 0;
        string? firstError = null;
        var replaced = new List<ReplacedFile>();

        foreach (var relativePath in relativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BridgeFileContent? content;
            try
            {
                content = await client.ReadAsync(relativePath, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                firstError ??= ex.Message;
                continue;
            }

            if (content is null)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(content.Bytes);
            var updated = SearchReplaceCore.Apply(text, regex, substitution, onlyAt, out var replacedHere);
            if (replacedHere == 0 || string.Equals(updated, text, StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(updated));
            var result = await writer.SaveFileAsync(relativePath, stream, content.ModifiedUtc, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                firstError ??= result.Error;
                continue;
            }

            filesChanged++;
            occurrences += replacedHere;
            replaced.Add(new ReplacedFile(relativePath, text, result.LastWriteUtc));
        }

        return new ReplaceResult(filesChanged, occurrences, firstError, replaced);
    }

    public async Task<ReplaceResult> RestoreAsync(
        IReadOnlyList<ReplacedFile> files, CancellationToken cancellationToken = default)
    {
        var restored = 0;
        string? firstError = null;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(file.PreviousContent));
            var result = await writer.SaveFileAsync(file.Path, stream, file.NewLastWriteUtc, cancellationToken)
                .ConfigureAwait(false);
            if (result.Conflict)
            {
                firstError ??= $"{file.Path} changed again after the replace; it was left as it is.";
            }
            else if (!result.Succeeded)
            {
                firstError ??= result.Error;
            }
            else
            {
                restored++;
            }
        }

        return new ReplaceResult(restored, restored, firstError);
    }

    /// <summary>The Settings ignore rules (newline gitignore-style lines) become exclude globs.</summary>
    private string? MergeExcludes(string? queryExcludes)
    {
        var rules = configuration[SearchDefaults.IgnoreRulesKey] ?? SearchDefaults.IgnoreRules;
        var fromRules = rules
            .Split('\n')
            .Select(l => l.Trim().TrimEnd('\r'))
            .Where(l => l.Length > 0 && !l.StartsWith('#'));
        var merged = string.Join(',', fromRules);
        if (string.IsNullOrWhiteSpace(queryExcludes))
        {
            return merged.Length == 0 ? null : merged;
        }

        return merged.Length == 0 ? queryExcludes : $"{queryExcludes},{merged}";
    }
}
