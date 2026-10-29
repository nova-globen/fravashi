using System.IO.Enumeration;
using System.Text;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>The answer to "may this read run without asking the user", and why.</summary>
/// <param name="Contained">True only when the call provably stays inside the session's copy.</param>
/// <param name="Reason">
/// The sentence the user (or the agent, on a denial) is shown. Never empty: an auto-allow explains
/// itself in the decision document the CLI logs, and a prompt explains why it appeared.
/// </param>
public sealed record AgentReadVerdict(bool Contained, string Reason)
{
    public static AgentReadVerdict Allow(string reason) => new(true, reason);

    public static AgentReadVerdict Ask(string reason) => new(false, reason);
}

/// <summary>
/// Decides whether an auto-allowed read stays inside the session's contained copy (plan 2d decision 2).
/// <para>
/// The allowlist in <see cref="AgentToolSafety"/> says a read cannot CHANGE anything, which was never
/// the same as saying it cannot TAKE anything. The agent runs in the studio's own container as the
/// studio's own uid, so an unscoped <c>Read</c> reaches <c>/proc/1/environ</c> (the host-bridge URL and
/// token, the window token, the integrity key), <c>/data/fravashi.db</c> and the Data Protection key
/// ring that decrypts the stored provider credentials. Withholding those from the child's own
/// environment (<c>AgentProcessEnvironment</c>) does nothing about a file it can simply open, so this is
/// the other half of that containment: inside the copy is unattended, everything else asks.
/// </para>
/// <para>
/// It FAILS CLOSED in every direction. An unknown root, a missing mandatory path, a path that will not
/// normalise, a symlink the studio cannot follow and a link loop all ASK rather than allow.
/// </para>
/// <para>
/// Containment is decided by the OPERATING SYSTEM rather than by path arithmetic done here, and it has
/// to hold under EVERY way the path is really going to be resolved. Predicting one of them was the
/// standing defect: five separate escapes came out of the studio's own string analysis disagreeing
/// with the thing that opens the file, and the last of them out of checking two true readings
/// separately when what runs is the two of them composed.
/// </para>
/// <para>
/// The KERNEL reading, because the kernel is what finally opens it. <see cref="KernelPath"/> asks it
/// (<c>realpath(3)</c> on Linux, <c>GetFinalPathNameByHandle</c> on Windows) instead of guessing, so
/// symlinks, <c>..</c>, <c>.</c> padding, case and what counts as a SEPARATOR are all the kernel's
/// business. That last one mattered: on Linux a backslash is an ordinary filename character, so
/// splitting a path on it invented directory levels that were never there, and a pattern's <c>..</c>
/// chain was then measured from a base eight deep instead of one. A path that does not exist yet is
/// canonicalised as far as it does exist and stays literal after that, so an absent leaf cannot dodge
/// the check.
/// </para>
/// <para>
/// The kernel is asked about the path AS WRITTEN, and that is not a detail. Round FIVE of this was
/// asking it about a path <c>Path.GetFullPath</c> had already rewritten: with <c>out</c> a link
/// leaving the copy, <c>out/../secrets/keys.txt</c> was collapsed to <c>&lt;copy&gt;/secrets/keys.txt</c>
/// before <c>realpath</c> ever saw it, so the kernel dutifully confirmed a path that was never sent to
/// it and the read of the real secret ran unattended. The kernel resolves a link and THEN climbs from
/// where it landed; string arithmetic climbs from where the link was written. Handing the kernel the
/// arithmetic's answer is the lexical reading wearing the kernel's name, which is the same defect as
/// the four before it. Only <c>.</c> and empty components are removed on the way (<c>Compact</c>),
/// because those mean the directory itself whatever any component turns out to be, and a path padded
/// past PATH_MAX would otherwise be refused rather than judged.
/// </para>
/// <para>
/// And the LEXICAL reading, because the program that hands the kernel that path does its own string
/// arithmetic first: a glob walker counts <c>..</c> from the name it was given, and a CLI resolves a
/// relative argument before it opens anything. Kernel truth alone is NOT enough there, and it was
/// measured in the container: with <c>shortcut</c> a link to <c>a/b/c</c>, both inside the copy,
/// <c>realpath("&lt;copy&gt;/shortcut/../../../data/keys")</c> stays inside the copy while the same
/// string resolved lexically lands on <c>/data/keys</c>. So a path is contained only when BOTH
/// readings are inside it, which is the studio declining to bet on whose arithmetic runs.
/// </para>
/// <para>
/// And their COMPOSITION, because that is the one a caller performs. Round SIX was requiring each of
/// the two readings to be inside INDEPENDENTLY and calling that enough: "A is inside" and "B is
/// inside" does not imply "kernel(A) is inside", since the lexically collapsed string can name a
/// symlink the as-written kernel walk never reaches. With <c>nested</c> a link DEEPER INSIDE the copy
/// and <c>out</c> a link leaving it, <c>nested/../out/keys.txt</c> read the secret unattended: as
/// written the kernel climbs out of where <c>nested</c> landed to a name that is not there
/// (<c>&lt;copy&gt;/src/deep/a/out/keys.txt</c>, inside), lexically it is <c>&lt;copy&gt;/out/keys.txt</c>
/// (inside), and the composition of the two opens <c>&lt;secrets&gt;/keys.txt</c>. The same shape
/// auto-allowed <c>/data/fravashi.db</c>, <c>/proc/1/environ</c> and the Data Protection key ring in
/// the live container. So the lexical answer is handed BACK to the kernel and that has to be inside
/// too. The boundary must not rest on the CLI happening to collapse <c>..</c> before the hook fires,
/// which it does today and which is undocumented and version specific; and it never held for
/// <c>Glob</c>/<c>Grep</c> patterns at all, which demonstrably arrive un-collapsed.
/// </para>
/// <para>
/// A path-bearing field is only a path when the tool sent it as a JSON STRING. Anything else (an
/// object, an array, a number, a boolean, a null) has a rendering, not a value, and reading that
/// rendering as a directory name is how JSON punctuation became path segments: <c>{"a/b/c/d/e/f/g/h":1}</c>
/// as Glob's <c>path</c> manufactured an eight-deep base that a pattern's eight <c>..</c> then walked
/// back into the copy, and <c>["/data/fravashi.db"]</c> as Read's <c>file_path</c> read as a relative
/// name under it. The JSON kind now travels with the value (<see cref="AgentToolInput"/>) and a
/// path-bearing field that is not text ASKS, without any path arithmetic being attempted on it.
/// </para>
/// <para>
/// A PATTERN is not judged as a path (see <c>Place</c>): glob syntax spells a traversal several ways
/// over (a brace list expands to whole segments, a backslash escapes the next character), so only the
/// pattern's literal prefix is walked, and anything a wildcard stands for is refused rather than
/// guessed at. Neutralising two wildcard characters and walking the rest as if it were a path was the
/// hole this replaced. The pattern is half of where a Glob or a Grep reads, so
/// <see cref="Resolve"/> answers for it too: a call whose <c>path</c> is an ordinary folder deep inside
/// the copy and whose pattern climbs out of it used to be shown to the user as landing INSIDE the copy,
/// which is the reassuring half of a true sentence.
/// </para>
/// <para>
/// And what a wildcard MATCHES, which is round SEVEN. Refusing a magic segment that spells <c>..</c>
/// rested on "those segments are matched against real directory entries, which never include <c>..</c>",
/// and that is true and beside the point: a real directory entry can be a SYMLINK out of the copy. With
/// <c>out</c> a link to the secrets tree, <c>*/keys.txt</c> expands to <c>out/keys.txt</c> and reads the
/// real secret; nothing in the pattern spells a traversal, every part of it the studio could read as a
/// path is inside, and it ran unattended. So the studio does not try to work out which entries a
/// wildcard will match: when a pattern holds one, the region it can walk (the subtree under its literal
/// prefix, and anything a link inside that subtree leads to) is ESTABLISHED to hold no link out of the
/// copy, by walking it. A link out of it means asking; a region too large to walk within
/// <see cref="MaxWalkEntries"/> means asking too. What that walk cannot cover is written down on
/// <see cref="LinkWalk"/> rather than implied.
/// </para>
/// </summary>
public static class AgentReadScope
{
    /// <summary>How many symlinks one path may go through before the studio gives up and asks.</summary>
    private const int MaxLinkFollows = 40;

    /// <summary>A bound on the walk, so a pathological path costs bounded time and then asks.</summary>
    private const int MaxPathSteps = 512;

    /// <summary>
    /// What this PLATFORM counts as a path separator, taken from the platform rather than assumed.
    /// On Linux that is <c>'/'</c> alone: a backslash is an ordinary filename character there, and
    /// splitting on it invented directory levels the kernel does not see, which is exactly the
    /// disagreement the escapes were built on.
    /// </summary>
    private static readonly char[] Separators =
        Path.DirectorySeparatorChar == Path.AltDirectorySeparatorChar
            ? [Path.DirectorySeparatorChar]
            : [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// Every character that makes a pattern segment something other than a plain directory name:
    /// wildcards, character classes, brace lists and the extglob groups' parentheses. A segment holding
    /// one of these is not a name the studio can walk, so the containment check stops in front of it.
    /// </summary>
    private static readonly char[] Magic = ['*', '?', '[', ']', '{', '}', '(', ')', '!'];

    /// <summary>Glob punctuation, dropped when reading what literal text a magic segment spells out.</summary>
    private static readonly char[] MagicPunctuation = ['*', '?', '[', ']', '{', '}', '(', ')', '!', '@', '+', '|', ','];

    /// <summary>How many brace alternatives one pattern may expand to before the studio gives up.</summary>
    private const int MaxPatternAlternatives = 64;

    /// <summary>How many nested brace lists one pattern may hold before the studio gives up.</summary>
    private const int MaxBraceDepth = 16;

    /// <summary>Longer than any honest pattern, and a cheap bound on the work the expansion does.</summary>
    private const int MaxPatternLength = 4096;

    /// <summary>
    /// How many directory entries one auto-allow decision may look at while establishing that a
    /// wildcard cannot walk out of the copy, before the studio stops and asks instead (ADR 0024).
    /// <para>
    /// This is a BUDGET, and it is a trade rather than a fact: too small a bound turns every large
    /// repository into a prompt on the agent's most common call, and an unbounded one lets a single
    /// glob cost an unbounded walk. It was measured rather than guessed, in the studio's own Linux
    /// container on 2026-08-06: a copy of this repository (6,908 entries) costs about 70ms per
    /// decision and a tree AT this budget about 205ms, both against roughly half that for a bare
    /// recursive enumeration of the same tree. Past the budget the answer is a prompt, never an allow.
    /// </para>
    /// </summary>
    private const int MaxWalkEntries = 50_000;

    /// <summary>
    /// Whether this read-only call may run unattended against <paramref name="workspaceRoot"/> (the
    /// session's contained copy, e.g. <c>/workspaces/{id}</c>) or, since backlog 202,
    /// <paramref name="memoryRoot"/>: this session's own slice of its workspace's agent memory
    /// (backlog 155), reached through the session-side link
    /// <c>SessionFrameworkProjector.MemoryLinkPathFor</c> creates. Null means the studio either does
    /// not know one or the session was never linked to its shared memory, and every memory read keeps
    /// asking exactly as it did before this parameter existed.
    /// <para>
    /// The two roots are checked by the SAME machinery below, never a second algorithm: a path is
    /// contained the moment it lands inside either one, under every reading each already demands. A
    /// path reached only through <paramref name="memoryRoot"/>'s session-side link still has to climb
    /// back out through a kernel-resolved symlink read, and a <c>..</c> chain written against that link
    /// still collapses LEXICALLY before the kernel ever sees it, which is what keeps a read from
    /// drifting past the memory folder onto the session's settings, its approval script or a
    /// transcript sitting right beside it.
    /// </para>
    /// <para>
    /// <paramref name="memoryTarget"/> PINS that link: a link is a live thing, and re-locating it on
    /// every call (which this still does, because a studio-recorded canonical answer could itself go
    /// stale) is not enough on its own. Measure the candidate against WHATEVER the link happens to
    /// resolve to right now and nothing else, and the root is, by construction, always "inside itself":
    /// one later approved command that re-points the link (<c>ln -sfn /data &lt;link&gt;</c>) makes
    /// every reading of it agree with every reading of a read through it, because both are resolved
    /// through the SAME live link at the SAME moment. So the link's CURRENT kernel reading is also
    /// compared against the studio's own record of where <c>SessionFrameworkProjector.LinkMemory</c>
    /// actually pointed it (<c>MemoryDirectoryFor</c>), independently named and never itself read
    /// through the link, and the two have to agree exactly. A mismatch (or either side failing to
    /// resolve) drops the memory root entirely, which is what leaves every memory read asking the
    /// moment the link no longer points where the studio put it.
    /// </para>
    /// <para>
    /// Since backlog 338, a THIRD root: <paramref name="toolResultsRoot"/>, exactly the CURRENT
    /// session's own slice of the CLI's saved-tool-output folder
    /// (<c>SessionFrameworkProjector.ToolResultsDirectoryFor</c>), never another session's and never
    /// guessed from a tool call's own text. It needs no second, pinned "target" argument the way the
    /// memory root does (there is only one real path here, not a link-and-target pair), but it is not
    /// trusted on the bare strength of resolving either: the session config home is writable by the
    /// agent's own uid, so <see cref="ContainmentRoots"/> only includes it when its kernel-resolved
    /// reading EQUALS its plain lexical one, meaning no symlink was planted anywhere on the way down
    /// from the config home. It sits in the SAME session folder as the approval hook's settings and
    /// script, which is exactly why it has to be this narrow: a <c>..</c> chain out of it still
    /// collapses lexically before the kernel ever sees it, the same guarantee that keeps a memory read
    /// off those siblings.
    /// </para>
    /// </summary>
    public static AgentReadVerdict Check(
        string toolName,
        AgentToolInput input,
        string? workspaceRoot,
        string? memoryRoot = null,
        string? memoryTarget = null,
        string? toolResultsRoot = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (AgentToolSafety.TargetOf(toolName) is not { } target)
        {
            return AgentReadVerdict.Ask($"{Name(toolName)} is not on the studio's read-only list, so it asks.");
        }

        if (!target.ReadsTheFileSystem)
        {
            return AgentReadVerdict.Allow($"{Name(toolName)} only reads, so the studio allows it automatically.");
        }

        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Path.IsPathRooted(workspaceRoot))
        {
            return AgentReadVerdict.Ask(
                $"The studio does not know which contained copy this session is confined to, so it is asking "
                + $"before letting {Name(toolName)} read anything.");
        }

        if (Locate(workspaceRoot, baseDirectory: null) is not { } root)
        {
            return AgentReadVerdict.Ask(
                $"The studio could not resolve this session's contained copy, so it is asking before letting "
                + $"{Name(toolName)} read anything.");
        }

        var roots = ContainmentRoots(root, memoryRoot, memoryTarget, toolResultsRoot);

        // The tool's own directory: its path argument when it has one, the copy itself when the
        // argument is optional and absent (the CLI resolves that case against the child's working
        // directory, which IS the copy). A path field the tool sent as something other than a JSON
        // string never gets that far: it is not a path, so there is nothing to resolve.
        var directory = root;
        if (FirstText(input, target.PathFields, out var named) is { } refusal)
        {
            return AgentReadVerdict.Ask(NotAPath(toolName, refusal));
        }

        if (named is not null)
        {
            if (Locate(named, root) is not { } resolved)
            {
                return AgentReadVerdict.Ask(Undetermined(toolName, named));
            }

            if (!IsInsideAny(resolved, roots))
            {
                return AgentReadVerdict.Ask(Outside(toolName, named));
            }

            directory = resolved;
        }
        else if (target.PathRequired)
        {
            return AgentReadVerdict.Ask(
                $"{Name(toolName)} did not say which path it would open, so the studio is asking instead of "
                + "assuming one.");
        }

        if (target.PatternField is { } patternField)
        {
            if (FirstText(input, [patternField], out var pattern) is { } patternRefusal)
            {
                return AgentReadVerdict.Ask(NotAPath(toolName, patternRefusal));
            }

            if (pattern is not null)
            {
                switch (Place(pattern, directory, roots))
                {
                    case PatternPlacement.Outside:
                        return AgentReadVerdict.Ask(Outside(toolName, pattern));

                    case PatternPlacement.Traversable:
                        return AgentReadVerdict.Ask(Traversable(toolName, pattern));

                    case PatternPlacement.Undetermined:
                        return AgentReadVerdict.Ask(Undetermined(toolName, pattern));
                }
            }
        }

        return AgentReadVerdict.Allow(
            $"{Name(toolName)} only reads, and it stays inside this workspace's contained copy, its own agent "
            + "memory, or its own saved tool output, so the studio allows it automatically.");
    }

    /// <summary>
    /// The containment roots one call is measured against: always the workspace copy, plus this
    /// session's agent-memory root when <paramref name="memoryRoot"/> names one, it resolves, AND its
    /// current kernel reading is EXACTLY <paramref name="memoryTarget"/>'s (also resolved, never taken
    /// on faith as a bare string). That last check is load-bearing, not belt-and-suspenders: re-locating
    /// the link is what lets a read through it be judged at all, but it is ALSO exactly what would make
    /// a re-pointed link pass, because the candidate and the root would then both be resolved through
    /// the SAME live link at the SAME moment and necessarily agree with each other regardless of where
    /// the link actually points. Comparing the link's answer against an INDEPENDENTLY NAMED expected
    /// target is the only thing that can catch that, and it is why a caller is handed two strings rather
    /// than one.
    /// <para>
    /// That still is not quite enough on its own, which a host review of this change caught before it
    /// was committed. <paramref name="memoryTarget"/> is itself resolved THROUGH THE KERNEL
    /// (<c>Locate</c> follows every symlink in it, same as everything else here), so if the real folder
    /// the studio linked memory to is later replaced by a symlink (remove
    /// <c>/data/agent-memory/&lt;key&gt;</c>, <c>ln -s /data</c> in its place), the link and the expected
    /// target BOTH resolve to <c>/data</c> and agree with each other for the exact same reason a
    /// re-pointed session-side link did. So <paramref name="memoryTarget"/>'s own NAME is checked
    /// separately (<see cref="IsRealDirectory"/>): it has to be a plain directory, not a link to one,
    /// because the name is the one thing in this comparison that is supposed to be fixed. A target that
    /// is missing or is itself a link drops the memory root exactly like a mismatched one does.
    /// </para>
    /// <para>
    /// Anything that fails to resolve, or resolves to anywhere else, leaves the memory root out entirely
    /// rather than failing the whole call, which is what leaves every memory read asking when the studio
    /// cannot vouch for where it really lands.
    /// </para>
    /// <para>
    /// Since backlog 338, a THIRD root may join these two: <paramref name="toolResultsRoot"/>, the
    /// current session's own slice of the CLI's saved-tool-output folder. It needs no SECOND, pinned
    /// "target" string the way the memory root does, because there is only one real path here rather
    /// than a link-and-target pair, but it is NOT trusted on the bare strength of resolving: the whole
    /// session config home is writable by the agent's own uid (the CLI itself writes there), so one
    /// later approved command could replace <c>tool-results</c>, or the provider-id folder above it
    /// (which may not exist yet on a fresh session), with a symlink to anywhere. So its CANONICAL
    /// reading (kernel-resolved, every symlink followed) has to equal its LEXICAL one (pure string
    /// arithmetic that never touches the filesystem) exactly: the moment a symlink sits anywhere on the
    /// way down from the config home, the kernel's answer diverges from the literal spelling and the
    /// root is dropped, the same direction every other failure to pin a root here already goes.
    /// </para>
    /// </summary>
    private static Located[] ContainmentRoots(Located root, string? memoryRoot, string? memoryTarget, string? toolResultsRoot = null)
    {
        List<Located> roots = [root];

        if (!string.IsNullOrWhiteSpace(memoryRoot) && !string.IsNullOrWhiteSpace(memoryTarget)
            && Locate(memoryRoot, baseDirectory: null) is { } memory
            && Locate(memoryTarget, baseDirectory: null) is { } expected
            && IsRealDirectory(memoryTarget) && SamePath(memory.Canonical, expected.Canonical))
        {
            roots.Add(memory);
        }

        if (!string.IsNullOrWhiteSpace(toolResultsRoot)
            && Locate(toolResultsRoot, baseDirectory: null) is { } results
            && SamePath(results.Canonical, results.Lexical))
        {
            roots.Add(results);
        }

        return [.. roots];
    }

    /// <summary>
    /// True only when <paramref name="path"/> NAMES a real directory rather than a link to one. Pinning
    /// the memory root to this name is worthless if the name itself can be swapped for a link: remove
    /// the real folder and <c>ln -s /data</c> in its place, and the link's kernel reading and this name's
    /// kernel reading would both land on <c>/data</c> and agree with each other, which is the same
    /// construction <see cref="ContainmentRoots"/>'s own comparison exists to refuse one level up.
    /// </summary>
    private static bool IsRealDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && info.LinkTarget is null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Exact equality between two already-canonical paths, the platform's own notion of "same name".</summary>
    private static bool SamePath(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return a.TrimEnd(Separators).Equals(b.TrimEnd(Separators), comparison);
    }

    /// <summary>
    /// Where a call's path-bearing field really lands, for the human answering the prompt, or null when
    /// the tool named no location at all.
    /// <para>
    /// This decides NOTHING by itself. <see cref="Check"/> is what gates an unattended read, and the broker's
    /// trust branch (ADR 0028) reads the <see cref="AgentToolTarget.Containment"/> this answers, never the
    /// display strings. This exists because asking is not enough on its own: the
    /// one-line subject on the prompt is capped, and a capped path reads as a different path, so the
    /// user was answering about a destination nobody had shown them. Every tool that asks gets its
    /// destination resolved the same way <see cref="Check"/> resolves one, from the RAW value, under
    /// both readings, and the answer travels to the UI.
    /// </para>
    /// </summary>
    public static AgentToolTarget? Resolve(
        string toolName,
        AgentToolInput input,
        string? workspaceRoot,
        string? memoryRoot = null,
        string? memoryTarget = null,
        string? toolResultsRoot = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Without a copy to measure against there is no containment to claim, but the destination is
        // still worth resolving and showing: an absolute path resolves on its own.
        var root = !string.IsNullOrWhiteSpace(workspaceRoot) && Path.IsPathRooted(workspaceRoot)
            ? Locate(workspaceRoot, baseDirectory: null)
            : null;

        // Backlog 202 (and 338's own third root): the SAME roots Check() measures against (pinned the
        // same way, see ContainmentRoots), so a call the broker is about to auto-allow is never shown to
        // a person as merely "inside" without saying which root that was.
        var roots = root is { } located ? ContainmentRoots(located, memoryRoot, memoryTarget, toolResultsRoot) : [];

        var field = AgentToolPaths.FieldsOf(toolName)
            .FirstOrDefault(f => input.KindOf(f) != AgentToolValueKind.Absent);

        AgentToolTarget? placed = null;

        // The directory the tool would work FROM: its path argument when it named one, the copy when
        // the argument is optional and absent, and nothing at all when the studio could not place it.
        var directory = root;

        if (field is not null)
        {
            // Present, but the tool did not send it as a JSON string: there is no path here, and
            // inventing one out of how the value renders is the escape AgentToolInput exists to refuse.
            var raw = input.Text(field);
            if (string.IsNullOrWhiteSpace(raw))
            {
                placed = Unplaceable(field, input[field]);
                directory = null;
            }
            else if (Locate(raw, root) is not { } resolved)
            {
                placed = Unplaceable(field, raw);
                directory = null;
            }
            else
            {
                var containment = roots.Length == 0
                    ? AgentTargetContainment.Undetermined
                    : IsInsideAny(resolved, roots) ? AgentTargetContainment.Inside : AgentTargetContainment.Outside;

                // The other destination is shown whenever it disagrees, and it is exactly the
                // disagreement a symlink inside the copy hides a destination outside it behind. It is
                // the COMPOSED reading rather than the bare string, because the bare string is not
                // where anything lands: on the round-six shape both the as-written answer and the
                // string are in-copy names and only the composition says "secrets". A prompt showing
                // two reassuring paths and an OUTSIDE flag is the reassuring half of a true sentence
                // again, which is how the escape moved to the prompt the last time the machine closed.
                var collapsed = string.Equals(resolved.Composed, resolved.Canonical, StringComparison.Ordinal)
                    ? null
                    : AgentToolSubject.Shorten(resolved.Composed, AgentToolSubject.MaxTargetLength);

                placed = new AgentToolTarget(
                    field,
                    AgentToolSubject.Shorten(raw, AgentToolSubject.MaxTargetLength),
                    AgentToolSubject.Shorten(resolved.Canonical, AgentToolSubject.MaxTargetLength),
                    collapsed,
                    containment);
                directory = resolved;
            }
        }

        // A PATTERN is the other half of where a Glob or a Grep reads, and it was the half the prompt
        // never mentioned: a call whose "path" is an ordinary folder deep inside the copy and whose
        // pattern climbs out of it was shown to the user as landing INSIDE the copy, which is the
        // reassuring half of a true sentence. So when the path is fine and the pattern is not, the
        // pattern is what the user is told about.
        if (placed is null or { Containment: AgentTargetContainment.Inside }
            && FromPattern(toolName, input, directory, roots) is { } fromPattern)
        {
            return fromPattern;
        }

        return placed;
    }

    /// <summary>
    /// What the prompt should say about a tool's PATTERN, or null when the pattern provably stays
    /// inside the copy (or this session's agent memory) and the path argument is the more useful thing
    /// to show.
    /// </summary>
    private static AgentToolTarget? FromPattern(
        string toolName, AgentToolInput input, Located? directory, IReadOnlyList<Located> roots)
    {
        if (AgentToolSafety.TargetOf(toolName)?.PatternField is not { } field)
        {
            return null;
        }

        switch (input.KindOf(field))
        {
            case AgentToolValueKind.Absent:
                return null;

            case AgentToolValueKind.NotText:
                return Unplaceable(field, input[field]);
        }

        var pattern = input[field];
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        if (directory is not { } from || roots.Count == 0)
        {
            return Unplaceable(field, pattern);
        }

        return Place(pattern, from, roots) switch
        {
            PatternPlacement.Inside => null,

            // Outside because a link out of the copy really is reachable through this pattern: the
            // studio walked the region it can expand into and found one. Which name the engine will
            // match is still not claimed, and the prompt says the pattern rather than a destination.
            PatternPlacement.Outside or PatternPlacement.Traversable => new AgentToolTarget(
                field,
                AgentToolSubject.Shorten(pattern, AgentToolSubject.MaxTargetLength),
                Canonical: null,
                Collapsed: null,
                AgentTargetContainment.Outside),
            _ => Unplaceable(field, pattern),
        };
    }

    /// <summary>A field the studio will not claim a destination for, which is never shown as inside.</summary>
    private static AgentToolTarget Unplaceable(string field, string requested) =>
        new(field,
            AgentToolSubject.Shorten(requested, AgentToolSubject.MaxTargetLength),
            Canonical: null,
            Collapsed: null,
            AgentTargetContainment.Undetermined);

    /// <summary>
    /// The prompt a PATH PATTERN earns, or null when every way of reading it stays inside the copy.
    /// <para>
    /// A pattern is not a path with two wildcards in it: braces expand to whole path segments before
    /// anything is matched, a backslash escapes the character after it, and classes and extglob groups
    /// stand for names nobody can know in advance. So the pattern is judged the only honest way: it is
    /// brace-expanded, unescaped, and then only its LITERAL PREFIX (the segments in front of the first
    /// magic one) is walked and contained. That prefix is where a real ".." lives, and it is the
    /// deepest point the engine is guaranteed to start from.
    /// </para>
    /// <para>
    /// Everything from the first magic segment onward is unknowable, so it is not judged, it is
    /// refused whenever it so much as spells "..": those segments are matched against real directory
    /// entries, which never include "." or "..". A pattern too tangled to expand (runaway braces,
    /// absurd length) asks, like everything else the studio cannot pin down.
    /// </para>
    /// <para>
    /// That leaves what a wildcard MATCHES, which is round seven and is not a question about spelling
    /// at all. A real directory entry is never ".." and CAN be a symlink out of the copy, so
    /// <c>*/keys.txt</c> expands onto a link named <c>out</c> and reads the secrets tree with every
    /// judged part of the pattern inside. Predicting which entries the engine will match is the same
    /// bet this whole type refuses, so instead the region the pattern can WALK is established to hold
    /// no link out of the copy (<see cref="LinkWalk"/>): the subtree under the resolved literal prefix
    /// under each of its readings, plus wherever a link inside that subtree leads. That is sufficient
    /// because a magic segment can no longer be followed by any "..", so the only way out of the region
    /// is through a link, and the walk looks at every one of them.
    /// </para>
    /// </summary>
    private static PatternPlacement Place(string pattern, Located directory, IReadOnlyList<Located> roots)
    {
        if (pattern.Length > MaxPatternLength)
        {
            return PatternPlacement.Undetermined;
        }

        // One budget for the whole call, so a pattern with sixty-four brace alternatives cannot buy
        // sixty-four walks, and so a subtree two alternatives share is walked once.
        var walk = new LinkWalk(roots);

        foreach (var reading in Readings(pattern))
        {
            var alternatives = new List<string>();
            if (!TryExpandBraces(reading, alternatives, depth: 0))
            {
                return PatternPlacement.Undetermined;
            }

            foreach (var alternative in alternatives)
            {
                if (LiteralPrefix(Unescape(alternative), out var wildcards) is not { } prefix)
                {
                    return PatternPlacement.Undetermined;
                }

                if (Locate(prefix, directory) is not { } resolved)
                {
                    return PatternPlacement.Undetermined;
                }

                if (!IsInsideAny(resolved, roots))
                {
                    return PatternPlacement.Outside;
                }

                if (wildcards)
                {
                    var reachable = walk.Establish(resolved);
                    if (reachable != PatternPlacement.Inside)
                    {
                        return reachable;
                    }
                }
            }
        }

        return PatternPlacement.Inside;
    }

    /// <summary>Where a pattern reads, as far as anyone can tell in advance.</summary>
    private enum PatternPlacement
    {
        /// <summary>Every reading of it stays inside the copy.</summary>
        Inside,

        /// <summary>The part of it that is a path at all leaves the copy under some reading.</summary>
        Outside,

        /// <summary>
        /// Its wildcards can walk through a link that leaves the copy. Nothing here claims the engine
        /// WILL match that link; the studio simply does not get to bet that it will not.
        /// </summary>
        Traversable,

        /// <summary>Too tangled to place, which is treated exactly like leaving.</summary>
        Undetermined,
    }

    /// <summary>
    /// Establishes, by WALKING it, that everything a pattern's wildcards can reach from a directory
    /// stays inside the copy. Nothing here predicts a match: every entry of the region is looked at,
    /// and a single link out of it is enough to end in a prompt.
    /// <para>
    /// It walks the subtree under the starting directory and follows a link that stays inside the copy
    /// to wherever it leads, because a glob traverses through such a link into a subtree the region did
    /// not otherwise contain. A directory is walked once (its kernel name is the key), so a cycle of
    /// links costs one pass rather than spinning.
    /// </para>
    /// <para>
    /// What it does NOT cover, said rather than implied. It is a measurement of the copy AS IT IS NOW:
    /// a link planted between this walk and the engine's expansion is not seen by it, which is a race
    /// the approval boundary already lives with (a link can only be planted by a change, and every
    /// change asks). It looks at symlinks, the only entry that redirects a walk to another place;
    /// a bind mount or a hard-linked file inside the copy is not one and is not detected. And it is
    /// bounded by <see cref="MaxWalkEntries"/>: a region larger than the budget is not established, so
    /// it asks.
    /// </para>
    /// </summary>
    private sealed class LinkWalk(IReadOnlyList<Located> roots)
    {
        private readonly HashSet<string> _visited = new(StringComparer.Ordinal);

        private int _budget = MaxWalkEntries;

        public PatternPlacement Establish(Located from)
        {
            var pending = new Stack<string>();

            // Every reading of the starting directory, because they can be different directories and
            // the engine's own arithmetic decides which one it expands from.
            foreach (var start in new[] { from.Lexical, from.Canonical, from.Composed })
            {
                if (Canonical(start) is not { } real)
                {
                    return PatternPlacement.Undetermined;
                }

                if (_visited.Add(real))
                {
                    pending.Push(real);
                }
            }

            while (pending.Count > 0)
            {
                if (Walk(pending, pending.Pop()) is { } refusal)
                {
                    return refusal;
                }
            }

            return PatternPlacement.Inside;
        }

        /// <summary>
        /// One directory of the region, or the answer that ends the walk. Entries that are links are
        /// judged and never descended blindly; entries that are directories are queued.
        /// </summary>
        private PatternPlacement? Walk(Stack<string> pending, string directory)
        {
            // Not a directory at all, which is a name a pattern matches nothing under: a file (Grep
            // takes one as its path), or a name that is simply not there. Neither holds an entry to
            // traverse through, and neither may become a prompt.
            if (!Directory.Exists(directory))
            {
                return null;
            }

            try
            {
                foreach (var (path, link, folder) in Entries(directory))
                {
                    if (--_budget < 0)
                    {
                        return PatternPlacement.Undetermined;
                    }

                    if (!link)
                    {
                        if (folder && _visited.Add(path))
                        {
                            pending.Push(path);
                        }

                        continue;
                    }

                    // A link is judged exactly as a path argument is, under every reading, so a
                    // dangling one and a chain of them answer here the same way they answer there.
                    if (Locate(path, baseDirectory: null) is not { } target)
                    {
                        return PatternPlacement.Undetermined;
                    }

                    if (!IsInsideAny(target, roots))
                    {
                        return PatternPlacement.Traversable;
                    }

                    // It stays inside, and a glob walks THROUGH it into a subtree this region does
                    // not otherwise hold, so that subtree is part of the region as well.
                    if (_visited.Add(target.Canonical) && Directory.Exists(target.Canonical))
                    {
                        pending.Push(target.Canonical);
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Nothing is there, so nothing can be traversed through it. A pattern under a name
                // that does not exist matches nothing, and it must not become a prompt.
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return PatternPlacement.Undetermined;
            }

            return null;
        }

        /// <summary>
        /// One directory's entries, with the two things the walk needs about each: whether it is a
        /// LINK (the only entry that sends a walk somewhere else) and whether it is a directory, both
        /// taken from the enumeration's own answer rather than asked again per entry.
        /// <para>
        /// Nothing is skipped and nothing inaccessible is ignored: an entry the studio cannot look at
        /// throws, and the walk answers Undetermined, which is a prompt. That is the whole reason the
        /// options are spelled out instead of defaulted, since the default is to skip quietly.
        /// </para>
        /// </summary>
        private static FileSystemEnumerable<(string Path, bool Link, bool Directory)> Entries(string directory) =>
            new(directory,
                static (ref FileSystemEntry entry) =>
                    (entry.ToFullPath(), (entry.Attributes & FileAttributes.ReparsePoint) != 0, entry.IsDirectory),
                new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    AttributesToSkip = 0,
                    ReturnSpecialDirectories = false,
                });
    }

    /// <summary>
    /// The ways a glob engine may read this pattern's backslashes, all of which have to be contained.
    /// On a POSIX engine a backslash escapes the next character (so <c>.\.</c> is the name <c>..</c>);
    /// with Windows path semantics it is a separator instead. The studio does not get to pick.
    /// </summary>
    private static IEnumerable<string> Readings(string pattern)
    {
        yield return pattern;

        if (pattern.Contains('\\', StringComparison.Ordinal))
        {
            yield return pattern.Replace('\\', '/');
        }
    }

    /// <summary>
    /// Expands the pattern's brace lists into the alternatives the engine will actually match, the way
    /// minimatch does: before anything else, honouring escapes and nesting, leaving an unbalanced or
    /// comma-less brace as literal text. False means it ran away (too many alternatives, nested too
    /// deep), which ends in a prompt.
    /// </summary>
    private static bool TryExpandBraces(string value, List<string> alternatives, int depth)
    {
        if (depth > MaxBraceDepth)
        {
            return false;
        }

        var open = -1;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                i++;
                continue;
            }

            if (value[i] == '{')
            {
                open = i;
                break;
            }
        }

        if (open < 0)
        {
            return Keep(value, alternatives);
        }

        var parts = new List<string>();
        var start = open + 1;
        var close = -1;
        var level = 0;
        for (var i = start; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\')
            {
                i++;
            }
            else if (c == '{')
            {
                level++;
            }
            else if (c == '}')
            {
                if (level == 0)
                {
                    parts.Add(value[start..i]);
                    close = i;
                    break;
                }

                level--;
            }
            else if (c == ',' && level == 0)
            {
                parts.Add(value[start..i]);
                start = i + 1;
            }
        }

        // No closing brace, or nothing to choose between: the engine keeps the text as it stands. The
        // "{" is still a magic character, so the segment is never walked as a name either way.
        if (close < 0 || parts.Count < 2)
        {
            return Keep(value, alternatives);
        }

        var prefix = value[..open];
        var suffix = value[(close + 1)..];
        foreach (var part in parts)
        {
            if (!TryExpandBraces(prefix + part + suffix, alternatives, depth + 1))
            {
                return false;
            }
        }

        return true;

        static bool Keep(string value, List<string> alternatives)
        {
            if (alternatives.Count >= MaxPatternAlternatives)
            {
                return false;
            }

            alternatives.Add(value);
            return true;
        }
    }

    /// <summary>Drops one level of backslash escapes, the way the engine does before it matches.</summary>
    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The part of an expanded pattern that is still a plain path: every segment in front of the first
    /// one holding a magic character. Null means the studio will not judge it at all, because a segment
    /// it cannot know the value of spells out a "..".
    /// <para>
    /// <paramref name="wildcards"/> reports whether there was such a segment at all, which is what
    /// decides whether the region past the prefix has to be established rather than reasoned about: a
    /// pattern of plain names reaches exactly what it spells, and one holding a wildcard reaches
    /// whatever the directory happens to contain, links included.
    /// </para>
    /// </summary>
    private static string? LiteralPrefix(string pattern, out bool wildcards)
    {
        var rooted = pattern.Length > 0 && Array.IndexOf(Separators, pattern[0]) >= 0;
        var literal = new List<string>();
        var magicSeen = false;

        foreach (var segment in pattern.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!magicSeen && segment.IndexOfAny(Magic) < 0)
            {
                literal.Add(segment);
                continue;
            }

            magicSeen = true;
            if (Spells(segment, ".."))
            {
                wildcards = true;
                return null;
            }
        }

        wildcards = magicSeen;

        var joined = string.Join('/', literal);
        return rooted ? "/" + joined : joined;
    }

    /// <summary>True when a magic segment's own text, once its glob punctuation is dropped, reads as
    /// <paramref name="text"/>: "[.][.]" and "@(..|zz)" are both a "..", written with decoration.</summary>
    private static bool Spells(string segment, string text)
    {
        var builder = new StringBuilder(segment.Length);
        foreach (var c in segment)
        {
            if (Array.IndexOf(MagicPunctuation, c) < 0)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Contains(text, StringComparison.Ordinal);
    }

    private static string NotAPath(string toolName, string field) =>
        $"{Name(toolName)} sent \"{field}\" as something other than text, so the studio has no path to "
        + "check and is asking rather than guessing at one.";

    private static string Undetermined(string toolName, string value) =>
        $"The studio could not work out where {Name(toolName)} would read ({Describe(value)}), so it is asking "
        + "rather than allowing it automatically.";

    private static string Traversable(string toolName, string value) =>
        $"{Name(toolName)} wants {Describe(value)}, and a wildcard in it can match a link that leaves "
        + "this workspace's contained copy. The studio does not get to guess which names it will match, "
        + "so it is asking rather than allowing it automatically.";

    private static string Outside(string toolName, string value) =>
        $"{Name(toolName)} wants {Describe(value)}, which is OUTSIDE this workspace's contained copy. Reads "
        + "inside the copy run unattended; this one is asking because it leaves it.";

    private static string Name(string? toolName) =>
        string.IsNullOrWhiteSpace(toolName) ? "This tool" : toolName;

    private static string Describe(string value) => AgentToolSubject.Trim(value, 120);

    /// <summary>
    /// The first of <paramref name="fields"/> that is present as a JSON STRING and says something,
    /// handed back through <paramref name="value"/>; null there means none of them named anything.
    /// <para>
    /// The return value is the refusal: the name of a field the tool DID send, but sent as something
    /// that is not a string. That is not a path and the studio does not get to invent one out of how
    /// the value renders, so it stops here rather than walking it. It is checked in the caller's field
    /// order, so the field the tool would actually have used is the one that refuses.
    /// </para>
    /// </summary>
    private static string? FirstText(AgentToolInput input, IReadOnlyList<string> fields, out string? value)
    {
        value = null;
        foreach (var field in fields)
        {
            switch (input.KindOf(field))
            {
                case AgentToolValueKind.NotText:
                    return field;

                case AgentToolValueKind.Text when !string.IsNullOrWhiteSpace(input[field]):
                    value = input[field];
                    return null;

                default:
                    continue;
            }
        }

        return null;
    }

    /// <summary>
    /// One value, resolved every one of the ways it is going to be resolved for real, so containment
    /// can be required of each. None of them is a prediction of another: <see cref="Lexical"/> is the
    /// string arithmetic the calling program does before it opens anything, <see cref="Canonical"/> is
    /// what the OS answers when it is asked about the path AS WRITTEN, and <see cref="Composed"/> is
    /// the two of them in the order a caller really performs them.
    /// </summary>
    private readonly record struct Located(string Lexical, string Canonical, string Composed);

    /// <summary>
    /// Where <paramref name="value"/> lands under every reading, relative values resolving against
    /// <paramref name="baseDirectory"/>. Null means the studio could not tell, which always ends in a
    /// prompt.
    /// </summary>
    private static Located? Locate(string value, Located? baseDirectory)
    {
        // The lexical answer, measured from the base spelled the way the caller spelled it: this is
        // what a glob walker's ".." arithmetic and a CLI's own path.resolve land on.
        if (Lexical(value, baseDirectory?.Lexical) is not { } lexical)
        {
            return null;
        }

        // The kernel's answer, measured from the directory the child is REALLY standing in, and asked
        // about the path AS WRITTEN. Nothing normalises it on the way there, which is the whole point:
        // Path.GetFullPath collapses "link/.." into the link's PARENT before the kernel is ever asked,
        // so a value like "out/../secrets/keys.txt" (with "out" a link that leaves the copy) reached
        // realpath already rewritten into an in-copy path and came back contained. The kernel resolves
        // a link and THEN climbs from where it landed; string arithmetic climbs from where the link was
        // written. Handing the kernel the arithmetic's answer is the lexical reading wearing the
        // kernel's name, which is the disagreement this type exists to refuse.
        if (Join(value, baseDirectory?.Canonical) is not { } asWritten
            || Canonical(asWritten) is not { } canonical)
        {
            return null;
        }

        // The reading a caller actually performs, which is neither of the two above but their
        // COMPOSITION: collapse ".." as text first, then let the kernel open what that spells. Round
        // SIX was exactly the gap between "each of two readings is inside" and "the reading that runs
        // is inside": with "nested" a link deeper INSIDE the copy and "out" a link leaving it,
        // "nested/../out/keys.txt" is inside as written (the kernel climbs from where "nested" landed,
        // to a name that is not there) and inside lexically ("<copy>/out/keys.txt"), while the string
        // arithmetic's answer handed back to the kernel opens the real secret outside the copy.
        // Measured in the studio's container on 2026-08-06 through three separate openers (.NET, node
        // and python all resolve then open). So the composition is asked about in its own right, which
        // costs one more realpath on a string this method has already worked out.
        if (Canonical(lexical) is not { } composed)
        {
            return null;
        }

        return new Located(lexical, canonical, composed);
    }

    /// <summary>
    /// An absolute path naming exactly what <paramref name="value"/> named, relative to
    /// <paramref name="baseDirectory"/>. It CONCATENATES and nothing else: no "..", no ".", no doubled
    /// separator and no trailing separator is touched, because every one of those is the kernel's to
    /// interpret and the interpretation is what is being measured. Null when there is no base to
    /// measure a relative value from, which ends in a prompt like every other unknown here.
    /// </summary>
    private static string? Join(string value, string? baseDirectory)
    {
        try
        {
            if (Path.IsPathRooted(value))
            {
                return value;
            }

            return baseDirectory is null ? null : Path.Combine(baseDirectory, value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The platform's own string arithmetic over a path: no symlink followed, no filesystem consulted.
    /// It is not the truth about where a read lands, and it is not used as if it were: it is one of the
    /// two readings that both have to stay inside the copy.
    /// </summary>
    private static string? Lexical(string value, string? baseDirectory)
    {
        try
        {
            var combined = Path.IsPathRooted(value) || baseDirectory is null
                ? value
                : Path.Combine(baseDirectory, value);
            return Path.GetFullPath(combined);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the OPERATING SYSTEM says this absolute path really is (<see cref="KernelPath"/>). The whole
    /// path is offered to it first, which is one syscall and the kernel's own resolution of every
    /// symlink, <c>..</c> and separator in it. Only when part of the path does not exist is it walked,
    /// component by component, so the longest EXISTING prefix stays canonical and just the absent tail
    /// is literal: a leaf nobody has created yet must not be a way to dodge the check.
    /// <para>
    /// Null on anything the studio cannot pin down, which always ends in a prompt: a symlink loop, a
    /// component it may not read, a name the OS rejects, or a path too long to walk in bounded time.
    /// </para>
    /// </summary>
    private static string? Canonical(string path)
    {
        // "." and a doubled separator mean the directory itself on every filesystem there is, whatever
        // any component of the path turns out to be, so dropping them is an identity rather than a
        // prediction. ".." is left exactly where it was written: it is the one component whose meaning
        // depends on what the component before it resolved to, and guessing that is the whole defect.
        // This is also what keeps an honest path padded with thousands of "./" answerable at all: the
        // kernel refuses a path past PATH_MAX, and refusing means asking.
        var fullPath = Compact(path);

        switch (KernelPath.Canonicalise(fullPath, out var whole))
        {
            case KernelPathOutcome.Resolved:
                return whole;

            case KernelPathOutcome.Refused:
                return null;
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        var pending = new Stack<string>();
        Push(pending, fullPath[root.Length..]);

        var current = KernelPath.Canonicalise(root, out var realRoot) == KernelPathOutcome.Resolved
            ? realRoot!
            : root;
        var follows = 0;
        var steps = 0;

        while (pending.Count > 0)
        {
            if (++steps > MaxPathSteps)
            {
                return null;
            }

            var segment = pending.Pop();
            if (segment is ".")
            {
                continue;
            }

            if (segment is "..")
            {
                // `current` is already canonical, so its parent is the one the kernel would climb to.
                current = Path.GetDirectoryName(current) is { Length: > 0 } parent ? parent : root;
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.Combine(current, segment);
            }
            catch (ArgumentException)
            {
                return null;
            }

            switch (KernelPath.Canonicalise(candidate, out var resolved))
            {
                case KernelPathOutcome.Resolved:
                    // The OS has followed whatever this was, including a chain of links.
                    current = resolved!;
                    continue;

                case KernelPathOutcome.Refused:
                    return null;
            }

            // Nothing is there by that name. That is usually the absent tail of a path being written
            // or globbed for, but it is also what a DANGLING symlink looks like to the OS, and a
            // dangling link still says where a read through it was aimed. So ask what it points at
            // before accepting the name as literal.
            if (!TryReadLink(candidate, out var target))
            {
                return null;
            }

            if (target is null)
            {
                current = candidate;
                continue;
            }

            if (++follows > MaxLinkFollows)
            {
                return null;
            }

            if (Path.IsPathRooted(target))
            {
                var targetRoot = Path.GetPathRoot(target);
                if (string.IsNullOrEmpty(targetRoot))
                {
                    return null;
                }

                current = targetRoot;
                Push(pending, target[targetRoot.Length..]);
            }
            else
            {
                // A relative link resolves against the directory holding the link, which is `current`.
                Push(pending, target);
            }
        }

        return current;
    }

    /// <summary>
    /// The same path with its no-op components ("." and empty ones) removed and NOTHING else changed.
    /// Every other component, ".." above all, is left where the caller wrote it.
    /// </summary>
    private static string Compact(string fullPath)
    {
        if (Path.GetPathRoot(fullPath) is not { Length: > 0 } root)
        {
            return fullPath;
        }

        var segments = fullPath[root.Length..]
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();

        return segments.Length == 0 ? root : root.TrimEnd(Separators) + Path.DirectorySeparatorChar
            + string.Join(Path.DirectorySeparatorChar, segments);
    }

    /// <summary>Pushes a path's components so the leftmost one pops first.</summary>
    private static void Push(Stack<string> pending, string path)
    {
        var segments = path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            pending.Push(segments[i]);
        }
    }

    /// <summary>
    /// The link target of one path component the OS reported as not there, which is the single case
    /// canonicalisation cannot answer: a DANGLING symlink resolves to nothing while still saying where
    /// a read through it was aimed. False means the studio could not look, which is treated as "cannot
    /// tell" rather than "not a link": guessing there would be the hole this whole type exists to close.
    /// </summary>
    private static bool TryReadLink(string path, out string? target)
    {
        target = null;
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            target = info.LinkTarget;
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Nothing is there, so nothing can be a link: the lexical path stands. A read of it fails
            // on its own merits, and the containment comparison is still made against the real root.
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// True only when the path is inside the copy under EVERY reading: where the OS would really open
    /// it as written, where the calling program's own string arithmetic puts it, and where that
    /// arithmetic's answer lands once the OS follows it. Any one of them landing outside is an escape,
    /// because any one of them can be the resolution that runs.
    /// <para>
    /// The third is not implied by the first two, and believing it was is round SIX of this. "A is
    /// inside" and "B is inside" says nothing about where kernel(A) lands, because a lexically
    /// collapsed string can name a symlink that the kernel walking the path AS WRITTEN never reaches.
    /// It is measured against the root's KERNEL reading, because it is itself a kernel answer and
    /// <see cref="Located.Canonical"/> is the kernel's name for the copy.
    /// </para>
    /// </summary>
    private static bool IsInside(Located candidate, Located root) =>
        IsInside(candidate.Lexical, root.Lexical)
        && IsInside(candidate.Canonical, root.Canonical)
        && IsInside(candidate.Composed, root.Canonical);

    /// <summary>
    /// True when a candidate is inside ANY one of several roots (backlog 202: the workspace copy and,
    /// when this session has one, its own agent-memory folder). Each root is still required to hold
    /// EVERY one of the candidate's readings on its own; a lexical reading landing in one root and a
    /// kernel reading landing in another is not good enough, for the exact round-six reason
    /// <see cref="IsInside(Located,Located)"/> is itself structured this way.
    /// </summary>
    private static bool IsInsideAny(Located candidate, IReadOnlyList<Located> roots)
    {
        foreach (var root in roots)
        {
            if (IsInside(candidate, root))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when a resolved path is the copy itself or something beneath it.</summary>
    private static bool IsInside(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var trimmed = root.TrimEnd(Separators);
        if (trimmed.Length == 0)
        {
            trimmed = root;
        }

        return candidate.Equals(trimmed, comparison)
            || candidate.StartsWith(trimmed + Path.DirectorySeparatorChar, comparison)
            || candidate.StartsWith(trimmed + Path.AltDirectorySeparatorChar, comparison);
    }
}
