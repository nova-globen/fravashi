using System.Text.RegularExpressions;

namespace Fravashi.Bridge;

/// <summary>One conversation file the CLI wrote for a workspace: its name, size and last-write stamp.</summary>
public sealed record BridgeCliConversation(string Name, long Size, DateTimeOffset ModifiedUtc);

/// <summary>A conversation file over <see cref="BridgeCliConversationService.MaxBytes"/>. Maps to 413.</summary>
public sealed class BridgeCliConversationTooLargeException(string name, long size)
    : Exception($"Conversation '{name}' is {size} bytes, over the import cap.")
{
    public long Size { get; } = size;
}

/// <summary>
/// Read-only access to the conversations the user ran with the Claude CLI OUTSIDE the studio, for one
/// REGISTERED workspace (backlog 43). The CLI keeps one JSONL per conversation under
/// <c>&lt;config&gt;/projects/&lt;encoded working directory&gt;/</c> on the host, so the folder is derived
/// here from the workspace's host root through <see cref="CliProjectFolderName"/> and never from
/// anything the caller sends: the studio names a workspace id and, to read, a bare file name.
/// <para>
/// Containment is to that ONE folder. A name must be a plain <c>*.jsonl</c> file name (no separator, no
/// dot other than the extension, so no <c>..</c>), the resolved path's parent must be the folder
/// itself, and neither the folder nor the file may be a link, because a link is the one way a
/// well-formed name could still land somewhere else. Subfolders (the CLI's per-session tool output) are
/// never listed or served. A read is capped at <see cref="MaxBytes"/> and nothing here ever writes.
/// </para>
/// </summary>
public sealed partial class BridgeCliConversationService(
    WorkspaceRegistry registry, string? projectsRoot = null, long maxBytes = BridgeCliConversationService.MaxBytes)
{
    /// <summary>The largest conversation file the bridge hands over (64 MiB).</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    private readonly string _projectsRoot = projectsRoot ?? DefaultProjectsRoot();

    /// <summary>
    /// Where the host's CLI keeps its projects: <c>CLAUDE_CONFIG_DIR</c> when the host user set one,
    /// otherwise <c>~/.claude</c>, the same rule the CLI itself follows.
    /// </summary>
    public static string DefaultProjectsRoot()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var home = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
        return Path.Combine(home, "projects");
    }

    /// <summary>The conversations for a workspace, newest first; empty when the CLI never ran there.</summary>
    public IReadOnlyList<BridgeCliConversation> List(string workspaceId)
    {
        var folder = FolderFor(workspaceId);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        RefuseLink(new DirectoryInfo(folder));
        var conversations = new List<BridgeCliConversation>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            var file = new FileInfo(path);
            if (!IsConversationName(file.Name) || file.LinkTarget is not null)
            {
                continue;
            }

            conversations.Add(new BridgeCliConversation(
                file.Name, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
        }

        return [.. conversations.OrderByDescending(c => c.ModifiedUtc)];
    }

    /// <summary>
    /// The whole file, read once and capped: a file that is (or grows while being read) over the cap
    /// throws <see cref="BridgeCliConversationTooLargeException"/> rather than being cut, because a
    /// silently truncated transcript would read as a complete one.
    /// </summary>
    public (byte[] Bytes, DateTimeOffset ModifiedUtc) Read(string workspaceId, string name)
    {
        if (!IsConversationName(name))
        {
            throw new ArgumentException($"Not a conversation file name: {name}", nameof(name));
        }

        var folder = FolderFor(workspaceId);
        var path = Path.GetFullPath(Path.Combine(folder, name));
        if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(folder), StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Path escapes the conversation folder: {name}");
        }

        if (!Directory.Exists(folder))
        {
            throw new FileNotFoundException($"No such conversation: {name}");
        }

        RefuseLink(new DirectoryInfo(folder));
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"No such conversation: {name}");
        }

        RefuseLink(file);
        if (file.Length > maxBytes)
        {
            throw new BridgeCliConversationTooLargeException(name, file.Length);
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                throw new BridgeCliConversationTooLargeException(name, buffer.Length);
            }
        }

        return (buffer.ToArray(), new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
    }

    /// <summary>The one folder a workspace's conversations may come from, derived from its HOST root.</summary>
    public string FolderFor(string workspaceId)
    {
        var workspace = registry.TryGet(workspaceId)
            ?? throw new KeyNotFoundException($"Unknown workspace '{workspaceId}'.");
        return Path.Combine(_projectsRoot, CliProjectFolderName.Encode(workspace.RootPath));
    }

    public static bool IsConversationName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 128 && ConversationName().IsMatch(name);

    private static void RefuseLink(FileSystemInfo info)
    {
        if (info.LinkTarget is not null)
        {
            throw new UnauthorizedAccessException($"Refusing to follow a link: {info.Name}");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]*\.jsonl\z", RegexOptions.CultureInvariant)]
    private static partial Regex ConversationName();
}
