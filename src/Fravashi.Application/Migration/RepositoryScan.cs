using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Migration;

/// <summary>One entry of a folder listing: a file with its size, or a folder.</summary>
public sealed record RepositoryEntry(string Path, bool IsDirectory, long Size);

/// <summary>
/// A folder's files, READ ONLY, for the setup's import (item #373): the window's folder through the host bridge in the
/// studio, a local folder in a test or a dev run. Nothing here ever writes, and nothing runs but git's own listing.
/// </summary>
public interface IRepositoryFiles
{
    /// <summary>
    /// The markdown files git tracks (<c>git ls-files</c>, so <c>.gitignore</c> is honoured), repository-relative with
    /// <c>/</c>; null when the folder is not a git repository or git cannot be read there, which the scan answers with a
    /// walk of the folder instead.
    /// </summary>
    Task<IReadOnlyList<string>?> TrackedMarkdownAsync(CancellationToken ct);

    /// <summary>One folder's entries ("" is the root); empty when it is not there.</summary>
    Task<IReadOnlyList<RepositoryEntry>> ListAsync(string folder, CancellationToken ct);

    /// <summary>At most the first <paramref name="maxBytes"/> of a file as text, or null when it is not there.</summary>
    Task<string?> ReadHeadAsync(string path, int maxBytes, CancellationToken ct);

    /// <summary>A whole file as text, or null when it is not there.</summary>
    Task<string?> ReadAllAsync(string path, CancellationToken ct);
}

/// <summary>A markdown file the scan found, and what the preview suggests doing with it.</summary>
/// <param name="Path">Repository-relative, with <c>/</c>.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Suggested">What it becomes unless a person says otherwise.</param>
/// <param name="Include">Whether it is checked unless a person says otherwise.</param>
/// <param name="Note">A line the preview shows under it ("24 items, numbers kept"), or null.</param>
/// <param name="TooLarge">Over the import's size limit: listed, never imported.</param>
/// <param name="WorkItemCount">How many work items it gives, for a file that becomes work items; else 0.</param>
public sealed record ScanCandidate(
    string Path, long Size, ImportTarget Suggested, bool Include, string? Note = null, bool TooLarge = false,
    int WorkItemCount = 0);

/// <summary>Where a running scan has got to: how many files it has looked at and what it has found so far.</summary>
public sealed record ScanProgress(int FilesSeen, IReadOnlyList<ScanCandidate> Found);

/// <summary>What a finished scan found.</summary>
/// <param name="Candidates">Every markdown file found, in path order, up to <see cref="RepositoryScan.Cap"/>.</param>
/// <param name="Capped">True when the scan stopped listing at the cap.</param>
/// <param name="FromGit">True when git's own listing was used; false for a walk of a folder that is not a repository.</param>
public sealed record ScanResult(IReadOnlyList<ScanCandidate> Candidates, bool Capped, bool FromGit)
{
    /// <summary>The checked files that become documents.</summary>
    public int DocumentsChecked => Candidates.Count(c => c.Include && !c.TooLarge && !c.Suggested.IsWorkItems);

    /// <summary>The work items the checked work-item files give.</summary>
    public int WorkItemsChecked => Candidates.Where(c => c.Include && !c.TooLarge && c.Suggested.IsWorkItems).Sum(c => c.WorkItemCount);
}

/// <summary>
/// Finds what a folder already has that the setup can import (item #373, <c>docs/plans/first-open-setup.md</c> section
/// 3.5), and suggests what each file becomes. It stays fast on a large repository by reading as little as it can: the
/// listing is paths and sizes only; only a file whose NAME says it may be a list (backlog, todo, tasks, checklist) has
/// its first 8 KB read to decide whether it is one; a title is read from at most the first 8 KB, and only when the
/// preview asks for it (the rows a person can see); full text is read only for the files that become work items (to
/// count them) and, at import time, for the checked files. It stops listing at <see cref="Cap"/> files.
/// </summary>
public sealed class RepositoryScan(IRepositoryFiles files, Func<string, bool, bool>? isIgnored = null)
{
    /// <summary>The most markdown files the scan lists.</summary>
    public const int Cap = 5000;

    /// <summary>The largest file the import takes.</summary>
    public const long MaxImportBytes = 1024 * 1024;

    /// <summary>How much of a file is read for its title or to tell whether it is a list.</summary>
    public const int HeadBytes = 8 * 1024;

    /// <summary>Folders a walk never enters, whatever the ignore rules say.</summary>
    private static readonly HashSet<string> NeverWalked = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj",
    };

    /// <summary>The folder's files, for reading titles and, at import, the checked files.</summary>
    public IRepositoryFiles Files => files;

    /// <summary>Lists the folder's markdown files and suggests what each becomes.</summary>
    /// <param name="progress">Told after every folder, with the files found so far, so the preview fills as it goes.</param>
    /// <param name="cap">The most files listed; <see cref="Cap"/> unless a test says otherwise.</param>
    public async Task<ScanResult> RunAsync(IProgress<ScanProgress>? progress = null, CancellationToken ct = default, int cap = Cap)
    {
        var found = new List<RepositoryEntry>();
        var seen = 0;
        var capped = false;
        var tracked = await files.TrackedMarkdownAsync(ct).ConfigureAwait(false);
        if (tracked is not null)
        {
            // Git's listing names the files; one listing per folder they sit in gives their sizes.
            foreach (var group in tracked.Select(Normalize).Where(IsMarkdown).Distinct(StringComparer.Ordinal)
                         .GroupBy(FolderOf, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var sizes = (await files.ListAsync(group.Key, ct).ConfigureAwait(false))
                    .Where(e => !e.IsDirectory)
                    .ToDictionary(e => Normalize(e.Path), e => e.Size, StringComparer.Ordinal);
                foreach (var path in group.Order(StringComparer.Ordinal))
                {
                    seen++;
                    if (sizes.TryGetValue(path, out var size))
                    {
                        if (found.Count >= cap)
                        {
                            capped = true;
                            break;
                        }

                        found.Add(new RepositoryEntry(path, false, size));
                    }
                }

                progress?.Report(new ScanProgress(seen, [.. found.Select(f => Suggest(f.Path, f.Size))]));
                if (capped)
                {
                    break;
                }
            }
        }
        else
        {
            var pending = new Queue<string>();
            pending.Enqueue(string.Empty);
            while (pending.Count > 0 && !capped)
            {
                ct.ThrowIfCancellationRequested();
                var folder = pending.Dequeue();
                foreach (var entry in (await files.ListAsync(folder, ct).ConfigureAwait(false))
                             .OrderBy(e => e.Path, StringComparer.Ordinal))
                {
                    var path = Normalize(entry.Path);
                    if (entry.IsDirectory)
                    {
                        if (!NeverWalked.Contains(NameOf(path)) && isIgnored?.Invoke(path, true) != true)
                        {
                            pending.Enqueue(path);
                        }

                        continue;
                    }

                    seen++;
                    if (!IsMarkdown(path) || isIgnored?.Invoke(path, false) == true)
                    {
                        continue;
                    }

                    if (found.Count >= cap)
                    {
                        capped = true;
                        break;
                    }

                    found.Add(new RepositoryEntry(path, false, entry.Size));
                }

                progress?.Report(new ScanProgress(seen, [.. found.Select(f => Suggest(f.Path, f.Size))]));
            }
        }

        var candidates = new List<ScanCandidate>(found.Count);
        foreach (var file in found.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            candidates.Add(await ClassifyAsync(file, ct).ConfigureAwait(false));
        }

        return new ScanResult(candidates, capped, tracked is not null);
    }

    /// <summary>
    /// A file's title for the preview: its first <c># </c> heading within the first 8 KB, else its file name as words.
    /// </summary>
    public async Task<string> TitleAsync(string path, CancellationToken ct = default)
    {
        var head = await files.ReadHeadAsync(path, HeadBytes, ct).ConfigureAwait(false);
        return DocumentTree.TitleOf(path, head ?? string.Empty);
    }

    /// <summary>The full text of every chosen file, read now; a file that has gone is left out.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(ImportSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var choice in selection.Files)
        {
            if (await files.ReadAllAsync(choice.Path, ct).ConfigureAwait(false) is { } text)
            {
                texts[choice.Path] = text;
            }
        }

        return texts;
    }

    /// <summary>
    /// What a markdown file becomes by its path alone, before anything is read: under <c>docs/</c> the type the folder
    /// gives it, checked; the root README Reference and CONTRIBUTING and CODE_OF_CONDUCT Governance, checked; the root
    /// CHANGELOG Reference, unchecked (it is the repository's own history); anything else Reference, unchecked, so a
    /// monorepo's package READMEs do not flood the wiki.
    /// </summary>
    public static (ImportTarget Target, bool Include) ByPath(string path)
    {
        var p = Normalize(path);
        var asMd = p.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase) ? p[..^".markdown".Length] + ".md" : p;
        if (asMd.StartsWith("docs/", StringComparison.Ordinal))
        {
            // The legacy import kept docs/backlog.md out because it became work items; a backlog that is NOT one the
            // parser reads (Stomme's is a table) is a document like any other.
            return (ImportTarget.Document(DocumentTree.TypeOf(asMd) ?? WikiItemType.Reference), true);
        }

        if (!asMd.Contains('/', StringComparison.Ordinal))
        {
            return Path.GetFileNameWithoutExtension(asMd).ToUpperInvariant() switch
            {
                "README" => (ImportTarget.Document(WikiItemType.Reference), true),
                "CONTRIBUTING" or "CODE_OF_CONDUCT" => (ImportTarget.Document(WikiItemType.Governance), true),
                "CHANGELOG" => (ImportTarget.Document(WikiItemType.Reference), false),
                _ => (ImportTarget.Document(WikiItemType.Reference), false),
            };
        }

        return (ImportTarget.Document(WikiItemType.Reference), false);
    }

    /// <summary>True when a file's NAME says it may be a list worth reading the head of.</summary>
    public static bool MayBeAList(string path)
    {
        var name = Path.GetFileNameWithoutExtension(Normalize(path)).ToLowerInvariant();
        return name.Contains("backlog", StringComparison.Ordinal) || name.Contains("todo", StringComparison.Ordinal)
            || name.Contains("tasks", StringComparison.Ordinal) || name.Contains("checklist", StringComparison.Ordinal);
    }

    private static ScanCandidate Suggest(string path, long size)
    {
        if (size > MaxImportBytes)
        {
            return new ScanCandidate(path, size, ByPath(path).Target, false, "Too large to import (over 1 MB)", TooLarge: true);
        }

        var (target, include) = ByPath(path);
        return new ScanCandidate(path, size, target, include);
    }

    private async Task<ScanCandidate> ClassifyAsync(RepositoryEntry file, CancellationToken ct)
    {
        var byPath = Suggest(file.Path, file.Size);
        if (byPath.TooLarge || !MayBeAList(file.Path))
        {
            return byPath;
        }

        var head = await files.ReadHeadAsync(file.Path, HeadBytes, ct).ConfigureAwait(false) ?? string.Empty;
        var isBacklog = BacklogMarkdown.Parse(head).Count > 0;
        if (!isBacklog && !ChecklistMarkdown.IsChecklist(file.Path, head))
        {
            return byPath;
        }

        // A list: its whole text is read now, once, to say how many items it gives. There are only ever a few.
        var text = await files.ReadAllAsync(file.Path, ct).ConfigureAwait(false) ?? head;
        if (isBacklog)
        {
            var entries = BacklogMarkdown.Parse(text);
            return byPath with
            {
                Suggested = ImportTarget.WorkItems, Include = true, WorkItemCount = entries.Count,
                Note = $"{Count(entries.Count, "item")}, numbers kept",
            };
        }

        var boxes = ChecklistMarkdown.Parse(text);
        var done = boxes.Count(b => b.Done);
        return byPath with
        {
            Suggested = ImportTarget.WorkItems, Include = true, WorkItemCount = boxes.Count,
            Note = done > 0 ? $"{Count(boxes.Count, "item")} from its checklist, {done} done" : $"{Count(boxes.Count, "item")} from its checklist",
        };
    }

    /// <summary>What <paramref name="path"/>'s file gives as work items, read in full: for the Becomes list's change.</summary>
    public async Task<(int Count, string Note)?> WorkItemsOfAsync(string path, CancellationToken ct = default)
    {
        var text = await files.ReadAllAsync(path, ct).ConfigureAwait(false);
        if (text is null)
        {
            return null;
        }

        var entries = BacklogMarkdown.Parse(text);
        if (entries.Count > 0)
        {
            return (entries.Count, $"{Count(entries.Count, "item")}, numbers kept");
        }

        var boxes = ChecklistMarkdown.Parse(text);
        return boxes.Count > 0
            ? (boxes.Count, $"{Count(boxes.Count, "item")} from its checklist")
            : (0, "No backlog items or checklist lines in it, so it gives no work items");
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static bool IsMarkdown(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static string FolderOf(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : string.Empty;

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];
}

/// <summary>
/// Makes the scan for the folder open in this window (item #373), with the folder's own ignore rules for a walk. Null
/// when the window's folder cannot be read (no host bridge and no dev repository root), which the import step says in
/// words.
/// </summary>
public interface IRepositoryScans
{
    /// <summary>The scan for this window's folder, or null when there is no folder to read.</summary>
    Task<RepositoryScan?> ForWindowAsync(CancellationToken ct = default);
}
