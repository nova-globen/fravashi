using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Tests.Sessions.Containment;

/// <summary>How a case names the thing it would open, which is what the oracle has to ask about.</summary>
public enum ContainmentShape
{
    /// <summary>A plain path argument, opened as spelled.</summary>
    Path,

    /// <summary>A glob pattern, expanded by a real glob engine before anything is opened.</summary>
    Pattern,

    /// <summary>
    /// A path-bearing field the CLI did not send as a JSON string. There is no path here for any
    /// operating system to resolve, so the only honest verdict is that the studio must not claim one.
    /// </summary>
    NotAString,
}

/// <summary>One adversarial tool call, with the tags saying which construction it is built from.</summary>
public sealed record ContainmentCase(
    int Index,
    string Tool,
    ContainmentShape Shape,
    string? PathField,
    string? PathValue,
    bool PathIsText,
    string? PatternField,
    string? PatternValue,
    bool PatternIsText,
    IReadOnlyList<string> Tags)
{
    /// <summary>The call as the approval path really sees it, JSON kinds and all.</summary>
    public AgentToolInput ToInput()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var text = new List<string>();

        if (PathField is not null && PathValue is not null)
        {
            values[PathField] = PathValue;
            if (PathIsText)
            {
                text.Add(PathField);
            }
        }

        if (PatternField is not null && PatternValue is not null)
        {
            values[PatternField] = PatternValue;
            if (PatternIsText)
            {
                text.Add(PatternField);
            }
        }

        return AgentToolInput.Create(values, text);
    }

    public string Describe() =>
        $"#{Index} {Tool}[{Shape}] {PathField}={Quote(PathValue, PathIsText)}"
        + (PatternField is null ? string.Empty : $" {PatternField}={Quote(PatternValue, PatternIsText)}")
        + $" ({string.Join(", ", Tags)})";

    private static string Quote(string? value, bool isText) =>
        value is null ? "<absent>" : isText ? $"\"{value}\"" : $"<not a string: {value}>";
}

/// <summary>
/// The adversarial corpus, GENERATED rather than hand-listed.
/// <para>
/// Every escape this boundary has suffered was a construction nobody had thought to write down, so the
/// cases are built as a cross product of the pieces those constructions were made of: how a value
/// STARTS (bare, padded with "./" past any display cap, through a symlink that goes deeper in, through
/// one that goes out, from an absolute path, from a prefix neighbour, through a name full of
/// backslashes), how far it CLIMBS (nothing up to more levels than the copy is deep), and where it
/// ENDS (a secret, an innocent file, a name that does not exist, a name whose parent does not exist).
/// Patterns get the same treatment with each ".." spelled the several ways glob syntax allows: a brace
/// list, a character class, an extglob group, a backslash-escaped dot.
/// </para>
/// <para>
/// Nothing here decides anything. A case is a question; <see cref="PlatformOracle"/> answers it by
/// asking the operating system, and the differential test compares that answer with the studio's.
/// </para>
/// </summary>
public static class ContainmentCorpus
{
    /// <summary>Tools that take a path, with the field each spells it in.</summary>
    private static readonly (string Tool, string Field)[] PathTools =
    [
        ("Read", "file_path"),
        ("NotebookRead", "notebook_path"),
        ("LS", "path"),
        ("Glob", "path"),
        ("Grep", "path"),
    ];

    /// <summary>
    /// How many "./" a value is padded with. 120 of them is 240 characters, comfortably past the
    /// 200-character cap the approval subject is shortened to, which is where round 2 lived.
    /// </summary>
    private static readonly int[] Paddings = [1, 5, 40, 120];

    /// <summary>
    /// Climb depths, including several past the depth of the copy itself, where the extra ".." are
    /// absorbed by the filesystem root instead of walking off it.
    /// <para>
    /// EVERY depth from nothing to past the copy's own depth is here, and the gap that used to be in
    /// this list is why: five is exactly how far <c>src/deep/a/b/c/</c> has to climb to land back on
    /// the copy root, which is the one place an escaping link is waiting. A root the corpus generates
    /// and a tail the corpus generates were never crossed at the only depth that joins them, so the
    /// whole family was missing from a corpus that looked like a full cross product.
    /// </para>
    /// </summary>
    private static readonly int[] ClimbDepths = [0, 1, 2, 3, 4, 5, 6, 9];

    private static readonly string[] Tails =
    [
        "secrets/keys.txt",
        "secrets",
        "secrets/",
        "secrets/ring/dp.xml",
        "src/main.txt",
        "w10/keys.txt",
        "newfile-that-is-not-there.txt",
        "nope/deeper/x.txt",
    ];

    /// <summary>
    /// Tails that reach their destination THROUGH a link that leaves the copy, rather than by naming
    /// what is outside it. Crossed with a root that is itself a link going deeper IN, this is round 6:
    /// the ".." is measured from where the first link landed under one reading and from where it was
    /// written under the other, so each reading stays inside on its own while the composition a real
    /// opener performs (collapse the text, then open) lands on the escaping link.
    /// </summary>
    private static readonly string[] EscapingLinkTails =
    [
        "out/keys.txt",
        "out/ring/dp.xml",
        "neighbour/keys.txt",
    ];

    /// <summary>
    /// The ways glob syntax spells one ".." segment. The first is the plain one; the rest are the
    /// spellings round 3 got through with, when only "*" and "?" were being neutralised.
    /// </summary>
    private static readonly (string Spelling, string Tag)[] ClimbSpellings =
    [
        ("..", "plain-climb"),
        ("{..,zz}", "brace"),
        ("{.{.,},zz}", "nested-brace"),
        ("[.][.]", "character-class"),
        ("@(..|zz)", "extglob"),
        (".\\.", "escaped-dot"),
        ("..\\", "trailing-escape"),
    ];

    /// <summary>
    /// Ways of writing a segment that MATCHES a name rather than spelling one, which is the whole of
    /// round 7: a wildcard is matched against real directory entries, and one of those entries can be a
    /// symlink out of the copy. None of these spells "..", none of them is a path the studio can read,
    /// and each of them names the escaping link the moment the copy holds one.
    /// </summary>
    private static readonly (string Spelling, string Tag)[] WildcardSpellings =
    [
        ("*", "star"),
        ("**", "globstar"),
        ("?ut", "question-mark"),
        ("ou?", "question-mark"),
        ("[on]ut", "character-class"),
        ("[!x]ut", "negated-class"),
        ("@(out|zz)", "extglob"),
        ("{*,zz}", "brace"),
    ];

    /// <summary>
    /// What follows the wildcard. A LITERAL tail is what makes the difference: a bare wildcard only
    /// NAMES the link, which leaks nothing, while a literal segment after it makes the engine traverse
    /// THROUGH the link and enumerate what is on the other side.
    /// </summary>
    private static readonly string[] WildcardTails =
    [
        "keys.txt",
        "ring/dp.xml",
        "k.txt",
    ];

    private static readonly string[] FixedPatterns =
    [
        "*",
        "*.txt",
        "**/*",
        "src/**/keys.txt",
        "../*/keys.txt",
        "../secrets/{keys,k}.txt",
        "{,../}secrets/*",
        "**/../../secrets/*",
        "/etc/host*",
        "/proc/1/*",
    ];

    /// <summary>
    /// Renderings of a path-bearing field the CLI sent as something other than a JSON string. The first
    /// is verbatim the one that manufactured an eight-deep base for a pattern's eight ".." to walk back
    /// into the copy from.
    /// </summary>
    private static readonly string[] NonStringRenderings =
    [
        "{\"a/b/c/d/e/f/g/h\":1}",
        "[\"/etc/passwd\"]",
        "5",
        "true",
        "null",
        "{}",
        "[]",
    ];

    public static IReadOnlyList<ContainmentCase> Generate(ContainmentFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var cases = new List<ContainmentCase>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tool = 0;

        void AddPath(string value, params string[] tags)
        {
            if (!seen.Add("P " + value))
            {
                return;
            }

            var (name, field) = PathTools[tool++ % PathTools.Length];
            cases.Add(new ContainmentCase(
                cases.Count, name, ContainmentShape.Path, field, value, PathIsText: true,
                PatternField: null, PatternValue: null, PatternIsText: false, tags));
        }

        string[] tails = fixture.HasSymlinks ? [.. Tails, .. EscapingLinkTails] : Tails;

        foreach (var (root, rootTag) in Roots(fixture))
        {
            foreach (var depth in ClimbDepths)
            {
                var climb = string.Concat(Enumerable.Repeat("../", depth));
                foreach (var tail in tails)
                {
                    var value = root + climb + tail;
                    AddPath(value, rootTag, "climb-" + depth, TailTag(tail));

                    // The same value with every separator written as a backslash. On Linux that is ONE
                    // filename, on Windows it is the same path: the studio has to be right on the
                    // platform it is running on, not on the one it was written against. The tails that
                    // reach THROUGH an escaping link get it too: they used not to, so the one family
                    // built out of two disagreeing readings was never crossed with the one construction
                    // built out of disagreeing separators.
                    if (depth > 0
                        && (tail.StartsWith("secrets", StringComparison.Ordinal)
                            || EscapingLinkTails.Contains(tail, StringComparer.Ordinal)))
                    {
                        AddPath(
                            value.Replace('/', '\\'), rootTag, "climb-" + depth, TailTag(tail), "backslash-value");
                    }
                }
            }
        }

        foreach (var (value, tag) in AbsoluteValues(fixture))
        {
            AddPath(value, tag, "absolute");
        }

        if (fixture.HasSymlinks)
        {
            // Backlog 202: this session's own agent memory, reached through the session-side link.
            // Plain reads and a leaf that does not exist yet have to be judged alongside the escapes
            // that sit right beside the link: another workspace's key, a link inside memory pointing
            // out of it, and a ".." chain that climbs from the link into the session directory.
            AddPath(
                Path.Combine(fixture.MemoryLink, "proof-marker.md"), "memory-inside", "memory");
            AddPath(
                Path.Combine(fixture.MemoryLink, "notes", "other-note.md"), "memory-inside", "memory");
            AddPath(
                Path.Combine(fixture.MemoryLink, "brand-new-note.md"), "memory-missing-leaf", "memory");
            AddPath(
                Path.Combine(fixture.MemoryLink, "escape", "keys.txt"), "memory-link-out", "memory");
            AddPath(
                Path.Combine(fixture.OtherMemoryShared, "proof-marker.md"), "memory-other-key", "memory");

            foreach (var suffix in new[] { "../../../settings.json", "../../../approve.sh", "../abcd1234.jsonl" })
            {
                AddPath(Path.Combine(fixture.MemoryLink, suffix), "memory-session-climb", "memory");
            }
        }

        // Backlog 338: this session's own saved tool output, a REAL folder (never a link, unlike the
        // memory root above) beside the approval hook's settings and script. Plain reads and a leaf
        // that does not exist yet have to be judged alongside the escapes that sit right beside it:
        // another session's own tool-results, and a ".." chain that climbs from it into the session
        // directory. Unlike memory this needs no symlink support to exist at all.
        AddPath(Path.Combine(fixture.ToolResults, "proof-marker.txt"), "toolresults-inside", "toolresults");
        AddPath(Path.Combine(fixture.ToolResults, "logs", "other-result.txt"), "toolresults-inside", "toolresults");
        AddPath(Path.Combine(fixture.ToolResults, "brand-new-result.txt"), "toolresults-missing-leaf", "toolresults");
        AddPath(
            Path.Combine(fixture.OtherSessionToolResults, "proof-marker.txt"), "toolresults-other-session", "toolresults");

        foreach (var suffix in new[] { "../../../../settings.json", "../../../../approve.sh", "../../abcd1234.jsonl" })
        {
            AddPath(Path.Combine(fixture.ToolResults, suffix), "toolresults-session-climb", "toolresults");
        }

        if (fixture.HasSymlinks)
        {
            // A link INSIDE tool-results that points OUT of it, the same shape memory's own "escape" is.
            AddPath(Path.Combine(fixture.ToolResults, "escape", "keys.txt"), "toolresults-link-out", "toolresults");
        }

        AddPatterns(fixture, cases);
        AddMemoryPatternCases(fixture, cases);
        AddToolResultsPatternCases(fixture, cases);
        AddNonStrings(cases);

        return cases;
    }

    /// <summary>
    /// Backlog 202, the pattern half: a wildcard that stays inside a clean part of memory, one rooted at
    /// the memory folder itself (which also holds a link out of it, so round 7 says this must ask too),
    /// and one whose literal prefix climbs off the memory link into the session directory.
    /// </summary>
    private static void AddMemoryPatternCases(ContainmentFixture fixture, List<ContainmentCase> cases)
    {
        if (!fixture.HasSymlinks)
        {
            return;
        }

        (string? Path, string PathTag, string Pattern, string PatternTag)[] bodies =
        [
            (Path.Combine(fixture.MemoryLink, "notes"), "memory-inside", "*", "memory"),
            (fixture.MemoryLink, "memory-link-out", "*", "memory"),
            (fixture.MemoryLink, "memory-session-climb", "../../../*.json", "memory"),
            (null, "no-path-field", fixture.MemoryLink + "/notes/*", "memory"),
        ];

        var tool = 0;
        foreach (var (path, pathTag, pattern, patternTag) in bodies)
        {
            var (name, field) = tool++ % 4 == 3 ? ("Grep", "glob") : ("Glob", "pattern");
            cases.Add(new ContainmentCase(
                cases.Count, name, ContainmentShape.Pattern,
                PathField: path is null ? null : "path", PathValue: path, PathIsText: true,
                PatternField: field, PatternValue: pattern, PatternIsText: true,
                Tags: [pathTag, patternTag, "pattern"]));
        }
    }

    /// <summary>
    /// Backlog 338, the pattern half: a wildcard that stays inside a clean part of this session's own
    /// tool-results (the "logs" subfolder, a sibling of the escaping link), one rooted at tool-results
    /// itself (which also holds that link, so round 7 says this must ask too), and one whose literal
    /// prefix climbs off tool-results into the session directory.
    /// </summary>
    private static void AddToolResultsPatternCases(ContainmentFixture fixture, List<ContainmentCase> cases)
    {
        if (!fixture.HasSymlinks)
        {
            return;
        }

        (string? Path, string PathTag, string Pattern, string PatternTag)[] bodies =
        [
            (Path.Combine(fixture.ToolResults, "logs"), "toolresults-inside", "*", "toolresults"),
            (fixture.ToolResults, "toolresults-link-out", "*", "toolresults"),
            (fixture.ToolResults, "toolresults-session-climb", "../../../../*.json", "toolresults"),
            (null, "no-path-field", fixture.ToolResults + "/logs/*", "toolresults"),
        ];

        var tool = 0;
        foreach (var (path, pathTag, pattern, patternTag) in bodies)
        {
            var (name, field) = tool++ % 4 == 3 ? ("Grep", "glob") : ("Glob", "pattern");
            cases.Add(new ContainmentCase(
                cases.Count, name, ContainmentShape.Pattern,
                PathField: path is null ? null : "path", PathValue: path, PathIsText: true,
                PatternField: field, PatternValue: pattern, PatternIsText: true,
                Tags: [pathTag, patternTag, "pattern"]));
        }
    }

    /// <summary>How a generated value starts, which is what decides the base its ".." are measured from.</summary>
    private static IEnumerable<(string Root, string Tag)> Roots(ContainmentFixture fixture)
    {
        yield return (string.Empty, "bare");

        foreach (var pad in Paddings)
        {
            yield return (string.Concat(Enumerable.Repeat("./", pad)), "padding-" + pad);
        }

        yield return ("src/deep/a/b/c/", "deep-real-directory");
        yield return ("dots/pad/pad2/", "deep-real-directory");
        yield return (fixture.Copy + "/", "absolute-root");
        yield return (fixture.Copy + "//", "doubled-separator");
        yield return (fixture.Neighbour + "/", "prefix-neighbour");

        if (fixture.HasSymlinks)
        {
            yield return ("nested/", "symlink-deeper-inside");
            yield return ("hop/", "symlink-chain-inside");
            yield return ("out/", "symlink-outside");
            yield return ("neighbour/", "symlink-prefix-neighbour");
            yield return ("dangling/", "dangling-symlink");
            yield return ("loop/", "symlink-loop");
        }

        if (fixture.HasBackslashNames)
        {
            yield return ("a\\b\\c\\", "backslash-entry-name");
        }
    }

    private static IEnumerable<(string Value, string Tag)> AbsoluteValues(ContainmentFixture fixture)
    {
        yield return (fixture.Copy, "the-copy-itself");
        yield return (fixture.Copy + "/", "trailing-separator");
        yield return (fixture.Copy + "/src/main.txt", "innocent");
        yield return (fixture.Copy + "x", "prefix-neighbour");
        yield return (fixture.Copy + "x/keys.txt", "prefix-neighbour");
        yield return (fixture.Copy + "0/keys.txt", "prefix-neighbour");
        yield return (fixture.Neighbour + "/keys.txt", "prefix-neighbour");
        yield return (fixture.Secrets, "secret");
        yield return (fixture.Secrets + "/keys.txt", "secret");
        yield return (fixture.Secrets + "/ring/dp.xml", "secret");
        // Padded past PATH_MAX. The kernel refuses a path this long AS WRITTEN, so the only reading
        // that answers for these is the composed one, which is also the reading a real CLI performs:
        // it collapses the padding itself and then opens something real. They are here because the
        // length is what a check gets tuned around, and a corpus that never generates one lets a bound
        // be picked that quietly turns honest paths into prompts.
        var flood = string.Concat(Enumerable.Repeat("./", 5000));
        yield return (fixture.Copy + "/" + flood + "src/main.txt", "padding-past-path-max");
        yield return (fixture.Copy + "/" + flood + "../secrets/keys.txt", "padding-past-path-max");

        yield return ("/proc/1/environ", "real-container-secret");
        yield return ("/data/fravashi.db", "real-container-secret");
        yield return ("/etc/passwd", "real-container-secret");

        if (fixture.HasSymlinks)
        {
            yield return (fixture.Copy + "/out/keys.txt", "symlink-outside");
            yield return (fixture.Copy + "/nested/keys.txt", "symlink-deeper-inside");
            yield return (fixture.Copy + "/dangling", "dangling-symlink");
            yield return (fixture.Copy + "/loop", "symlink-loop");
        }
    }

    private static void AddPatterns(ContainmentFixture fixture, List<ContainmentCase> cases)
    {
        var bodies = new List<(string Pattern, string[] Tags)>();
        foreach (var (spelling, tag) in ClimbSpellings)
        {
            for (var depth = 1; depth <= 8; depth++)
            {
                bodies.Add((string.Join('/', Enumerable.Repeat(spelling, depth)) + "/secrets/*", [tag]));
            }
        }

        foreach (var fixedPattern in FixedPatterns)
        {
            bodies.Add((fixedPattern, ["fixed-pattern"]));
        }

        var directories = new List<(string? Path, string Tag)>
        {
            (null, "no-path-field"),
            ("src/deep/a/b", "deep-real-directory"),
            (fixture.Copy, "absolute-root"),
        };

        if (fixture.HasSymlinks)
        {
            directories.Add(("nested", "symlink-deeper-inside"));
            directories.Add(("hop", "symlink-chain-inside"));
            directories.Add(("out", "symlink-outside"));

            // Round SIX as a PATTERN, which the corpus never generated at all: the path field is a
            // link going deeper IN, the pattern climbs, and what it climbs onto is a link going OUT.
            // Each half reads as in-copy on its own and only the composition lists the secrets, so a
            // corpus without this shape leaves the entire live half of that escape (patterns arrive
            // un-collapsed, unlike a file_path) with nothing generated against it. It is spelled
            // every way glob syntax spells a "..", because that is the cross product the fixed list
            // below is not.
            foreach (var (spelling, tag) in ClimbSpellings)
            {
                for (var depth = 1; depth <= 3; depth++)
                {
                    var climb = string.Join('/', Enumerable.Repeat(spelling, depth));
                    bodies.Add((climb + "/out/*", [tag, "through-escaping-link"]));
                    bodies.Add((climb + "/neighbour/*", [tag, "through-escaping-link"]));
                }
            }

            // Round SEVEN, which the corpus generated no shape of: a WILDCARD matching the escaping
            // link's own name, with a LITERAL tail behind it so the engine traverses through it. The
            // previous round added tails that reach through an escaping link the pattern SPELLS; this
            // is the cross product with a segment that spells nothing at all and matches the name,
            // which is the half no "does it spell .." rule can ever see.
            foreach (var (wildcard, wildcardTag) in WildcardSpellings)
            {
                foreach (var tail in WildcardTails)
                {
                    bodies.Add((
                        $"{wildcard}/{tail}",
                        [wildcardTag, "wildcard-matched-link", "through-escaping-link"]));

                    // The same with the wildcard one level down, so the traversal is not the first
                    // thing the engine does and a literal prefix exists in front of it.
                    bodies.Add((
                        $"./{wildcard}/{tail}",
                        [wildcardTag, "wildcard-matched-link", "through-escaping-link"]));
                }
            }

            // A climb onto the copy root and THEN a wildcard that matches the escaping link there:
            // every judged part is inside the copy under all three readings, and the escape is what
            // the wildcard turns out to match.
            bodies.Add(("../*/keys.txt", ["star", "wildcard-matched-link", "through-escaping-link"]));
            bodies.Add(("../**/keys.txt", ["globstar", "wildcard-matched-link", "through-escaping-link"]));

            bodies.Add(("out/*", ["fixed-pattern", "through-escaping-link"]));
            bodies.Add(("out/**/*", ["fixed-pattern", "through-escaping-link"]));
            bodies.Add(("../out/*", ["fixed-pattern", "through-escaping-link"]));
            bodies.Add(("{..,zz}/out/{keys,k}.txt", ["brace", "through-escaping-link"]));
        }

        var tool = 0;
        foreach (var (path, pathTag) in directories)
        {
            foreach (var (pattern, patternTags) in bodies)
            {
                var (name, field) = tool++ % 4 == 3 ? ("Grep", "glob") : ("Glob", "pattern");
                cases.Add(new ContainmentCase(
                    cases.Count, name, ContainmentShape.Pattern,
                    PathField: path is null ? null : "path", PathValue: path, PathIsText: true,
                    PatternField: field, PatternValue: pattern, PatternIsText: true,
                    Tags: [pathTag, .. patternTags, "pattern"]));
            }
        }
    }

    private static void AddNonStrings(List<ContainmentCase> cases)
    {
        // Deep enough to walk back into the copy from a base manufactured out of JSON punctuation,
        // which is exactly what the non-string path field bought last time.
        const string DeepClimb = "../../../../../../../../secrets/*";

        (string Tool, string Field, string? PatternField, string? Pattern)[] shapes =
        [
            ("Read", "file_path", null, null),
            ("NotebookRead", "notebook_path", null, null),
            ("LS", "path", null, null),
            ("Glob", "path", "pattern", DeepClimb),
            ("Grep", "path", "glob", "*"),
        ];

        foreach (var rendering in NonStringRenderings)
        {
            foreach (var (tool, field, patternField, pattern) in shapes)
            {
                cases.Add(new ContainmentCase(
                    cases.Count, tool, ContainmentShape.NotAString,
                    field, rendering, PathIsText: false,
                    patternField, pattern, PatternIsText: pattern is not null,
                    Tags: ["non-string-path", "json-kind"]));
            }

            // The mirror image: a real directory, and a PATTERN that is not a string.
            cases.Add(new ContainmentCase(
                cases.Count, "Glob", ContainmentShape.NotAString,
                "path", ".", PathIsText: true,
                "pattern", rendering, PatternIsText: false,
                Tags: ["non-string-pattern", "json-kind"]));
        }
    }

    private static string TailTag(string tail) => tail switch
    {
        "src/main.txt" => "innocent",
        "newfile-that-is-not-there.txt" => "missing-leaf",
        "nope/deeper/x.txt" => "missing-parent",
        "secrets/" => "trailing-separator",
        "w10/keys.txt" => "prefix-neighbour",
        "out/keys.txt" or "out/ring/dp.xml" or "neighbour/keys.txt" => "through-escaping-link",
        _ => "secret",
    };
}
