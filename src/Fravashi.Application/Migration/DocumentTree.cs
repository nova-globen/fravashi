using System.Text.RegularExpressions;
using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Migration;

/// <summary>One document to import: where it came from, what it becomes, and its body with its links rewritten.</summary>
/// <param name="Path">
/// The repository-relative path it was read from; for a folder's own document, the folder's path with no trailing slash.
/// </param>
/// <param name="Number">The wiki number it will have (the project is empty, so creation order decides it).</param>
/// <param name="ParentPath">The folder document it sits under, by that folder's path, or null at the top.</param>
/// <param name="Aliases">Other paths a link may use to mean this document: a folder with a slash, the folder's README.</param>
public sealed record DocumentDraft(
    string Path, int Number, WikiItemType Type, string Title, string Body, string? ParentPath = null,
    IReadOnlyList<string>? Aliases = null)
{
    /// <summary>Every path that means this document.</summary>
    public IEnumerable<string> Paths => [Path, .. Aliases ?? []];
}

/// <summary>
/// Chooses what a repository's documents become in the wiki (backlog 49, <c>docs/plans/dogfood-migration.md</c>).
/// <para>
/// Every markdown file under <c>docs/</c> is imported except the backlog, which becomes work items, and the folders
/// they sit in are kept: each folder becomes a document of its own, named after it, with the folder's README as its
/// body when it has one, and everything in the folder sits under it. That was a correction on 2026-09-23: the first
/// cut imported a chosen few files and flattened them, and the owner's reading of it was that the documents no longer
/// followed the repository's own structure.
/// </para>
/// <para>
/// Because the target project is empty, the number every document will have is known before any is created (parents
/// first, in a stable order), so a link from one imported document to another, or to a folder or its README, is
/// rewritten to that document's <c>@N</c> reference in the same pass. A link to anything not imported is left exactly as
/// written.
/// </para>
/// </summary>
public static partial class DocumentTree
{
    private const string Root = "docs";

    /// <summary>What a repository-relative markdown path becomes, or null when it is not imported.</summary>
    public static WikiItemType? TypeOf(string path)
    {
        var p = path.Replace('\\', '/');
        if (!p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !p.StartsWith(Root + "/", StringComparison.Ordinal)
            || p == RepositoryImport.BacklogPath)
        {
            return null;
        }

        return p switch
        {
            _ when p.StartsWith("docs/adr/", StringComparison.Ordinal) => WikiItemType.Adr,
            _ when p.StartsWith("docs/plans/", StringComparison.Ordinal) => WikiItemType.Plan,
            _ when p.StartsWith("docs/evidences/", StringComparison.Ordinal) => WikiItemType.Evidence,
            _ when p.StartsWith("docs/drafts/", StringComparison.Ordinal) => WikiItemType.Draft,
            _ when p.StartsWith("docs/prompts/", StringComparison.Ordinal) => WikiItemType.Draft,
            "docs/vision.md" or "docs/roadmap.md" or "docs/evidence-lifecycle.md" or "docs/reliability-targets.md"
                => WikiItemType.Governance,
            "docs/v2.md" => WikiItemType.Plan,
            "docs/layout.md" => WikiItemType.Architecture,
            _ => WikiItemType.Reference,
        };
    }

    /// <summary>
    /// The drafts for the given files, parents before their children and in a stable order (a folder's own files by
    /// path, then its folders by path), numbered from <paramref name="firstNumber"/>. Files that are not imported are
    /// left out, and so is a folder with nothing imported in it.
    /// </summary>
    public static IReadOnlyList<DocumentDraft> Plan(IReadOnlyDictionary<string, string> files, int firstNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(files);
        var types = new Dictionary<string, WikiItemType>(StringComparer.Ordinal);
        foreach (var path in files.Keys)
        {
            var normalized = path.Replace('\\', '/');
            if (TypeOf(normalized) is { } type)
            {
                types[normalized] = type;
            }
        }

        return Plan(files, types, firstNumber);
    }

    /// <summary>
    /// The drafts for exactly the files in <paramref name="types"/>, each as the type given (item #373: a person chose
    /// them in the setup's preview), parents before their children, numbered from <paramref name="firstNumber"/>. A file
    /// under <c>docs/</c> sits in the tree below <c>docs/</c>, as it always did; any other file (the root README, a
    /// package's notes) sits in the tree below the repository root, so its folders are kept as documents too. The
    /// <c>docs/</c> tree comes first, so a selection of <c>docs/</c> files alone is numbered exactly as before.
    /// </summary>
    public static IReadOnlyList<DocumentDraft> Plan(
        IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, WikiItemType> types, int firstNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(types);
        var texts = files.ToDictionary(f => f.Key.Replace('\\', '/'), f => f.Value, StringComparer.Ordinal);
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
        var chosenTypes = new Dictionary<string, WikiItemType>(StringComparer.Ordinal);
        foreach (var (raw, type) in types)
        {
            var path = raw.Replace('\\', '/');
            if (texts.TryGetValue(path, out var text))
            {
                chosen[path] = text;
                chosenTypes[path] = type;
            }
        }

        // Every folder between a file's root (docs/, or the repository root) and the file, which is what the tree is
        // made of.
        var folders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in chosen.Keys)
        {
            var root = RootOf(path);
            for (var folder = FolderOf(path); folder.Length > root.Length; folder = FolderOf(folder))
            {
                folders.Add(folder);
            }
        }

        // A folder's README is the folder's own document, not a second one beside it.
        var readmes = folders.ToDictionary(
            f => f,
            f => chosen.Keys.Where(p => FolderOf(p) == f && IsReadme(p)).Order(StringComparer.Ordinal).FirstOrDefault(),
            StringComparer.Ordinal);

        var order = new List<string>();
        void Walk(string folder)
        {
            order.AddRange(chosen.Keys
                .Where(p => FolderOf(p) == folder && readmes.GetValueOrDefault(folder) != p)
                .Order(StringComparer.Ordinal));
            foreach (var sub in folders.Where(f => FolderOf(f) == folder))
            {
                order.Add(sub);
                Walk(sub);
            }
        }

        Walk(Root);
        Walk(string.Empty);

        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
        {
            numbers[order[i]] = firstNumber + i;
            if (folders.Contains(order[i]))
            {
                // A link to the folder, to the folder with a slash, or to its README all mean the folder's document.
                numbers[order[i] + "/"] = firstNumber + i;
                if (readmes[order[i]] is { } readme)
                {
                    numbers[readme] = firstNumber + i;
                }
            }
        }

        return [.. order.Select(path =>
        {
            var parent = FolderOf(path);
            var parentPath = parent.Length > RootOf(path).Length ? parent : null;
            if (folders.Contains(path))
            {
                var readme = readmes[path];
                var body = readme is null ? string.Empty : Rewrite(readme, chosen[readme], numbers);
                return new DocumentDraft(
                    path, numbers[path], FolderType(path), NameOf(path), body, parentPath,
                    readme is null ? [path + "/"] : [path + "/", readme]);
            }

            return new DocumentDraft(
                path, numbers[path], chosenTypes[path], TitleOf(path, chosen[path]), Rewrite(path, chosen[path], numbers), parentPath);
        })];
    }

    /// <summary>The root a file's tree hangs from: <c>docs</c> for a file under it, else the repository root.</summary>
    private static string RootOf(string path) =>
        path.StartsWith(Root + "/", StringComparison.Ordinal) ? Root : string.Empty;

    private static string FolderOf(string path) =>
        path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static bool IsReadme(string path) =>
        NameOf(path).Equals("README.md", StringComparison.OrdinalIgnoreCase)
        || NameOf(path).Equals("README.markdown", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A folder's document takes the type its files would have, so the ADR folder is among the ADRs; a folder outside
    /// <c>docs/</c> is Reference.
    /// </summary>
    private static WikiItemType FolderType(string folder) => TypeOf(folder + "/folder.md") ?? WikiItemType.Reference;

    /// <summary>The first <c># </c> heading, else the file name as words.</summary>
    internal static string TitleOf(string path, string text)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal) && line[2..].Trim() is { Length: > 0 } heading)
            {
                return heading;
            }
        }

        return System.IO.Path.GetFileNameWithoutExtension(path).Replace('-', ' ');
    }

    /// <summary>
    /// Rewrites every markdown link whose target resolves to another imported document into <c>text (@N)</c>, which
    /// the wiki renders as a reference; every other link is left as written.
    /// </summary>
    internal static string Rewrite(string fromPath, string text, IReadOnlyDictionary<string, int> numbers)
    {
        var folder = FolderOf(fromPath.Replace('\\', '/'));
        return MarkdownLink().Replace(text, match =>
        {
            var target = match.Groups[2].Value;
            var hash = target.IndexOf('#', StringComparison.Ordinal);
            var file = hash < 0 ? target : target[..hash];
            if (file.Length == 0 || file.Contains("://", StringComparison.Ordinal))
            {
                return match.Value;
            }

            var resolved = Resolve(folder, file);
            return resolved is not null
                && (numbers.TryGetValue(resolved, out var number) || numbers.TryGetValue(resolved + "/", out number))
                ? $"{match.Groups[1].Value} (@{number})"
                : match.Value;
        });
    }

    private static string? Resolve(string folder, string relative)
    {
        var parts = relative.StartsWith('/') ? [] : folder.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(Uri.UnescapeDataString(part));
        }

        return string.Join('/', parts) + (relative.EndsWith('/') ? "/" : string.Empty);
    }

    /// <summary>A markdown link, <c>[text](target)</c>, not an image.</summary>
    [GeneratedRegex(@"(?<!!)\[([^\]\n]+)\]\(([^)\s]+)\)")]
    private static partial Regex MarkdownLink();
}
