using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Fravashi.Bridge;

/// <summary>One search hit; <see cref="Path"/> is workspace-relative with <c>/</c> separators.</summary>
public sealed record BridgeSearchMatch(string Path, int Line, int Column, int Length, string Preview);

/// <summary>A search request over one workspace. Globs are comma/semicolon-separated.</summary>
public sealed record BridgeSearchRequest(
    string Pattern,
    bool CaseSensitive = false,
    bool Regex = false,
    int MaxResults = 500,
    bool WholeWord = false,
    string? IncludeGlobs = null,
    string? ExcludeGlobs = null);

/// <summary>
/// Workspace search: ripgrep when available (explicit path, <c>rg.exe</c> beside the executable,
/// or PATH), else a managed line scan. The managed fallback skips <c>.git</c>, sniffs out binary
/// files, and caps file size, mirroring the studio's own fallback posture (ADR 0015). Glob
/// semantics: a glob ending in <c>/</c> matches a directory name anywhere in the path; a glob
/// without <c>/</c> matches the file NAME; anything else matches the whole relative path
/// (<c>**</c> crosses separators, <c>*</c> does not, <c>?</c> is one char).
/// </summary>
public sealed class BridgeSearchService(
    WorkspaceRegistry registry, string? ripgrepPath = null, bool forceManagedFallback = false)
{
    private const long MaxScannedFileBytes = 2 * 1024 * 1024;

    public async Task<IReadOnlyList<BridgeSearchMatch>> SearchAsync(
        string id, BridgeSearchRequest request, CancellationToken cancellationToken)
    {
        var root = registry.ResolvePath(id, null);
        if (string.IsNullOrWhiteSpace(request.Pattern))
        {
            return [];
        }

        if (!forceManagedFallback && ResolveRipgrep() is { } rg)
        {
            return await SearchWithRipgrepAsync(rg, root, request, cancellationToken).ConfigureAwait(false);
        }

        return SearchManagedStatic(root, request, cancellationToken);
    }

    private string? ResolveRipgrep()
    {
        if (!string.IsNullOrWhiteSpace(ripgrepPath) && File.Exists(ripgrepPath))
        {
            return ripgrepPath;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "rg.exe");
        if (File.Exists(beside))
        {
            return beside;
        }

        // PATH probe: let the OS resolve it; a failed start falls back to managed.
        return OperatingSystem.IsWindows() ? "rg.exe" : "rg";
    }

    private static async Task<IReadOnlyList<BridgeSearchMatch>> SearchWithRipgrepAsync(
        string rg, string root, BridgeSearchRequest request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(rg)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Redirected and closed immediately below. Without this the child inherits THIS process's
            // standard input, and what that handle happens to be decides what ripgrep searches.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("--vimgrep");
        startInfo.ArgumentList.Add("--no-heading");
        startInfo.ArgumentList.Add("--max-count");
        startInfo.ArgumentList.Add(request.MaxResults.ToString());
        if (!request.CaseSensitive)
        {
            startInfo.ArgumentList.Add("-i");
        }

        if (!request.Regex)
        {
            startInfo.ArgumentList.Add("--fixed-strings");
        }

        if (request.WholeWord)
        {
            startInfo.ArgumentList.Add("--word-regexp");
        }

        foreach (var glob in SplitGlobs(request.IncludeGlobs))
        {
            startInfo.ArgumentList.Add("-g");
            startInfo.ArgumentList.Add(glob);
        }

        foreach (var glob in SplitGlobs(request.ExcludeGlobs))
        {
            startInfo.ArgumentList.Add("-g");
            startInfo.ArgumentList.Add($"!{glob}");
        }

        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(request.Pattern);

        // The path ripgrep searches, stated rather than left to it. With no path argument, ripgrep searches
        // the working directory only when standard input is a TERMINAL, and reads standard input when it is
        // not. The bridge is hosted inside fravashi.exe, which is a windowed process with no console, and under
        // a test runner standard input is whatever the runner left there. Measured on 2026-09-17: ripgrep
        // sat at zero CPU forever, waiting on an input nobody was going to write, and the call hung until
        // its timeout while the managed fallback never ran because the process had started perfectly well.
        startInfo.ArgumentList.Add(".");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }

            // Closed at once, so the child sees end-of-input instead of waiting on a handle it inherited.
            process.StandardInput.Close();

            // BOTH pipes are drained at once, and that is not a tidiness point: reading stdout to EOF
            // first and stderr afterwards deadlocks whenever the child fills the stderr buffer, because
            // it then blocks writing stderr while this side waits for a stdout EOF that can never come.
            // Measured on 2026-09-17: three search tests hung for their full 100-second timeout with rg
            // sitting at zero CPU, reproducibly under parallel load, when rg had warnings to report (a
            // path that vanished under it). The sibling RepositorySearchService already starts its stderr
            // read before looping on stdout, which is why only this one hung.
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            var output = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return ParseVimgrep(output, request);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // rg not actually on PATH: honest managed fallback instead of a failure.
            return SearchManagedStatic(root, request, cancellationToken);
        }
    }

    /// <summary>
    /// Parses <c>path:line:col:preview</c> lines (rg --vimgrep). Match length is inferred: exact
    /// for fixed strings (the pattern's length), best-effort regex re-match on the preview at the
    /// reported column, else the pattern length.
    /// </summary>
    internal static IReadOnlyList<BridgeSearchMatch> ParseVimgrep(string output, BridgeSearchRequest request)
    {
        var matches = new List<BridgeSearchMatch>();
        foreach (var line in output.Split('\n'))
        {
            if (matches.Count >= request.MaxResults)
            {
                break;
            }

            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0)
            {
                continue;
            }

            // path:line:col:preview, where the preview may itself contain colons.
            var first = trimmed.IndexOf(':');
            if (first <= 0)
            {
                continue;
            }

            var second = trimmed.IndexOf(':', first + 1);
            var third = second < 0 ? -1 : trimmed.IndexOf(':', second + 1);
            if (third < 0)
            {
                continue;
            }

            if (!int.TryParse(trimmed.AsSpan(first + 1, second - first - 1), out var lineNumber)
                || !int.TryParse(trimmed.AsSpan(second + 1, third - second - 1), out var column))
            {
                continue;
            }

            // Workspace-relative with forward separators. The leading "./" comes from the explicit search
            // path the runner now passes (see SearchWithRipgrepAsync); a match is reported at the same path
            // whether or not ripgrep was told where to look, because a caller should not be able to tell.
            var path = trimmed[..first].Replace('\\', '/');
            if (path.StartsWith("./", StringComparison.Ordinal))
            {
                path = path[2..];
            }
            var preview = trimmed[(third + 1)..];
            matches.Add(new BridgeSearchMatch(
                path, lineNumber, column, ResolveLength(request, preview, column), preview));
        }

        return matches;
    }

    private static int ResolveLength(BridgeSearchRequest request, string preview, int column)
    {
        if (!request.Regex)
        {
            return request.Pattern.Length;
        }

        try
        {
            var options = request.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            var start = Math.Clamp(column - 1, 0, Math.Max(0, preview.Length - 1));
            var match = new Regex(request.Pattern, options, TimeSpan.FromMilliseconds(200))
                .Match(preview, start);
            if (match.Success)
            {
                return Math.Max(1, match.Length);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
        }

        return Math.Max(1, request.Pattern.Length);
    }

    private static IReadOnlyList<BridgeSearchMatch> SearchManagedStatic(
        string root, BridgeSearchRequest request, CancellationToken cancellationToken)
    {
        var comparison = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Regex? regex = null;
        if (request.Regex || request.WholeWord)
        {
            var options = RegexOptions.None;
            if (!request.CaseSensitive)
            {
                options |= RegexOptions.IgnoreCase;
            }

            var core = request.Regex ? request.Pattern : Regex.Escape(request.Pattern);
            var pattern = request.WholeWord ? $@"\b(?:{core})\b" : core;
            regex = new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }

        var includes = SplitGlobs(request.IncludeGlobs).Select(GlobToRegex).ToList();
        var excludes = SplitGlobs(request.ExcludeGlobs).Select(GlobToRegex).ToList();

        var matches = new List<BridgeSearchMatch>();
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(root, "*", enumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= request.MaxResults)
            {
                break;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileName(relative);
            if (includes.Count > 0 && !includes.Any(g => g.Match(relative, name)))
            {
                continue;
            }

            if (excludes.Any(g => g.Match(relative, name)))
            {
                continue;
            }

            var info = new FileInfo(file);
            if (info.Length > MaxScannedFileBytes || LooksBinary(file))
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (matches.Count >= request.MaxResults)
                {
                    break;
                }

                if (regex is not null)
                {
                    var m = regex.Match(line);
                    if (m.Success)
                    {
                        matches.Add(new BridgeSearchMatch(relative, lineNumber, m.Index + 1, Math.Max(1, m.Length), line));
                    }
                }
                else
                {
                    var index = line.IndexOf(request.Pattern, comparison);
                    if (index >= 0)
                    {
                        matches.Add(new BridgeSearchMatch(relative, lineNumber, index + 1, request.Pattern.Length, line));
                    }
                }
            }
        }

        return matches;
    }

    private static IEnumerable<string> SplitGlobs(string? globs) =>
        string.IsNullOrWhiteSpace(globs)
            ? []
            : globs.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record CompiledGlob(Regex? PathRegex, Regex? NameRegex, string? DirectoryName)
    {
        public bool Match(string relativePath, string fileName)
        {
            if (DirectoryName is not null)
            {
                return relativePath.StartsWith(DirectoryName + "/", StringComparison.OrdinalIgnoreCase)
                    || relativePath.Contains("/" + DirectoryName + "/", StringComparison.OrdinalIgnoreCase);
            }

            if (NameRegex is not null)
            {
                return NameRegex.IsMatch(fileName);
            }

            return PathRegex!.IsMatch(relativePath);
        }
    }

    private static CompiledGlob GlobToRegex(string glob)
    {
        if (glob.EndsWith('/'))
        {
            return new CompiledGlob(null, null, glob.TrimEnd('/'));
        }

        var translated = TranslateGlob(glob);
        return glob.Contains('/')
            ? new CompiledGlob(new Regex($"^{translated}$", RegexOptions.IgnoreCase), null, null)
            : new CompiledGlob(null, new Regex($"^{translated}$", RegexOptions.IgnoreCase), null);
    }

    private static string TranslateGlob(string glob)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    builder.Append(".*");
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++; // "**/" already covered by ".*"
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                builder.Append('.');
            }
            else
            {
                builder.Append(Regex.Escape(c.ToString()));
            }
        }

        return builder.ToString();
    }

    private static bool LooksBinary(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> head = stackalloc byte[1024];
            var read = stream.Read(head);
            return head[..read].Contains((byte)0);
        }
        catch (IOException)
        {
            return true;
        }
    }
}
