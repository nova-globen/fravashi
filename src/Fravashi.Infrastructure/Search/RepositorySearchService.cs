using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fravashi.Application.Repository;
using Fravashi.Application.Search;
using Fravashi.Infrastructure.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fravashi.Infrastructure.Search;

/// <summary>
/// <see cref="IRepositorySearchService"/> over ripgrep (ADR 0015): `rg --json` streamed and parsed,
/// with the settings-backed ignore rules passed as an `--ignore-file` so full gitignore semantics
/// apply. When `rg` is not on the PATH (bare dev host) a managed scanner with the same query
/// semantics and a practical gitignore subset runs instead; the result names the engine either way.
/// Replace is always managed (one consistent .NET-regex semantics for writes) and goes through the
/// gated repository writer.
/// </summary>
public sealed class RepositorySearchService : IRepositorySearchService
{
    private const long ManagedMaxFileBytes = 2 * 1024 * 1024;

    private static readonly Lazy<string?> RgExecutable = new(FindRg);

    private readonly string _root;
    private readonly IConfiguration _configuration;
    private readonly IRepositoryWriter _writer;
    private readonly ILogger<RepositorySearchService> _logger;

    private readonly bool _forceManagedEngine;

    public RepositorySearchService(
        IOptions<RepositoryReaderOptions> options,
        IConfiguration configuration,
        IRepositoryWriter writer,
        ILogger<RepositorySearchService> logger,
        bool forceManagedEngine = false)
    {
        var configured = options.Value.Root;
        _root = string.IsNullOrWhiteSpace(configured)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        _configuration = configuration;
        _writer = writer;
        _logger = logger;
        // Test seam: exercises the fallback scanner on hosts that do have rg installed.
        _forceManagedEngine = forceManagedEngine;
    }

    public async Task<SearchResultSet> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (_root.Length == 0)
        {
            return SearchResultSet.Failed("none", "No repository is configured.");
        }

        if (string.IsNullOrEmpty(query.Pattern))
        {
            return SearchResultSet.Empty(RgExecutable.Value is null ? "managed" : "ripgrep");
        }

        var ignoreRules = _configuration[SearchDefaults.IgnoreRulesKey];
        if (string.IsNullOrWhiteSpace(ignoreRules))
        {
            ignoreRules = SearchDefaults.IgnoreRules;
        }

        return !_forceManagedEngine && RgExecutable.Value is { } rg
            ? await RgSearchAsync(rg, query, ignoreRules, cancellationToken).ConfigureAwait(false)
            : await Task.Run(() => ManagedSearch(query, ignoreRules, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    // ---- ripgrep engine ----

    private async Task<SearchResultSet> RgSearchAsync(
        string rg, SearchQuery query, string ignoreRules, CancellationToken cancellationToken)
    {
        var ignoreFile = Path.Combine(Path.GetTempPath(), "fravashi-search-ignore-" + Guid.NewGuid().ToString("N")[..8]);
        await File.WriteAllTextAsync(ignoreFile, ignoreRules, cancellationToken).ConfigureAwait(false);
        try
        {
            var info = new ProcessStartInfo(rg)
            {
                WorkingDirectory = _root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,

                // Closed the moment the child starts. This runner does pass an explicit search path, so
                // ripgrep has no reason to read standard input, but inheriting this process's handle is a
                // dependence on something nobody here controls, and the bridge's copy of this code hung on
                // exactly that (backlog 99, measured 2026-09-17).
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            info.ArgumentList.Add("--json");
            info.ArgumentList.Add("--no-config");
            // Honor .gitignore even when the root is not (yet) a git repository, matching the
            // managed fallback's behavior.
            info.ArgumentList.Add("--no-require-git");
            info.ArgumentList.Add("--ignore-file");
            info.ArgumentList.Add(ignoreFile);
            info.ArgumentList.Add(query.CaseSensitive ? "--case-sensitive" : "--ignore-case");
            if (!query.IsRegex)
            {
                info.ArgumentList.Add("--fixed-strings");
            }

            if (query.WholeWord)
            {
                info.ArgumentList.Add("--word-regexp");
            }

            foreach (var glob in SplitGlobs(query.IncludeGlobs))
            {
                info.ArgumentList.Add("--glob");
                info.ArgumentList.Add(NormalizeIncludeGlob(glob));
            }

            foreach (var glob in SplitGlobs(query.ExcludeGlobs))
            {
                info.ArgumentList.Add("--glob");
                info.ArgumentList.Add("!" + NormalizeIncludeGlob(glob));
            }

            info.ArgumentList.Add("--");
            info.ArgumentList.Add(query.Pattern);
            info.ArgumentList.Add(".");

            using var process = new Process { StartInfo = info };
            process.Start();
            process.StandardInput.Close();
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            var files = new List<SearchFileResult>();
            var currentPath = string.Empty;
            var currentMatches = new List<SearchMatch>();
            var total = 0;
            var truncated = false;

            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                using var json = JsonDocument.Parse(line);
                var type = json.RootElement.GetProperty("type").GetString();
                if (type == "begin")
                {
                    currentPath = NormalizeRgPath(json.RootElement.GetProperty("data").GetProperty("path").GetProperty("text").GetString() ?? string.Empty);
                    currentMatches = [];
                }
                else if (type == "match")
                {
                    var data = json.RootElement.GetProperty("data");
                    var lineText = data.GetProperty("lines").GetProperty("text").GetString() ?? string.Empty;
                    lineText = lineText.TrimEnd('\r', '\n');
                    var lineNumber = data.GetProperty("line_number").GetInt32();
                    var lineBytes = Encoding.UTF8.GetBytes(lineText);
                    foreach (var sub in data.GetProperty("submatches").EnumerateArray())
                    {
                        var startByte = sub.GetProperty("start").GetInt32();
                        var endByte = sub.GetProperty("end").GetInt32();
                        var column = ByteToCharOffset(lineBytes, startByte) + 1;
                        var length = ByteToCharOffset(lineBytes, Math.Min(endByte, lineBytes.Length)) + 1 - column;
                        currentMatches.Add(new SearchMatch(lineNumber, column, Math.Max(length, 0), lineText));
                        total++;
                        if (total >= query.MaxResults)
                        {
                            truncated = true;
                            break;
                        }
                    }

                    if (truncated)
                    {
                        break;
                    }
                }
                else if (type == "end" && currentMatches.Count > 0)
                {
                    files.Add(new SearchFileResult(currentPath, currentMatches));
                    currentMatches = [];
                }
            }

            if (truncated)
            {
                if (currentMatches.Count > 0)
                {
                    files.Add(new SearchFileResult(currentPath, currentMatches));
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited between the check and the kill.
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            // rg exits 1 for "no matches" (not an error) and 2 for real failures (bad regex, IO).
            if (!truncated && process.ExitCode > 1)
            {
                var message = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(l => l.Length > 0) ?? "ripgrep failed.";
                return SearchResultSet.Failed("ripgrep", message);
            }

            return new SearchResultSet(files, total, truncated, "ripgrep");
        }
        finally
        {
            try
            {
                File.Delete(ignoreFile);
            }
            catch (IOException)
            {
                // Temp cleanup only.
            }
        }
    }

    private static string NormalizeRgPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    private static int ByteToCharOffset(byte[] lineBytes, int byteOffset)
    {
        if (byteOffset <= 0)
        {
            return 0;
        }

        return Encoding.UTF8.GetCharCount(lineBytes, 0, Math.Min(byteOffset, lineBytes.Length));
    }

    private static string[] SplitGlobs(string? globs) =>
        string.IsNullOrWhiteSpace(globs)
            ? []
            : globs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>VS Code treats a bare "*.md" as "anywhere"; rg globs anchor, so prefix "**/".</summary>
    private static string NormalizeIncludeGlob(string glob) =>
        glob.StartsWith("**", StringComparison.Ordinal) || glob.StartsWith('/')
            ? glob.TrimStart('/')
            : glob.Contains('/', StringComparison.Ordinal) ? glob : "**/" + glob;

    private static string? FindRg()
    {
        // Extensionless "rg" is accepted on Windows too: CreateProcess runs any PE given a full
        // path, and unix-style tool drops (~/bin/rg) are common on dev hosts.
        string[] names = OperatingSystem.IsWindows() ? ["rg.exe", "rg"] : ["rg"];
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var fileName in names)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry; skip it.
                }
            }
        }

        return null;
    }

    // ---- managed fallback engine ----

    private SearchResultSet ManagedSearch(SearchQuery query, string ignoreRules, CancellationToken cancellationToken)
    {
        Regex regex;
        try
        {
            regex = BuildRegex(query);
        }
        catch (ArgumentException ex)
        {
            return SearchResultSet.Failed("managed", $"Invalid regular expression: {ex.Message}");
        }

        var gitignorePath = Path.Combine(_root, ".gitignore");
        var ignore = GitignoreMatcher.FromRuleText(
            File.Exists(gitignorePath) ? File.ReadAllText(gitignorePath) : null, ignoreRules);
        var include = GitignoreMatcher.FromGlobList(query.IncludeGlobs);
        var exclude = GitignoreMatcher.FromGlobList(query.ExcludeGlobs);

        var files = new List<SearchFileResult>();
        var total = 0;
        var truncated = false;

        foreach (var file in Walk(_root, string.Empty, ignore, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (include is not null && !include.IsMatch(file.Relative))
            {
                continue;
            }

            if (exclude is not null && exclude.IsMatch(file.Relative))
            {
                continue;
            }

            var matches = MatchFile(file.Full, regex, query.MaxResults - total, ref truncated);
            if (matches.Count > 0)
            {
                files.Add(new SearchFileResult(file.Relative, matches));
                total += matches.Count;
            }

            if (truncated || total >= query.MaxResults)
            {
                truncated = truncated || total >= query.MaxResults;
                break;
            }
        }

        return new SearchResultSet(files, total, truncated, "managed");
    }

    private IEnumerable<(string Full, string Relative)> Walk(
        string directory, string relativePrefix, GitignoreMatcher ignore, CancellationToken cancellationToken)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            var relative = relativePrefix.Length == 0 ? name : $"{relativePrefix}/{name}";
            var isDirectory = Directory.Exists(entry);
            if (ignore.IsIgnored(relative, isDirectory))
            {
                continue;
            }

            if (isDirectory)
            {
                foreach (var nested in Walk(entry, relative, ignore, cancellationToken))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return (entry, relative);
            }
        }
    }

    private static List<SearchMatch> MatchFile(string fullPath, Regex regex, int budget, ref bool truncated)
    {
        var matches = new List<SearchMatch>();
        try
        {
            if (new FileInfo(fullPath).Length > ManagedMaxFileBytes)
            {
                return matches;
            }

            var lineNumber = 0;
            foreach (var rawLine in File.ReadLines(fullPath))
            {
                lineNumber++;
                if (lineNumber == 1 && rawLine.Contains('\0', StringComparison.Ordinal))
                {
                    return []; // binary
                }

                foreach (Match m in regex.Matches(rawLine))
                {
                    matches.Add(new SearchMatch(lineNumber, m.Index + 1, m.Length, rawLine));
                    if (matches.Count >= budget)
                    {
                        truncated = true;
                        return matches;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable file: skip, do not fail the whole search.
        }

        return matches;
    }

    private static Regex BuildRegex(SearchQuery query) => SearchReplaceCore.BuildRegex(query);

    // ---- replace (managed on purpose: one write semantics regardless of search engine) ----

    public async Task<ReplaceResult> ReplaceAsync(
        SearchQuery query,
        string replacement,
        IReadOnlyList<string> relativePaths,
        (int Line, int Column)? onlyAt = null,
        CancellationToken cancellationToken = default)
    {
        if (_root.Length == 0)
        {
            return new ReplaceResult(0, 0, "No repository is configured.");
        }

        Regex regex;
        try
        {
            regex = BuildRegex(query);
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
            var full = ResolveWithinRoot(relativePath);
            if (full is null || !File.Exists(full))
            {
                continue;
            }

            string text;
            DateTimeOffset stamp;
            try
            {
                text = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
                stamp = new DateTimeOffset(File.GetLastWriteTimeUtc(full), TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                firstError ??= ex.Message;
                continue;
            }

            var updated = SearchReplaceCore.Apply(text, regex, substitution, onlyAt, out var replacedHere);

            if (replacedHere == 0 || string.Equals(updated, text, StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(updated));
            var result = await _writer.SaveFileAsync(relativePath, stream, stamp, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                firstError ??= result.Error;
                _logger.LogWarning("Replace in {Path} failed: {Error}", relativePath, result.Error);
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
            var result = await _writer.SaveFileAsync(file.Path, stream, file.NewLastWriteUtc, cancellationToken)
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

    private string? ResolveWithinRoot(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        var combined = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(_root, normalized)));
        var rootWithSeparator = _root + Path.DirectorySeparatorChar;
        return combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ? combined : null;
    }
}
