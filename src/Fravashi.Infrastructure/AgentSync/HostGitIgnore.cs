using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Fravashi.Infrastructure.Bridge;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.AgentSync;

/// <summary>
/// What the HOST's own git ignores in a workspace folder (backlog 161), asked of git itself through the bridge rather
/// than worked out by a second parser of its rules. That covers every source git honours: the repository's
/// <c>.gitignore</c> files at every level, <c>.git/info/exclude</c>, and the <c>core.excludesFile</c> the host user
/// configured, which is where a person keeps what they ignore in EVERY repository (a local <c>.env</c>, editor
/// settings carrying a token). The sync engine used to read the root <c>.gitignore</c> alone, so a file ignored only
/// globally was copied into the agent's copy, where the agent CLI loaded it.
/// <para>
/// Two answers, because a path either existed on the host when the session started or did not. The SNAPSHOT
/// (<c>git ls-files --others --ignored --exclude-standard --directory</c>) answers every path that existed then, with a
/// wholly ignored folder listed once as <c>name/</c>. A path that appeared later is ASKED about
/// (<c>git check-ignore</c>), once, together with the folders above it, and the answers are kept for the session: git
/// answers for a folder named with a trailing slash whether or not it exists yet, so a build filling a newly ignored
/// folder costs one question rather than one per file. A tracked file is never ignored, as in git.
/// Rules are read when the session starts, as the repository's <c>.gitignore</c> always was; a rule changed during a
/// session applies from the next start.
/// </para>
/// <para>
/// A tracked file is never ignored, and that is ENFORCED rather than assumed (backlog 253): the host's tracked files
/// (<c>git ls-files</c>) are read with the snapshot and again at every rescan, and a tracked path, or a folder holding
/// one, is never ignored by the snapshot, by an earlier answer, or by the repository's root rules the engine reads
/// itself. So a file force-added under a rule syncs, and one that becomes tracked during a session arrives at the next
/// rescan. A path the host reports as created is asked about again (<see cref="Forget"/>).
/// </para>
/// </summary>
public sealed partial class HostGitIgnore
{
    // Windows caps a command line near 32K characters, and the bridge runs git on the Windows host.
    private const int MaxAskCharacters = 24_000;
    private const string TruncationMarker = "\n[truncated]";

    private readonly BridgeClient? _bridge;
    private readonly HashSet<string> _files;
    private readonly List<string> _folders;
    private readonly ConcurrentDictionary<string, bool> _asked = new(StringComparer.Ordinal);

    // The rule git named for each path it answered ignored (check-ignore -v), for the withheld-path log.
    private readonly ConcurrentDictionary<string, string> _rules = new(StringComparer.Ordinal);
    private volatile TrackedPaths _tracked = TrackedPaths.Empty;

    private HostGitIgnore(BridgeClient? bridge, IEnumerable<string> listed)
    {
        _bridge = bridge;
        _files = new HashSet<string>(StringComparer.Ordinal);
        _folders = [];
        foreach (var entry in listed)
        {
            if (entry.EndsWith('/'))
            {
                _folders.Add(entry);
            }
            else
            {
                _files.Add(entry);
            }
        }
    }

    /// <summary>No host rules: the folder is not a repository, or its git could not be asked.</summary>
    public static HostGitIgnore None { get; } = new(null, []);

    /// <summary>Whether the host's git answers for this folder at all.</summary>
    public bool Answers => _bridge is not null;

    /// <summary>
    /// Takes the snapshot. A folder the host's git cannot list for (not a repository, or no git on the host) gets
    /// <see cref="None"/> and the reason in the log, so the copy still works and follows the repository's
    /// <c>.gitignore</c> as before. A bridge that cannot be reached throws, and the session start is retried.
    /// </summary>
    public static async Task<HostGitIgnore> LoadAsync(BridgeClient bridge, ILogger logger, CancellationToken ct)
    {
        var result = await bridge.GitAsync(
            ["ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "-z"], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            if (result.IsGitMissing)
            {
                // Backlog 266: the bridge answers this rather than failing, so the copy starts as for a plain folder.
                logger.LogInformation(
                    "git is not installed on the host, so no git ignore rules apply to this workspace's agent copy.");
            }
            else if (result.Stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "The workspace folder is not a git repository, so no git ignore rules apply to its agent copy.");
            }
            else
            {
                logger.LogWarning(
                    "The host's git could not list what it ignores in this workspace (exit {ExitCode}: {Error}); the "
                    + "agent copy follows the repository's .gitignore alone until the next start.",
                    result.ExitCode, result.Stderr.Trim());
            }

            return None;
        }

        var stdout = result.Stdout;
        if (stdout.EndsWith(TruncationMarker, StringComparison.Ordinal))
        {
            // Eight megabytes of ignored names with folders collapsed; what was listed still holds.
            logger.LogWarning(
                "The host's list of ignored paths was cut at the bridge's output cap; paths past the cut are treated "
                + "as not ignored for this session.");
            stdout = stdout[..^TruncationMarker.Length];
        }

        var host = new HostGitIgnore(
            bridge, stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Replace('\\', '/')));
        if (!await host.RefreshTrackedAsync(ct).ConfigureAwait(false))
        {
            logger.LogWarning(
                "The host's git could not list its tracked files in this workspace; until the next rescan a tracked "
                + "file under an ignore rule may be left out of the agent copy.");
        }

        return host;
    }

    /// <summary>
    /// Reads the host's tracked files again (<c>git ls-files</c>). False when git could not answer; the last list then
    /// stands. A list cut at the bridge's output cap is used as far as it goes.
    /// </summary>
    public async Task<bool> RefreshTrackedAsync(CancellationToken ct)
    {
        if (_bridge is null)
        {
            return true;
        }

        var result = await _bridge.GitAsync(["ls-files", "-z"], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return false;
        }

        var stdout = result.Stdout.EndsWith(TruncationMarker, StringComparison.Ordinal)
            ? result.Stdout[..^TruncationMarker.Length]
            : result.Stdout;
        _tracked = new TrackedPaths(stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Replace('\\', '/')));
        return true;
    }

    /// <summary>Whether the host's git tracks <paramref name="path"/>, or, for a folder, any file under it.</summary>
    public bool Tracks(string path, bool isDirectory)
    {
        var tracked = _tracked;
        return isDirectory ? tracked.Folders.Contains(path) : tracked.Files.Contains(path);
    }

    /// <summary>Drops an earlier answer for <paramref name="path"/>, so it is asked about again (a re-created file, backlog 253).</summary>
    public void Forget(string path)
    {
        _asked.TryRemove(path, out _);
        _rules.TryRemove(path, out _);
    }

    /// <summary>
    /// Why the host's git ignores <paramref name="path"/>, in words for the log: the rule git named when it was asked,
    /// or that the start-of-session list holds it (git does not say which rule there). Null when nothing here says so.
    /// </summary>
    public string? Why(string path)
    {
        if (Lists(path))
        {
            return "the host's git listed it as ignored when this session started (git ls-files --ignored)";
        }

        foreach (var asked in FoldersAbove(path).Append(path))
        {
            if (_asked.TryGetValue(asked, out var ignored) && ignored)
            {
                return _rules.TryGetValue(asked, out var rule)
                    ? $"the host's git ignores {(asked == path ? "it" : $"'{asked}'")} by the rule {rule}"
                    : $"the host's git ignores {(asked == path ? "it" : $"'{asked}'")}";
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the snapshot lists <paramref name="path"/> or a folder above it, and the host does not track it.
    /// Synchronous, because the pull and the copy watcher decide on every path; a path the snapshot does not list may
    /// still be ignored if it is new, which <see cref="IgnoresAsync"/> answers.
    /// </summary>
    public bool Lists(string path)
    {
        var tracked = _tracked;
        if (tracked.Files.Contains(path) || tracked.Folders.Contains(path))
        {
            return false;
        }

        if (_files.Contains(path))
        {
            return true;
        }

        foreach (var folder in _folders)
        {
            if (path.StartsWith(folder, StringComparison.Ordinal)
                || (path.Length + 1 == folder.Length && folder.StartsWith(path, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the host's git ignores <paramref name="path"/>: true or false, or null when it could not be asked
    /// (a git failure; an unreachable bridge throws), which a caller treats as "not known to be safe".
    /// </summary>
    public async Task<bool?> IgnoresAsync(string path, CancellationToken ct)
    {
        if (!Answers || Tracks(path, isDirectory: false))
        {
            return false;
        }

        if (Lists(path))
        {
            return true;
        }

        if (Known(path) is { } known)
        {
            return known;
        }

        return await AskAsync([path], ct).ConfigureAwait(false) ? Known(path) : null;
    }

    /// <summary>
    /// What was asked already: ignored when a folder above it was answered ignored, else the path's own answer. Folder
    /// answers are used only here, for new paths, and never by <see cref="Lists"/>, because a folder git calls ignored
    /// can still hold a TRACKED file, and a synced file must not be dropped on the strength of its folder.
    /// </summary>
    private bool? Known(string path)
    {
        foreach (var folder in FoldersAbove(path))
        {
            if (_asked.TryGetValue(folder, out var ignored) && ignored)
            {
                return true;
            }
        }

        return _asked.TryGetValue(path, out var known) ? known : null;
    }

    private static IEnumerable<string> FoldersAbove(string path)
    {
        for (var slash = path.IndexOf('/'); slash > 0 && slash < path.Length - 1; slash = path.IndexOf('/', slash + 1))
        {
            yield return path[..(slash + 1)];
        }
    }

    /// <summary>
    /// Asks about every path not answered yet, in as few git runs as the command line allows, so a burst of new files
    /// costs one host call rather than one each. False when git could not answer; nothing is remembered then.
    /// </summary>
    public async Task<bool> AskAsync(IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        if (!Answers)
        {
            return true;
        }

        var unanswered = new List<string>();
        var queued = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (Lists(path) || Known(path) is not null)
            {
                continue;
            }

            foreach (var question in FoldersAbove(path).Where(f => !_asked.ContainsKey(f)).Append(path))
            {
                if (queued.Add(question))
                {
                    unanswered.Add(question);
                }
            }
        }

        var batch = new List<string>();
        var length = 0;
        foreach (var path in unanswered)
        {
            if (batch.Count > 0 && length + path.Length + 1 > MaxAskCharacters)
            {
                if (!await AskBatchAsync(batch, ct).ConfigureAwait(false))
                {
                    return false;
                }

                batch.Clear();
                length = 0;
            }

            batch.Add(path);
            length += path.Length + 1;
        }

        return batch.Count == 0 || await AskBatchAsync(batch, ct).ConfigureAwait(false);
    }

    private async Task<bool> AskBatchAsync(List<string> batch, CancellationToken ct)
    {
        // quotePath off: git then escapes only what a line of output cannot hold (a quote, a backslash, a control
        // character), and Unquote reads that back. "--" so a name starting with a hyphen stays a name. -v names the
        // rule (source:line:pattern, a tab, then the path) for the withheld-path log (backlog 253); it also prints a
        // path whose deciding rule is a file-level NEGATION, with the "!" pattern, and such a path is not ignored.
        List<string> args = ["-c", "core.quotePath=false", "check-ignore", "-v", "--"];
        args.AddRange(batch);
        var result = await _bridge!.GitAsync(args, ct).ConfigureAwait(false);
        if (result.ExitCode is not (0 or 1))
        {
            return false; // 128: git could not answer. Exit 1 is the ordinary "none of them".
        }

        var ignored = new Dictionary<string, string>(StringComparer.Ordinal);
        if (result.ExitCode == 0)
        {
            foreach (var raw in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // A path git prints with a tab in it is quoted, so the last tab is the separator.
                var line = raw.TrimEnd('\r');
                var tab = line.LastIndexOf('\t');
                if (tab < 0)
                {
                    continue;
                }

                // The source can hold a colon (a Windows path to the person's excludes file), so the pattern is found
                // after the first ":<line number>:".
                var rule = line[..tab];
                if (RuleShape().Match(rule) is { Success: true } shape && shape.Groups[1].Value.StartsWith('!'))
                {
                    continue;
                }

                ignored[Unquote(line[(tab + 1)..]).Replace('\\', '/')] = rule;
            }
        }

        foreach (var path in batch)
        {
            if (ignored.TryGetValue(path, out var rule))
            {
                _rules[path] = rule;
                _asked[path] = true;
            }
            else
            {
                _asked[path] = false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads back a path git printed in its C-style quoting (<c>"a\"b"</c>, <c>\t</c>, octal bytes). A line that does
    /// not start with a quote is the path as it is.
    /// </summary>
    public static string Unquote(string line)
    {
        if (line.Length < 2 || line[0] != '"' || line[^1] != '"')
        {
            return line;
        }

        var bytes = new List<byte>(line.Length);
        for (var i = 1; i < line.Length - 1; i++)
        {
            var c = line[i];
            if (c != '\\' || i + 1 >= line.Length - 1)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }

            var next = line[++i];
            if (next is >= '0' and <= '7' && i + 2 < line.Length - 1)
            {
                bytes.Add(Convert.ToByte(line.Substring(i, 3), 8));
                i += 2;
                continue;
            }

            bytes.Add(next switch
            {
                'a' => 0x07,
                'b' => 0x08,
                't' => 0x09,
                'n' => 0x0A,
                'v' => 0x0B,
                'f' => 0x0C,
                'r' => 0x0D,
                _ => (byte)next,
            });
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    [GeneratedRegex(@"^.*?:\d+:(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleShape();

    /// <summary>The host's tracked files and every folder above one, replaced whole when read again.</summary>
    private sealed class TrackedPaths
    {
        public static readonly TrackedPaths Empty = new([]);

        public TrackedPaths(IEnumerable<string> files)
        {
            foreach (var file in files)
            {
                Files.Add(file);
                for (var slash = file.IndexOf('/'); slash > 0; slash = file.IndexOf('/', slash + 1))
                {
                    Folders.Add(file[..slash]);
                }
            }
        }

        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Folders { get; } = new(StringComparer.Ordinal);
    }
}
