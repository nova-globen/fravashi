namespace Fravashi.Infrastructure.Tests.Sessions.Containment;

/// <summary>
/// A REAL directory tree for the containment differential to be measured against, in a temporary
/// directory the test owns and deletes.
/// <para>
/// It exists because ground truth cannot come from a second copy of the studio's own path arithmetic.
/// Every question the oracle answers ("does this land inside the copy") is answered by the operating
/// system walking these actual entries, so the tree has to hold the actual entries: a copy, a secret
/// area outside it, a directory whose name is a PREFIX NEIGHBOUR of the copy, and the symlinks that
/// make a path's destination differ from its spelling.
/// </para>
/// <para>
/// The layout is deliberately the shape of the four escapes:
/// <list type="bullet">
/// <item><c>nested</c> points DEEPER INSIDE the copy, which is the case where the kernel's answer stays
/// in while string arithmetic walks out (round 4).</item>
/// <item><c>out</c> points OUTSIDE the copy, which is the opposite: the spelling stays in while the
/// kernel walks out, and it is why a lexical-only check was never enough (rounds 1 to 3).</item>
/// <item><c>w10</c> is a sibling of the copy <c>w1</c> whose absolute path shares its prefix.</item>
/// <item>a file whose NAME contains backslashes, because on Linux that is an ordinary filename
/// character and splitting a path on it invents directory levels the kernel never sees.</item>
/// <item>a dangling link and a link loop, because both are things the studio cannot resolve and must
/// therefore refuse rather than guess at.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ContainmentFixture : IDisposable
{
    private ContainmentFixture(string root, bool symlinks, bool backslashNames)
    {
        Root = root;
        Copy = Path.Combine(root, "w1");
        Neighbour = Path.Combine(root, "w10");
        Secrets = Path.Combine(root, "secrets");
        HasSymlinks = symlinks;
        HasBackslashNames = backslashNames;

        // Backlog 202: this workspace's own agent memory, shaped exactly like production
        // (SessionFrameworkProjector): a shared REAL folder outside the copy, and a session-side
        // symlink to it sitting beside the things that must keep asking.
        MemoryShared = Path.Combine(root, "agent-memory", "w1key");
        OtherMemoryShared = Path.Combine(root, "agent-memory", "otherkey");
        SessionConfigHome = Path.Combine(root, "session", "claude");
        SessionSettings = Path.Combine(SessionConfigHome, "settings.json");
        SessionApprove = Path.Combine(SessionConfigHome, "approve.sh");
        SessionProjectDirectory = Path.Combine(SessionConfigHome, "projects", "w1key");
        SessionTranscript = Path.Combine(SessionProjectDirectory, "abcd1234.jsonl");
        MemoryLink = Path.Combine(SessionProjectDirectory, "memory");

        // Backlog 338: this session's own saved tool output, keyed by the SAME provider id ("abcd1234")
        // as the transcript right beside it, exactly as the real CLI nests "<session>/tool-results" under
        // its project folder. A SEPARATE session's folder tree stands in for "another session's tool
        // results", which naming directly must never be contained by THIS session's root.
        ToolResults = Path.Combine(SessionProjectDirectory, "abcd1234", "tool-results");
        OtherSessionToolResults = Path.Combine(root, "other-session", "claude", "projects", "w1key", "zzzz9999", "tool-results");
    }

    /// <summary>The temporary directory holding everything, copy and secrets alike.</summary>
    public string Root { get; }

    /// <summary>The session's contained copy: the boundary every verdict is about.</summary>
    public string Copy { get; }

    /// <summary>A sibling whose absolute path is <see cref="Copy"/> plus one character.</summary>
    public string Neighbour { get; }

    /// <summary>The stand-in for everything the container holds that an agent must not read.</summary>
    public string Secrets { get; }

    /// <summary>
    /// The REAL shared memory folder for THIS workspace's key (backlog 155/202), the stand-in for
    /// <c>/data/agent-memory/&lt;key&gt;</c>. It is OUTSIDE the copy, exactly as in production.
    /// </summary>
    public string MemoryShared { get; }

    /// <summary>A DIFFERENT workspace's shared memory: naming it directly must never be contained.</summary>
    public string OtherMemoryShared { get; }

    /// <summary>The stand-in for this session's <c>CLAUDE_CONFIG_DIR</c>.</summary>
    public string SessionConfigHome { get; }

    /// <summary>A sibling of the memory link that must keep asking (the approval hook's settings).</summary>
    public string SessionSettings { get; }

    /// <summary>A sibling of the memory link that must keep asking (the approval script, with the secret).</summary>
    public string SessionApprove { get; }

    /// <summary>The stand-in for <c>&lt;config home&gt;/projects/&lt;key&gt;</c>, holding the link and the transcript.</summary>
    public string SessionProjectDirectory { get; }

    /// <summary>A sibling of the memory link that must keep asking (the CLI's own conversation file).</summary>
    public string SessionTranscript { get; }

    /// <summary>
    /// The SESSION-SIDE name (backlog 202): a symlink at <c>&lt;config home&gt;/projects/&lt;key&gt;/memory</c>
    /// pointing at <see cref="MemoryShared"/>, exactly as <c>SessionFrameworkProjector.LinkMemory</c> builds it.
    /// This is the second containment root <c>AgentReadScope</c> is handed.
    /// </summary>
    public string MemoryLink { get; }

    /// <summary>
    /// THIS session's own saved tool output (backlog 338): a REAL folder (never a link, unlike
    /// <see cref="MemoryLink"/>) at <c>&lt;config home&gt;/projects/&lt;key&gt;/abcd1234/tool-results</c>, the
    /// same provider id <see cref="SessionTranscript"/> already uses. This is the third containment root
    /// <c>AgentReadScope</c> is handed.
    /// </summary>
    public string ToolResults { get; }

    /// <summary>
    /// A DIFFERENT session's tool-results folder, under its own wholly separate session tree. Naming it
    /// directly must never be contained by THIS session's <see cref="ToolResults"/> root.
    /// </summary>
    public string OtherSessionToolResults { get; }

    /// <summary>
    /// False when this platform would not let the test create symlinks (Windows without developer
    /// mode), which removes whole families of case rather than weakening them.
    /// </summary>
    public bool HasSymlinks { get; }

    /// <summary>False on Windows, where a backslash cannot be part of a file's name.</summary>
    public bool HasBackslashNames { get; }

    public static ContainmentFixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "fravashi-containment-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(root, "w1", "src", "deep", "a", "b", "c"));
        Directory.CreateDirectory(Path.Combine(root, "w1", "dots", "pad", "pad2"));
        Directory.CreateDirectory(Path.Combine(root, "w10"));
        Directory.CreateDirectory(Path.Combine(root, "secrets", "ring"));

        // Inside the copy. One of them is called "keys.txt" on purpose: a check that matched on the
        // basename rather than the location would call the real one contained.
        File.WriteAllText(Path.Combine(root, "w1", "src", "main.txt"), "inside\n");
        File.WriteAllText(Path.Combine(root, "w1", "src", "deep", "a", "b", "c", "leaf.txt"), "inside\n");
        File.WriteAllText(Path.Combine(root, "w1", "src", "deep", "a", "b", "keys.txt"), "inside\n");

        // Outside it: the tree standing in for /data, the key ring and /proc/1/environ.
        File.WriteAllText(Path.Combine(root, "secrets", "keys.txt"), "secret\n");
        File.WriteAllText(Path.Combine(root, "secrets", "k.txt"), "secret\n");
        File.WriteAllText(Path.Combine(root, "secrets", "ring", "dp.xml"), "secret\n");
        File.WriteAllText(Path.Combine(root, "w10", "keys.txt"), "secret\n");

        // Backlog 202: this workspace's own shared agent memory (outside the copy, exactly as
        // /data/agent-memory/<key> is), a DIFFERENT workspace's memory (naming it directly must never
        // be contained), and the session config home it is linked from, with the siblings that must
        // keep asking: the approval hook's settings, its script, and a transcript file.
        Directory.CreateDirectory(Path.Combine(root, "agent-memory", "w1key", "notes"));
        Directory.CreateDirectory(Path.Combine(root, "agent-memory", "otherkey"));
        Directory.CreateDirectory(Path.Combine(root, "session", "claude", "projects", "w1key"));
        File.WriteAllText(Path.Combine(root, "agent-memory", "w1key", "proof-marker.md"), "remembered\n");
        // A SIBLING of "notes", not a descendant of it: the wildcard-walk tests below are scoped to
        // "notes" precisely so that a wildcard pattern rooted there can be proven safe (round 7's
        // containment check establishes the WHOLE subtree a wildcard can walk holds no escaping link,
        // so "escape" living anywhere under the directory a pattern is rooted at would correctly make
        // that pattern ask, exactly as it should once a real escaping link exists).
        File.WriteAllText(Path.Combine(root, "agent-memory", "w1key", "notes", "other-note.md"), "remembered too\n");
        File.WriteAllText(Path.Combine(root, "agent-memory", "otherkey", "proof-marker.md"), "not yours\n");
        File.WriteAllText(Path.Combine(root, "session", "claude", "settings.json"), "{}\n");
        File.WriteAllText(Path.Combine(root, "session", "claude", "approve.sh"), "#!/bin/sh\n");
        File.WriteAllText(
            Path.Combine(root, "session", "claude", "projects", "w1key", "abcd1234.jsonl"), "{}\n");

        // Backlog 338: this session's own saved tool output, a REAL folder (never a link) right beside the
        // transcript, keyed by the same provider id; and a wholly separate session's tool-results, standing
        // in for "another session's own saved output", which must never be reachable from this one's root.
        Directory.CreateDirectory(
            Path.Combine(root, "session", "claude", "projects", "w1key", "abcd1234", "tool-results", "logs"));
        File.WriteAllText(
            Path.Combine(root, "session", "claude", "projects", "w1key", "abcd1234", "tool-results", "proof-marker.txt"),
            "saved output\n");
        // A SIBLING of "logs", not a descendant of it, for the same reason MemoryShared's "notes" is a
        // sibling of its own "escape": the wildcard-walk tests below are scoped to "logs" precisely so a
        // wildcard pattern rooted there can be proven safe (round 7's containment check establishes the
        // WHOLE subtree a wildcard can walk holds no escaping link, so "escape" living anywhere under the
        // directory a pattern is rooted at would correctly make that pattern ask).
        File.WriteAllText(
            Path.Combine(root, "session", "claude", "projects", "w1key", "abcd1234", "tool-results", "logs", "other-result.txt"),
            "saved output too\n");
        Directory.CreateDirectory(Path.Combine(root, "other-session", "claude", "projects", "w1key", "zzzz9999", "tool-results"));
        File.WriteAllText(
            Path.Combine(root, "other-session", "claude", "projects", "w1key", "zzzz9999", "tool-results", "proof-marker.txt"),
            "not yours\n");

        var symlinks = TryLinks(root);
        var backslashNames = TryBackslashName(root);

        return new ContainmentFixture(root, symlinks, backslashNames);
    }

    private static bool TryLinks(string root)
    {
        var copy = Path.Combine(root, "w1");
        try
        {
            // Deeper INSIDE the copy: the shape where the kernel's answer and the caller's arithmetic
            // disagree in the direction that keeps the read in.
            Directory.CreateSymbolicLink(Path.Combine(copy, "nested"), Path.Combine("src", "deep", "a", "b"));

            // A link to a link, still inside: the chain has to be followed, not just the first hop.
            Directory.CreateSymbolicLink(Path.Combine(copy, "hop"), "nested");

            // OUTSIDE the copy, while every character of the path that reaches it reads as inside.
            Directory.CreateSymbolicLink(Path.Combine(copy, "out"), Path.Combine("..", "secrets"));
            Directory.CreateSymbolicLink(Path.Combine(copy, "neighbour"), Path.Combine("..", "w10"));

            // Dangling, and pointed outside: a read through it fails, but it still says where it aimed.
            File.CreateSymbolicLink(
                Path.Combine(copy, "dangling"), Path.Combine("..", "secrets", "missing.txt"));

            // A loop, which the OS refuses to resolve and the studio therefore must not guess at.
            File.CreateSymbolicLink(Path.Combine(copy, "loop"), "loop2");
            File.CreateSymbolicLink(Path.Combine(copy, "loop2"), "loop");

            // Backlog 202: the SESSION-SIDE name, a symlink to the shared memory, built exactly the way
            // SessionFrameworkProjector.LinkMemory builds the real one.
            Directory.CreateSymbolicLink(
                Path.Combine(root, "session", "claude", "projects", "w1key", "memory"),
                Path.Combine("..", "..", "..", "..", "agent-memory", "w1key"));

            // A link INSIDE memory that points OUT of it: an agent's own note could hold one, and a
            // read through it must escape the same way a read through "out" inside the copy does.
            Directory.CreateSymbolicLink(
                Path.Combine(root, "agent-memory", "w1key", "escape"),
                Path.Combine("..", "..", "secrets"));

            // Backlog 338: a link INSIDE this session's own tool-results folder that points OUT of it,
            // the same shape as memory's "escape" above. Six levels up (tool-results/abcd1234/w1key/
            // projects/claude/session) reaches root, then into secrets.
            Directory.CreateSymbolicLink(
                Path.Combine(root, "session", "claude", "projects", "w1key", "abcd1234", "tool-results", "escape"),
                Path.Combine("..", "..", "..", "..", "..", "..", "secrets"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryBackslashName(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            // ONE file, whose name happens to contain three backslashes. Anything that splits a path on
            // a backslash sees four directory levels here; the kernel sees a name.
            File.WriteAllText(Path.Combine(root, "w1") + "/a\\b\\c\\keys.txt", "inside\n");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
