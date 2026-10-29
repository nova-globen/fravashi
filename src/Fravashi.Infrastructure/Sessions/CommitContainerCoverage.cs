using System.Text;
using System.Text.RegularExpressions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// The container facts a Skip condition is evaluated against (backlog 295): a seam so a test can hand
/// <see cref="CommitContainerCoverage.Scan"/> container-shaped facts deterministically, whatever the test SUITE
/// ITSELF happens to run on (a Windows dev host carries pwsh and powershell.exe on PATH, which the studio's own
/// Linux container never does). Production never constructs one of these: <see cref="CommitContainerCoverage.Real"/>
/// is the default and reads <see cref="ContainerCapabilities"/>.
/// </summary>
public interface IContainerFacts
{
    /// <summary>Whether the facts describe a Windows host.</summary>
    bool IsWindows { get; }

    /// <summary>Whether a PowerShell interpreter is on PATH.</summary>
    bool HasPowerShellOnPath { get; }

    /// <summary>Whether a named environment variable reaches the test run these facts describe.</summary>
    bool EnvVarIsSet(string name);

    /// <summary>Whether a file exists at an absolute path.</summary>
    bool FileExists(string path);
}

/// <summary>
/// Backlog 295: what a commit review could not run in the studio's own container, read from the test SOURCES in
/// the workspace's contained copy, never by loading or reflecting over a test assembly and never by running a
/// test's constructor. A changed path is matched to a test file by its basename appearing in that file's text, or
/// by being that file itself (a test file is not guaranteed to contain its own name); within a matched file, a
/// custom <c>[XFact]</c>/<c>[XTheory]</c> attribute actually applied to a test is resolved (the same file first,
/// matching this repository's private-nested convention, so two files naming the same attribute do not collide; a
/// shared <c>internal</c>/<c>public</c> one falls back to a small cross-file index) and its constructor is read as
/// a tiny expression grammar: <see cref="OperatingSystem.IsWindows"/>, <c>Environment.GetEnvironmentVariable</c>
/// presence, <c>File.Exists</c> on a literal path, and <c>&amp;&amp;</c>/<c>||</c>/<c>!</c>/ternary/if-chain
/// composition of those, evaluated against an <see cref="IContainerFacts"/> (<see cref="Real"/> in production). A
/// shape this cannot read, or an attribute this cannot resolve at all, is reported as unclear rather than treated
/// as covered or silently dropped, per the item's own instruction to say so rather than guess.
/// <para>
/// One exception is named rather than parsed: a reference whose type is literally <c>RealPowerShell</c> is the
/// test helper that resolves a PowerShell interpreter by starting a real process, which this scanner must not do,
/// so it is answered directly from <see cref="IContainerFacts.HasPowerShellOnPath"/> instead, reusing the literal
/// reason text already written in that helper's own <c>SkipReason</c> property.
/// </para>
/// <para>
/// A second: a reason that itself names "the studio container" as where its own tier runs (the real-browser live
/// tier's own wording) is downgraded from a confident skip to unclear rather than left as a claim that
/// contradicts its own source (see <see cref="RunsHereHint"/>): whether the building agent set the tier's switch
/// on the command it ran is not something source text can answer.
/// </para>
/// </summary>
public static class CommitContainerCoverage
{
    private const int MaxDepth = 4;

    /// <summary>The production facts: this process's own platform, PATH and environment.</summary>
    public static readonly IContainerFacts Real = new RealContainerFacts();

    private const string UnclearReason = "unclear from source whether this runs in the container; check by hand";

    private static readonly Regex AttributeClassDeclaration = new(
        @"(?<access>public|internal|private|protected)\s+(?:sealed\s+)?class\s+(?<name>\w+)\s*:\s*(?:FactAttribute|TheoryAttribute)\b",
        RegexOptions.Compiled);

    // A bare "[XFact]" and an attribute carrying arguments or riding beside another in the same list
    // ("[XFact(Timeout = 1)]", "[Theory, XTheory]") both start the same way: right after "[" or ",".
    private static readonly Regex UsedAttribute = new(
        @"[\[,]\s*(?<name>[A-Za-z_]\w*(?:Fact|Theory))\b",
        RegexOptions.Compiled);

    private static readonly Regex MainClassDeclaration = new(
        @"(?:public|internal)\s+(?:sealed\s+)?(?:partial\s+)?class\s+(?<name>\w+)",
        RegexOptions.Compiled);

    /// <summary>
    /// What, among the changed repository-relative paths, a test in the copy at <paramref name="copyRoot"/> is
    /// known or suspected not to run in this container, as "ClassName (reason)" lines, sorted and de-duplicated.
    /// Empty when nothing under <c>tests/</c> is touched, or the copy is not there yet. <paramref name="facts"/>
    /// defaults to <see cref="Real"/>; a test passes container-shaped facts instead.
    /// </summary>
    public static IReadOnlyList<string> Scan(string? copyRoot, IReadOnlyList<string> changedRepoPaths, IContainerFacts? facts = null)
    {
        if (string.IsNullOrWhiteSpace(copyRoot) || changedRepoPaths.Count == 0)
        {
            return [];
        }

        var testsRoot = Path.Combine(copyRoot, "tests");
        if (!Directory.Exists(testsRoot))
        {
            return [];
        }

        facts ??= Real;

        var basenames = changedRepoPaths
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // A changed test file does not necessarily contain its own name (#272's own follow-up touched only
        // WebInstallCurlRetryTests.cs itself), so it is a match in its own right, by path, not only by mention.
        var changedFullPaths = changedRepoPaths
            .Select(p => NormalizeFullPath(Path.Combine(copyRoot, p.Replace('/', Path.DirectorySeparatorChar))))
            .ToHashSet(StringComparer.Ordinal);

        if (basenames.Count == 0 && changedFullPaths.Count == 0)
        {
            return [];
        }

        var fileTexts = ReadTestSources(testsRoot);

        // A cross-file index of SHARED (internal/public) skip attributes, by their usage name (without the
        // "Attribute" suffix): the private, per-file convention is resolved locally and never needs this.
        var shared = new Dictionary<string, (string FileText, string Body, string ClassName)>(StringComparer.Ordinal);
        foreach (var text in fileTexts.Values)
        {
            foreach (Match m in AttributeClassDeclaration.Matches(text))
            {
                if (string.Equals(m.Groups["access"].Value, "private", StringComparison.Ordinal))
                {
                    continue;
                }

                var className = m.Groups["name"].Value;
                var key = UsageName(className);
                if (!shared.ContainsKey(key) && TryExtractBracedBody(text, m.Index + m.Length, out var body))
                {
                    shared[key] = (text, body, className);
                }
            }
        }

        var results = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (path, text) in fileTexts)
        {
            var matches = basenames.Any(b => text.Contains(b, StringComparison.Ordinal))
                || changedFullPaths.Contains(NormalizeFullPath(path));
            if (!matches)
            {
                continue;
            }

            var localAttrNames = new HashSet<string>(StringComparer.Ordinal);
            var localDefs = new Dictionary<string, (string Body, string ClassName)>(StringComparer.Ordinal);
            foreach (Match m in AttributeClassDeclaration.Matches(text))
            {
                var className = m.Groups["name"].Value;
                localAttrNames.Add(className);
                if (TryExtractBracedBody(text, m.Index + m.Length, out var body))
                {
                    localDefs[UsageName(className)] = (body, className);
                }
            }

            // Each usage is named by the class that ENCLOSES it, not "the file's first class": a file can hold
            // more than one test class (EmbeddingTests.cs holds WordPieceTokenizerTests and OnnxTextEmbedderTests),
            // and a usage inside the second one is not the first one's to claim.
            var classDeclarations = MainClassDeclaration.Matches(text)
                .Where(m => !localAttrNames.Contains(m.Groups["name"].Value))
                .Select(m => (Index: m.Index, Name: m.Groups["name"].Value))
                .OrderBy(c => c.Index)
                .ToList();
            if (classDeclarations.Count == 0)
            {
                continue;
            }

            foreach (Match used in UsedAttribute.Matches(text))
            {
                var name = used.Groups["name"].Value;
                var enclosing = classDeclarations.LastOrDefault(c => c.Index <= used.Index).Name;
                if (enclosing is null)
                {
                    continue;
                }

                string? body;
                string? className;
                string scopeText;
                if (localDefs.TryGetValue(name, out var local))
                {
                    (body, className) = local;
                    scopeText = text;
                }
                else if (shared.TryGetValue(name, out var global))
                {
                    body = global.Body;
                    className = global.ClassName;
                    scopeText = global.FileText;
                }
                else
                {
                    // A custom [XFact]/[XTheory] is unmistakably in play, but its definition cannot be found
                    // (declared with no access modifier, or outside tests/ entirely): reported, not dropped.
                    results.Add($"{enclosing} ({UnclearReason})");
                    continue;
                }

                var ctor = ExtractConstructorBody(body, className);
                results.Add(ctor is null
                    ? $"{enclosing} ({UnclearReason})"
                    : Describe(enclosing, EvaluateConstructorBody(ctor, scopeText, facts)));
            }
        }

        return [.. results.Where(r => r.Length > 0)];
    }

    /// <summary>
    /// A reason naming the STUDIO CONTAINER as where the gated tier runs (the real-browser live tier's own
    /// wording: "...where a Chromium is installed (the studio container)..."): an agent sets its switch
    /// explicitly on the command it runs (CLAUDE.md tells it to), which <see cref="ContainerCapabilities.EnvVarIsSet"/>
    /// cannot see either way (it answers INHERITANCE only, backlog 108), so "not run" would contradict the test's
    /// own documentation and whether it really ran is not something source text can settle. Unclear, not a guess
    /// either way, rather than the self-contradicting claim this would otherwise be.
    /// </summary>
    private const string RunsHereHint = "studio container";

    private static string Describe(string enclosing, Outcome outcome) => outcome.Kind switch
    {
        OutcomeKind.Skips when outcome.Reason!.Contains(RunsHereHint, StringComparison.OrdinalIgnoreCase) =>
            $"{enclosing} ({UnclearReason})",
        OutcomeKind.Skips => $"{enclosing} ({outcome.Reason})",
        OutcomeKind.Unclear => $"{enclosing} ({UnclearReason})",
        _ => string.Empty,
    };

    private static string NormalizeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static string UsageName(string attributeClassName) =>
        attributeClassName.EndsWith("Attribute", StringComparison.Ordinal)
            ? attributeClassName[..^"Attribute".Length]
            : attributeClassName;

    private static Dictionary<string, string> ReadTestSources(string testsRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return result;
        }
        catch (UnauthorizedAccessException)
        {
            return result;
        }

        var binSegment = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        var objSegment = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        foreach (var file in files)
        {
            if (file.Contains(binSegment, StringComparison.Ordinal) || file.Contains(objSegment, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                result[file] = File.ReadAllText(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    // ---- reading a class body and its constructor ----------------------------------------------

    private static bool TryExtractBracedBody(string text, int searchFrom, out string body)
    {
        body = string.Empty;
        var openBrace = text.IndexOf('{', searchFrom);
        if (openBrace < 0)
        {
            return false;
        }

        var closeBrace = MatchingBrace(text, openBrace);
        if (closeBrace < 0)
        {
            return false;
        }

        body = text[(openBrace + 1)..closeBrace];
        return true;
    }

    private static string? ExtractConstructorBody(string classBody, string className)
    {
        var exprCtor = Regex.Match(classBody, @$"{Regex.Escape(className)}\s*\(\s*\)\s*=>\s*", RegexOptions.Singleline);
        if (exprCtor.Success)
        {
            var start = exprCtor.Index + exprCtor.Length;
            var end = FindStatementEnd(classBody, start);
            return end > start ? classBody[start..(end + 1)] : null;
        }

        var blockCtor = Regex.Match(classBody, @$"{Regex.Escape(className)}\s*\(\s*\)\s*\{{", RegexOptions.Singleline);
        if (blockCtor.Success)
        {
            var openBrace = blockCtor.Index + blockCtor.Length - 1;
            var closeBrace = MatchingBrace(classBody, openBrace);
            return closeBrace > openBrace ? classBody[(openBrace + 1)..closeBrace] : null;
        }

        return null;
    }

    private static Outcome EvaluateConstructorBody(string body, string fileText, IContainerFacts facts)
    {
        body = body.Trim();
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        var branches = new List<(string Cond, string Statement)>();
        var remaining = body;
        while (true)
        {
            var pattern = branches.Count == 0 ? @"^if\s*\(" : @"^\s*else\s+if\s*\(";
            var ifMatch = Regex.Match(remaining, pattern, RegexOptions.Singleline);
            if (!ifMatch.Success)
            {
                break;
            }

            var openParen = remaining.IndexOf('(', ifMatch.Index);
            var closeParen = MatchingParen(remaining, openParen);
            if (closeParen < 0)
            {
                return Outcome.Unclear;
            }

            var cond = remaining[(openParen + 1)..closeParen];
            var openBrace = remaining.IndexOf('{', closeParen + 1);
            if (openBrace < 0)
            {
                return Outcome.Unclear;
            }

            var closeBrace = MatchingBrace(remaining, openBrace);
            if (closeBrace < 0)
            {
                return Outcome.Unclear;
            }

            branches.Add((cond, remaining[(openBrace + 1)..closeBrace]));
            remaining = remaining[(closeBrace + 1)..];
        }

        if (branches.Count > 0)
        {
            // Anything left after the last recognised "else if" block (PlatformOracle's foreach-over-literal-paths
            // fallback, among others) is a shape this reads as a chain and then stops reading, not a guess: an
            // if/else-if that falls through to more statements is UNCLEAR rather than silently "Runs".
            if (remaining.Trim().Length > 0)
            {
                return Outcome.Unclear;
            }

            foreach (var (cond, statement) in branches)
            {
                var value = EvalCondition(cond, fileText, facts, visiting, MaxDepth);
                if (value is null)
                {
                    return Outcome.Unclear;
                }

                if (value == true)
                {
                    return EvalAssignedExpression(statement, fileText, facts, visiting, MaxDepth);
                }
            }

            return Outcome.Runs;
        }

        return EvalAssignedExpression(body, fileText, facts, visiting, MaxDepth);
    }

    private static Outcome EvalAssignedExpression(
        string statementText, string fileText, IContainerFacts facts, HashSet<string> visiting, int depth)
    {
        // Anchored at the start (after only whitespace) and required to be the WHOLE statement: a leading
        // statement before this one (RealEmbeddingModelFactAttribute's "var directory = ...;" before its own
        // "if") would otherwise let an unrelated, unconditional "Skip = ..." elsewhere in the text be read as
        // this one. A non-anchored ".+?;" would also stop at a semicolon INSIDE a reason string (RealPowerShell's
        // own reason has one), so the true end is found the string-literal-aware way too.
        var trimmed = statementText.TrimStart();
        var leading = statementText.Length - trimmed.Length;

        var assign = Regex.Match(trimmed, @"^Skip\s*=\s*", RegexOptions.Singleline);
        if (!assign.Success)
        {
            assign = Regex.Match(trimmed, @"^return\s+", RegexOptions.Singleline);
        }

        if (!assign.Success)
        {
            return Outcome.Unclear;
        }

        var start = leading + assign.Length;
        var end = FindStatementEnd(statementText, start);
        if (end <= start)
        {
            return Outcome.Unclear;
        }

        return statementText[(end + 1)..].Trim().Length > 0
            ? Outcome.Unclear
            : EvalExpr(statementText[start..end], fileText, facts, visiting, depth);
    }

    // ---- expressions (what Skip, or a referenced property, is set to) --------------------------

    private static Outcome EvalExpr(string rhs, string fileText, IContainerFacts facts, HashSet<string> visiting, int depth)
    {
        rhs = rhs.Trim();
        if (rhs.Length == 0)
        {
            return Outcome.Unclear;
        }

        if (rhs == "null")
        {
            return Outcome.Runs;
        }

        if (TryReadStringLiteralChain(rhs, out var literal))
        {
            return Outcome.Skip(SubstitutePlaceholders(literal, fileText));
        }

        if (Regex.IsMatch(rhs, @"^RealPowerShell\.\w+"))
        {
            return facts.HasPowerShellOnPath
                ? Outcome.Runs
                : Outcome.Skip(RealPowerShellReason(fileText)
                    ?? "No PowerShell interpreter on PATH; this container does not ship pwsh or powershell.");
        }

        if (TrySplitTernary(rhs, out var cond, out var whenTrue, out var whenFalse))
        {
            var value = EvalCondition(cond, fileText, facts, visiting, depth);
            return value is null ? Outcome.Unclear : EvalExpr(value == true ? whenTrue : whenFalse, fileText, facts, visiting, depth);
        }

        if (Regex.IsMatch(rhs, @"^\w+$") && depth > 0 && visiting.Add(rhs))
        {
            try
            {
                var member = FindExpressionBodiedMember(fileText, rhs);
                if (member is not null)
                {
                    return EvalExpr(member, fileText, facts, visiting, depth - 1);
                }
            }
            finally
            {
                visiting.Remove(rhs);
            }
        }

        return Outcome.Unclear;
    }

    private static string? RealPowerShellReason(string fileText)
    {
        var skipReason = FindExpressionBodiedMember(fileText, "SkipReason");
        if (skipReason is null)
        {
            return null;
        }

        return TrySplitTernary(skipReason, out _, out _, out var whenNull) && TryReadStringLiteralChain(whenNull, out var reason)
            ? reason
            : null;
    }

    // ---- conditions (whether a branch's Skip fires here) ----------------------------------------

    private static bool? EvalCondition(string cond, string fileText, IContainerFacts facts, HashSet<string> visiting, int depth)
    {
        cond = cond.Trim();
        if (cond.Length == 0)
        {
            return null;
        }

        if (cond[0] == '(' && cond[^1] == ')' && MatchingParen(cond, 0) == cond.Length - 1)
        {
            return EvalCondition(cond[1..^1], fileText, facts, visiting, depth);
        }

        if (cond[0] == '!')
        {
            var inner = EvalCondition(cond[1..], fileText, facts, visiting, depth);
            return inner is null ? null : !inner;
        }

        var orParts = SplitTopLevel(cond, "||");
        if (orParts.Count > 1)
        {
            var sawNull = false;
            foreach (var part in orParts)
            {
                var value = EvalCondition(part, fileText, facts, visiting, depth);
                if (value == true)
                {
                    return true;
                }

                if (value is null)
                {
                    sawNull = true;
                }
            }

            return sawNull ? null : false;
        }

        var andParts = SplitTopLevel(cond, "&&");
        if (andParts.Count > 1)
        {
            var sawNull = false;
            foreach (var part in andParts)
            {
                var value = EvalCondition(part, fileText, facts, visiting, depth);
                if (value == false)
                {
                    return false;
                }

                if (value is null)
                {
                    sawNull = true;
                }
            }

            return sawNull ? null : true;
        }

        if (cond == "OperatingSystem.IsWindows()")
        {
            return facts.IsWindows;
        }

        var fileExists = Regex.Match(cond, "^File\\.Exists\\(\\s*\"(?<p>[^\"]*)\"\\s*\\)$");
        if (fileExists.Success)
        {
            return facts.FileExists(fileExists.Groups["p"].Value);
        }

        if (cond.Contains("GetEnvironmentVariable", StringComparison.Ordinal))
        {
            return EvalEnvVarCondition(cond, fileText, facts);
        }

        if (Regex.IsMatch(cond, @"^\w+$") && depth > 0 && visiting.Add(cond))
        {
            try
            {
                var member = FindExpressionBodiedMember(fileText, cond);
                if (member is not null)
                {
                    return EvalCondition(member, fileText, facts, visiting, depth - 1);
                }
            }
            finally
            {
                visiting.Remove(cond);
            }
        }

        return null;
    }

    /// <summary>
    /// A condition naming <c>GetEnvironmentVariable</c>, read ONLY for the shapes this repository's live tiers
    /// actually write: "is it blank/null" (negative: true means NOT set) or a comparison to a literal, either
    /// <c>==</c>/<c>!=</c> or <c>string.Equals(...)</c> (by this point with no leading <c>!</c>, which
    /// <see cref="EvalCondition"/> already stripped and will negate). Anything else (compared against a variable,
    /// used as a bare truthy value, or any shape not listed here) is a guess this does not make: null.
    /// </summary>
    private static bool? EvalEnvVarCondition(string cond, string fileText, IContainerFacts facts)
    {
        var name = ExtractEnvVarName(cond, fileText);
        if (name is null)
        {
            return null;
        }

        bool negative;
        if (cond.Contains("IsNullOrWhiteSpace", StringComparison.Ordinal) || cond.Contains("is null", StringComparison.Ordinal)
            || cond.Contains("!=", StringComparison.Ordinal))
        {
            negative = true;
        }
        else if (cond.Contains("==", StringComparison.Ordinal) || cond.Contains("string.Equals(", StringComparison.Ordinal))
        {
            negative = false;
        }
        else
        {
            return null;
        }

        var isSet = facts.EnvVarIsSet(name);
        return negative ? !isSet : isSet;
    }

    private static readonly Regex InterpolationPlaceholder = new(@"\{(?<name>[\w.]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Fills in a <c>{Type.Const}</c> interpolation hole left verbatim by <see cref="TryReadStringLiteralChain"/>
    /// when the referenced member resolves to a same-file string constant (the common shape: a live tier's own
    /// environment variable NAME, named in its own reason text), so the shown reason reads as the test's author
    /// wrote it rather than as a code fragment. A hole that does not resolve this way is left exactly as written.
    /// </summary>
    private static string SubstitutePlaceholders(string text, string fileText) =>
        InterpolationPlaceholder.Replace(text, m =>
        {
            var arg = m.Groups["name"].Value;
            var field = arg.Contains('.', StringComparison.Ordinal) ? arg[(arg.LastIndexOf('.') + 1)..] : arg;
            var constMatch = Regex.Match(fileText, @$"const\s+string\s+{Regex.Escape(field)}\s*=\s*""(?<v>[^""]*)""");
            return constMatch.Success ? constMatch.Groups["v"].Value : m.Value;
        });

    private static string? ExtractEnvVarName(string text, string fileText)
    {
        var call = Regex.Match(text, @"GetEnvironmentVariable\(\s*(?<arg>[^)]*)\)");
        if (!call.Success)
        {
            return null;
        }

        var arg = call.Groups["arg"].Value.Trim();
        var literal = Regex.Match(arg, "^\"(?<v>[^\"]*)\"$");
        if (literal.Success)
        {
            return literal.Groups["v"].Value;
        }

        var field = arg.Contains('.', StringComparison.Ordinal) ? arg[(arg.LastIndexOf('.') + 1)..] : arg;
        if (!Regex.IsMatch(field, @"^\w+$"))
        {
            return null;
        }

        var constMatch = Regex.Match(fileText, @$"const\s+string\s+{Regex.Escape(field)}\s*=\s*""(?<v>[^""]*)""");
        return constMatch.Success ? constMatch.Groups["v"].Value : null;
    }

    private static string? FindExpressionBodiedMember(string fileText, string name)
    {
        var declaration = Regex.Match(fileText, @$"\b{Regex.Escape(name)}\b[^=;{{}}]*=>\s*", RegexOptions.Singleline);
        if (!declaration.Success)
        {
            return null;
        }

        var start = declaration.Index + declaration.Length;
        var end = FindStatementEnd(fileText, start);
        return end > start ? fileText[start..end].Trim() : null;
    }

    // ---- small text-scanning primitives, all string/char-literal aware -------------------------

    private static List<string> SplitTopLevel(string text, string op)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                i = SkipStringLiteral(text, i);
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(text, i);
                continue;
            }

            if (c == '(')
            {
                depth++;
                i++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                i++;
                continue;
            }

            if (depth == 0 && i + op.Length <= text.Length && string.CompareOrdinal(text, i, op, 0, op.Length) == 0)
            {
                parts.Add(text[start..i]);
                i += op.Length;
                start = i;
                continue;
            }

            i++;
        }

        parts.Add(text[start..]);
        return parts;
    }

    private static bool TrySplitTernary(string text, out string cond, out string whenTrue, out string whenFalse)
    {
        cond = whenTrue = whenFalse = string.Empty;
        var depth = 0;
        var questionIndex = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                i = SkipStringLiteral(text, i) - 1;
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(text, i) - 1;
                continue;
            }

            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                continue;
            }

            if (depth == 0 && c == '?' && (i + 1 >= text.Length || (text[i + 1] != '.' && text[i + 1] != '?')))
            {
                questionIndex = i;
                break;
            }
        }

        if (questionIndex < 0)
        {
            return false;
        }

        depth = 0;
        var colonIndex = -1;
        for (var i = questionIndex + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                i = SkipStringLiteral(text, i) - 1;
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(text, i) - 1;
                continue;
            }

            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                continue;
            }

            if (depth == 0 && c == ':')
            {
                colonIndex = i;
                break;
            }
        }

        if (colonIndex < 0)
        {
            return false;
        }

        cond = text[..questionIndex].Trim();
        whenTrue = text[(questionIndex + 1)..colonIndex].Trim();
        whenFalse = text[(colonIndex + 1)..].Trim();
        return true;
    }

    private static bool TryReadStringLiteralChain(string rhs, out string text)
    {
        text = string.Empty;
        var i = 0;
        var any = false;
        var sb = new StringBuilder();
        while (i < rhs.Length)
        {
            while (i < rhs.Length && char.IsWhiteSpace(rhs[i]))
            {
                i++;
            }

            if (i < rhs.Length && rhs[i] == '$')
            {
                i++;
            }

            if (i >= rhs.Length || rhs[i] != '"')
            {
                break;
            }

            var quoteStart = i;
            var end = SkipStringLiteral(rhs, quoteStart);
            sb.Append(rhs[(quoteStart + 1)..(end - 1)]);
            any = true;
            i = end;
            while (i < rhs.Length && char.IsWhiteSpace(rhs[i]))
            {
                i++;
            }

            if (i < rhs.Length && rhs[i] == '+')
            {
                i++;
                continue;
            }

            break;
        }

        if (!any || i < rhs.Length)
        {
            return false;
        }

        text = sb.ToString();
        return true;
    }

    private static int FindStatementEnd(string text, int start)
    {
        var depth = 0;
        var i = start;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                i = SkipStringLiteral(text, i);
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(text, i);
                continue;
            }

            if (c == '(' || c == '[')
            {
                depth++;
                i++;
                continue;
            }

            if (c == ')' || c == ']')
            {
                depth--;
                i++;
                continue;
            }

            if (c == ';' && depth == 0)
            {
                return i;
            }

            i++;
        }

        return -1;
    }

    private static int MatchingBrace(string text, int openBraceIndex)
    {
        var depth = 0;
        var i = openBraceIndex;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                i = SkipStringLiteral(text, i);
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(text, i);
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var newline = text.IndexOf('\n', i);
                i = newline < 0 ? text.Length : newline + 1;
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }

            i++;
        }

        return -1;
    }

    private static int MatchingParen(string text, int openIndex)
    {
        var depth = 0;
        var i = openIndex;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                i = SkipStringLiteral(text, i);
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(text, i);
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }

            i++;
        }

        return -1;
    }

    private static int SkipStringLiteral(string text, int quoteIndex)
    {
        var verbatim = quoteIndex > 0 && text[quoteIndex - 1] == '@';
        var i = quoteIndex + 1;
        while (i < text.Length)
        {
            if (verbatim)
            {
                if (text[i] == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }
            }
            else
            {
                if (text[i] == '\\')
                {
                    i += 2;
                    continue;
                }

                if (text[i] == '"')
                {
                    return i + 1;
                }
            }

            i++;
        }

        return text.Length;
    }

    private static int SkipCharLiteral(string text, int quoteIndex)
    {
        var i = quoteIndex + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (text[i] == '\'')
            {
                return i + 1;
            }

            i++;
        }

        return text.Length;
    }

    // ---- the tri-state result of reading one Skip expression -----------------------------------

    private enum OutcomeKind
    {
        Runs,
        Skips,
        Unclear,
    }

    private readonly record struct Outcome(OutcomeKind Kind, string? Reason)
    {
        public static readonly Outcome Runs = new(OutcomeKind.Runs, null);
        public static readonly Outcome Unclear = new(OutcomeKind.Unclear, null);

        public static Outcome Skip(string reason) => new(OutcomeKind.Skips, reason);
    }

    // ---- the real facts, used wherever a caller gives Scan none -------------------------------

    private sealed class RealContainerFacts : IContainerFacts
    {
        public bool IsWindows => ContainerCapabilities.IsWindows;

        public bool HasPowerShellOnPath => ContainerCapabilities.HasPowerShellOnPath;

        public bool EnvVarIsSet(string name) => ContainerCapabilities.EnvVarIsSet(name);

        public bool FileExists(string path) => ContainerCapabilities.FileExists(path);
    }
}
