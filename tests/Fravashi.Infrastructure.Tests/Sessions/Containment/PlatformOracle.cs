using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Fravashi.Infrastructure.Tests.Sessions.Containment;

/// <summary>What the operating system says one adversarial call would really reach.</summary>
public enum PlatformTruth
{
    /// <summary>The OS could not be asked (this platform has no oracle, or the run failed).</summary>
    Unknown,

    /// <summary>
    /// The OS WAS asked and the oracle still cannot say where this lands: the kernel refused the name
    /// for a reason that is not "it is not there", a pattern the shell would not parse, a match set too
    /// large to account for. It is reported and it fails, exactly like <see cref="Unknown"/>, because a
    /// case nobody judged is a hole in the guard rather than a pass. Keeping it apart from
    /// <see cref="NoReach"/> is the point: the two used to share a bucket, and the bucket was never
    /// asserted against, so a whole family of escapes could sit in it looking like "nothing happened".
    /// </summary>
    Undetermined,

    /// <summary>
    /// Nothing is reachable at all, and the platform SAID so rather than the oracle running out of ways
    /// to ask: the name is positively absent (and could not be brought into existence either), the
    /// kernel refused it outright so an opener handed the same name reads nothing through it, or the
    /// pattern matched nothing. A read of it leaks nothing, so a "contained" verdict here is not a
    /// breach and is not asserted against.
    /// </summary>
    NoReach,

    /// <summary>Everything it would touch is an object the OS finds inside the contained copy.</summary>
    Inside,

    /// <summary>
    /// At least one object it would touch is NOT inside the copy. This is the breach direction: the
    /// studio must never call such a call contained.
    /// </summary>
    Escapes,

    /// <summary>
    /// There is no path here for any OS to resolve, because the field did not arrive as a JSON string.
    /// Nothing may be claimed about where it lands.
    /// </summary>
    NotAPath,
}

/// <summary>
/// GROUND TRUTH, taken from the platform.
/// <para>
/// The point of this type is what it does NOT do. It contains no path arithmetic, no normalisation, no
/// prefix comparison and no second opinion about what a separator is, because a test built out of the
/// same reasoning as the code under test asserts that code back to itself. That is precisely how four
/// escapes in a row got through a green suite.
/// </para>
/// <para>
/// So every question is put to the operating system, over a REAL tree
/// (<see cref="ContainmentFixture"/>), through programs that are not the studio:
/// <list type="bullet">
/// <item>Where a PATH lands is answered by <c>stat</c> following it from a shell whose working
/// directory is the copy, exactly as the agent CLI's child would. The answer taken from it is the
/// INODE, not a string, so nothing about the spelling can influence the comparison. It is asked TWICE:
/// once about the path as written, and once about the path an opener composes, which is
/// <c>realpath -s</c> (coreutils' own lexical collapse of <c>..</c>) followed by the same <c>stat</c>.
/// A caller does the second, always, and the two are not each other: round 6 was a value each reading
/// called contained on its own while their composition opened the secret. The worse of the two answers
/// is the verdict.</item>
/// <item>Whether that inode is inside the copy is answered by <c>find</c> walking the copy. A symlink
/// leaving the copy is therefore outside by construction: <c>find</c> does not descend it.</item>
/// <item>What a PATTERN expands to is answered by a real glob engine, the shell's own, with extglob,
/// globstar and nullglob on. Brace lists, character classes, extglob groups and backslash escapes are
/// then handled by the thing that defines what they mean rather than by a model of it.</item>
/// <item>A name that does not exist yet is answered by CREATING it (<c>mkdir</c>, which never
/// overwrites anything) and asking where it appeared, then removing it. An absent leaf is one of the
/// ways a check gets dodged, so "it is not there" is not accepted as "it is harmless".</item>
/// <item>A name that resolves to NOTHING is not accepted as harmless on the strength of the oracle
/// having run out of ways to ask, either. The kernel is asked for its REASON, and only a reason that
/// really means "an opener handed this same name reads nothing through it" counts as
/// <see cref="PlatformTruth.NoReach"/>. Anything else is <see cref="PlatformTruth.Undetermined"/> and
/// is reported.</item>
/// </list>
/// </para>
/// <para>
/// A glob MATCH is judged without following a final symlink, because a match is a name the walker
/// found and reporting a name leaks nothing; a PATH argument is judged following it, because the tool
/// opens what it points at. That distinction is the tools' behaviour, not a softening of the rule.
/// </para>
/// <para>
/// A PATTERN gets both readings too, and it used not to. It was expanded only from a directory the
/// shell reached PHYSICALLY (<c>cd -P</c>), so the oracle answered for the pattern as spelled and said
/// NOREACH about every pattern a normalising walker reads differently: <c>nested/../out/*</c> walks out
/// of where <c>nested</c> landed and matches nothing as written, while collapsing its <c>..</c> as text
/// first spells <c>&lt;copy&gt;/out/*</c> and lists the secrets. NOREACH is not asserted against, so the
/// oracle held the exact blind spot of the code it audits, which is the worst property a differential
/// can have: it makes the subsystem look verified. The composed reading needs no model of that walker,
/// only the same composition applied to a pattern: bash performs the brace expansion and quote removal
/// (with globbing off, so a spelling like <c>{..,zz}</c> or <c>.\.</c> becomes <c>..</c> because a real
/// shell says it does), coreutils collapses the result lexically, and the shell's glob engine expands
/// what that spells. Three separate implementations, none of them this repository's.
/// </para>
/// <para>
/// This is Linux only, and deliberately so. The subsystem it guards runs in the studio's Linux
/// container, and the constructions that broke it (a backslash as an ordinary filename character, POSIX
/// glob syntax, unprivileged symlinks) either do not exist on Windows or mean something else there.
/// Running a weaker check on Windows and calling the boundary covered is the failure mode this whole
/// file exists to end, so on Windows it reports why it cannot answer and the differential test skips.
/// </para>
/// </summary>
public static class PlatformOracle
{
    /// <summary>
    /// What separates one job's fields from the next. It is a UNIT SEPARATOR rather than a tab, and
    /// that is the whole point: a tab is IFS whitespace, so a run of them collapses into one and an
    /// EMPTY field simply disappears. The oracle read its jobs with <c>IFS</c> set to tab, so every
    /// case with no path field (a Glob or a Grep that names only a pattern, which is 112 of them)
    /// arrived with its pattern shifted into the directory slot, was judged as a nonsense directory,
    /// answered NOREACH, and was never asserted against. A whole family of calls looked measured and
    /// was not. A non-whitespace separator never collapses and never drops an empty field, so a job
    /// either parses into exactly the four values it was written with or does not parse at all.
    /// </summary>
    private const char Separator = '\u001f';

    /// <summary>Why the oracle cannot run here, or null when it can.</summary>
    public static string? SkipReason { get; } = Determine();

    private static string? Determine()
    {
        if (OperatingSystem.IsWindows())
        {
            return "ground truth for this boundary is the Linux kernel plus a real POSIX glob engine, and "
                + "the constructions that broke it (a backslash as a filename character, extglob, "
                + "unprivileged symlinks) do not mean the same thing on Windows. A green run here has "
                + "not measured this boundary at all. Run it on Linux: the recipe is in README.md beside "
                + "this file (an SDK container over the repo, because the studio image ships no SDK).";
        }

        foreach (var tool in new[] { "/bin/bash", "/usr/bin/env" })
        {
            if (File.Exists(tool))
            {
                return null;
            }
        }

        return "no shell was found to expand patterns with, so there is no independent glob engine to "
            + "measure the studio against.";
    }

    /// <summary>
    /// What the platform says each case would reach, keyed by case index. Cases whose field is not a
    /// JSON string never reach the shell: there is no path in them to resolve.
    /// </summary>
    public static IReadOnlyDictionary<int, PlatformTruth> Judge(
        string copy,
        IReadOnlyList<ContainmentCase> cases,
        string? secondInsideRoot = null,
        string? thirdInsideRoot = null)
    {
        ArgumentNullException.ThrowIfNull(cases);

        var truths = new Dictionary<int, PlatformTruth>();
        var jobs = new StringBuilder();
        var asked = 0;

        foreach (var probe in cases)
        {
            if (probe.Shape == ContainmentShape.NotAString)
            {
                truths[probe.Index] = PlatformTruth.NotAPath;
                continue;
            }

            var kind = probe.Shape == ContainmentShape.Pattern ? "pattern" : "path";
            var path = Encodable(probe, probe.PathValue);
            var pattern = Encodable(probe, probe.PatternValue);
            jobs.Append(CultureInfo.InvariantCulture, $"{probe.Index}{Separator}{kind}{Separator}{path}{Separator}{pattern}\0");
            truths[probe.Index] = PlatformTruth.Unknown;
            asked++;
        }

        if (asked == 0)
        {
            return truths;
        }

        var scratch = Path.Combine(Path.GetTempPath(), "fravashi-oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var scriptPath = Path.Combine(scratch, "oracle.sh");
            var jobsPath = Path.Combine(scratch, "jobs");
            File.WriteAllText(scriptPath, Script.ReplaceLineEndings("\n"));
            File.WriteAllText(jobsPath, jobs.ToString());

            foreach (var (index, verdict) in Run(scriptPath, copy, jobsPath, secondInsideRoot, thirdInsideRoot))
            {
                truths[index] = verdict;
            }
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return truths;
    }

    /// <summary>
    /// One field on its way into the jobs file, or a throw. A value carrying the separator or a NUL
    /// would split into fields nobody wrote, and the shell would then judge some other question and
    /// answer it confidently under this case's index. That is the same silence the separator change
    /// closes, so it is a hard failure of the harness rather than a case that quietly means something
    /// else. Nothing in the corpus contains either character today; this is here so that a corpus that
    /// starts to cannot do it unnoticed.
    /// </summary>
    private static string Encodable(ContainmentCase probe, string? value)
    {
        if (value is not null && (value.Contains(Separator, StringComparison.Ordinal) || value.Contains('\0', StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "a case's value holds the jobs file's own framing characters, so the oracle would be asked "
                + $"a different question than the one generated: {probe.Describe()}");
        }

        return value ?? string.Empty;
    }

    private static IEnumerable<(int Index, PlatformTruth Verdict)> Run(
        string script, string copy, string jobs, string? secondInsideRoot, string? thirdInsideRoot)
    {
        var start = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = copy,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(copy);
        start.ArgumentList.Add(jobs);

        // The separator is HANDED to the script rather than spelled again inside it. Two spellings of
        // one framing character can drift apart, and the way this one failed is the way that drift
        // fails: silently, with every case still answered and a family of them answering the wrong
        // question.
        start.ArgumentList.Add(Separator.ToString());

        // Backlog 202: a SECOND region the platform counts as "inside", standing in for this session's
        // own agent memory (which is OUTSIDE the copy, exactly as /data/agent-memory/<key> is). Empty
        // when the caller has none, which the script reads as "no second region".
        start.ArgumentList.Add(secondInsideRoot ?? string.Empty);

        // Backlog 338: a THIRD region, standing in for this session's own saved-tool-output folder
        // (equally OUTSIDE the copy). Empty when the caller has none.
        start.ArgumentList.Add(thirdInsideRoot ?? string.Empty);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("the ground-truth shell would not start");

        // Both pipes are drained concurrently: a shell that fills one while the reader is blocked on
        // the other would hang the suite rather than answer.
        var outputRead = process.StandardOutput.ReadToEndAsync();
        var errorRead = process.StandardError.ReadToEndAsync();
        var output = outputRead.GetAwaiter().GetResult();
        var error = errorRead.GetAwaiter().GetResult();
        if (!process.WaitForExit(milliseconds: 300_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("the ground-truth shell did not finish in five minutes");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"the ground-truth shell failed (exit {process.ExitCode}): {error}");
        }

        var results = new List<(int, PlatformTruth)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length != 2 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var index))
            {
                continue;
            }

            results.Add((index, parts[1].Trim() switch
            {
                "INSIDE" => PlatformTruth.Inside,
                "ESCAPES" => PlatformTruth.Escapes,
                "NOREACH" => PlatformTruth.NoReach,
                "UNDETERMINED" => PlatformTruth.Undetermined,
                _ => PlatformTruth.Unknown,
            }));
        }

        return results;
    }

    /// <summary>
    /// The oracle itself. It is a shell script on purpose: bash and coreutils are a completely separate
    /// implementation of "where does this path go", written by other people, decades before this
    /// boundary existed, and the glob engine in it is a real one rather than a model of one.
    /// </summary>
    private const string Script = """
        # Ground truth for the containment differential. Arguments:
        # <copy> <jobs file> <field separator> <second inside root, or empty> <third inside root, or empty>.
        # Answers, per job: INSIDE, ESCAPES, NOREACH or UNDETERMINED. It never reasons about a path
        # as a string: every question goes to the kernel, to coreutils, or to a real glob engine.
        copy=$1
        jobs=$2
        sep=$3
        second=$4
        third=$5

        shopt -s extglob globstar nullglob dotglob
        cd -P -- "$copy" || exit 9

        declare -A inside=()
        declare -A cache=()
        RESULT=NOREACH

        # How many matches one pattern may produce before the oracle stops claiming to have judged
        # it. Past this it says UNDETERMINED rather than judging a prefix of the answer.
        max_matches=2000

        # Backlog 202 (and 338's own third root): a studio call is contained when it lands inside the
        # copy OR inside this session's own agent memory OR its own saved tool output, so ground
        # truth's "inside" set is the union of all three trees' inodes. A second or third root is
        # optional (most callers of this oracle have neither), and an empty string here means exactly
        # that: find on an empty path would error, so it is skipped.
        load_inside() {
          inside=()
          local i
          while IFS= read -r i; do inside[$i]=1; done < <(find "$copy" -printf '%i\n' 2>/dev/null)
          if [[ -n $second ]]; then
            while IFS= read -r i; do inside[$i]=1; done < <(find "$second" -printf '%i\n' 2>/dev/null)
          fi
          if [[ -n $third ]]; then
            while IFS= read -r i; do inside[$i]=1; done < <(find "$third" -printf '%i\n' 2>/dev/null)
          fi
        }
        load_inside

        # Whether an inode freshly brought into existence (a missing leaf, mkdir'd to see where it
        # landed) is under any of the roots. $inside is loaded once at start and this name did not
        # exist then, so it is not in that map; a fresh "find $copy" catches the common case, and a
        # second and third "find" are tried too so a missing leaf under memory or tool-results is not
        # called ESCAPES purely because the search never looked there.
        inode_is_inside() {
          local ino=$1
          [[ -n $(find "$copy" -inum "$ino" -print -quit 2>/dev/null) ]] && return 0
          [[ -n $second ]] && [[ -n $(find "$second" -inum "$ino" -print -quit 2>/dev/null) ]] && return 0
          [[ -n $third ]] && [[ -n $(find "$third" -inum "$ino" -print -quit 2>/dev/null) ]]
        }

        # The worse of two answers. UNDETERMINED outranks INSIDE deliberately: an answer nobody
        # could work out is not permission to call a call contained, it is a hole, and a hole
        # wearing NOREACH's name is how this differential stayed quiet about a whole family.
        worse() {
          if [[ $1 == ESCAPES || $2 == ESCAPES ]]; then RESULT=ESCAPES
          elif [[ $1 == UNDETERMINED || $2 == UNDETERMINED ]]; then RESULT=UNDETERMINED
          elif [[ $1 == INSIDE || $2 == INSIDE ]]; then RESULT=INSIDE
          else RESULT=NOREACH
          fi
        }

        # Where one PATH argument lands, under EVERY reading a real opener may give it.
        judge_path() {
          local v=$1 lex a b
          if [[ -n ${cache[$v]+set} ]]; then RESULT=${cache[$v]}; return; fi

          # AS WRITTEN: every component handed to the kernel in the order it was spelled.
          judge_one "$v"
          a=$RESULT

          # COMPOSED: ".." collapsed as string arithmetic FIRST, and the RESULT handed to the kernel.
          # That is the order node's path.resolve, python's os.path.abspath and .NET's Path.GetFullPath
          # all work in before they open anything, so it is a reading a real read really gets, and it
          # is not implied by the other one: a collapsed spelling can name a link the as-written walk
          # never reaches. "realpath -s" is coreutils' own lexical pass, so this is still somebody
          # else's implementation rather than a model of the code under test.
          b=NOREACH
          lex=$(realpath -m -s -- "$v" 2>/dev/null)
          if [[ -z $lex ]]; then
            b=UNDETERMINED
          elif [[ $lex != "$v" ]]; then
            judge_one "$lex"
            b=$RESULT
          fi

          worse "$a" "$b"
          cache[$v]=$RESULT
        }

        # One reading of one path. The tool opens what the name points at, so links are followed.
        judge_one() {
          local v=$1 ino why

          ino=$(stat -L -c '%i' -- "$v" 2>/dev/null)
          if [[ -n $ino ]]; then
            if [[ -n ${inside[$ino]+set} ]]; then RESULT=INSIDE; else RESULT=ESCAPES; fi
            return
          fi

          # It did not resolve, and WHY it did not resolve is the whole difference between "an open
          # of this returns nothing, so it leaks nothing" and "the oracle has no idea". Those two
          # must not share a bucket, because only the first is safe to leave unasserted. So the
          # kernel is asked for its reason instead of the answer being reached by exhaustion.
          why=$(LC_ALL=C stat -L -- "$v" 2>&1 >/dev/null)
          case $why in
            *"No such file or directory"* | *"Not a directory"*)
              # Positively absent. An absent leaf is one of the ways a check gets dodged, so ask the
              # kernel to PUT the name there and see where it landed; mkdir never replaces anything,
              # so this cannot damage the fixture or anything outside it.
              if mkdir -- "$v" 2>/dev/null; then
                ino=$(stat -c '%i' -- "$v" 2>/dev/null)
                if [[ -n $ino ]] && inode_is_inside "$ino"; then
                  RESULT=INSIDE
                else
                  RESULT=ESCAPES
                fi
                rmdir -- "$v" 2>/dev/null
              else
                # The name cannot be brought into existence either, so it stays absent and a read
                # of it returns nothing at all.
                RESULT=NOREACH
              fi
              ;;
            *"Too many levels of symbolic links"* | *"File name too long"* | *"Permission denied"*)
              # The kernel REFUSED the name. That is an answer rather than a gap: an opener handed
              # the same name gets the same refusal and reads nothing through it.
              RESULT=NOREACH
              ;;
            *)
              RESULT=UNDETERMINED
              ;;
          esac
        }

        # What one PATTERN reaches, under both of the readings a real glob engine may give it.
        judge_pattern() {
          local dir=$1 pat=$2 a b
          judge_pattern_as_written "$dir" "$pat"
          a=$RESULT
          judge_pattern_composed "$dir" "$pat"
          b=$RESULT
          worse "$a" "$b"
        }

        # AS WRITTEN: expanded by the shell's own glob engine from the directory the tool named,
        # reached PHYSICALLY, so every ".." in the pattern is the kernel's to interpret.
        judge_pattern_as_written() {
          local dir=$1 pat=$2
          local -a matches=()

          if [[ -n $dir ]]; then
            if ! cd -P -- "$dir" 2>/dev/null; then
              cd -P -- "$copy" || exit 9
              # There is nowhere for the expansion to start, so it reaches nothing.
              RESULT=NOREACH
              return
            fi
          fi

          if eval "matches=( $pat )" 2>/dev/null; then
            judge_matches
          else
            RESULT=UNDETERMINED
          fi

          cd -P -- "$copy" || exit 9
        }

        # COMPOSED: the pattern's own ".." collapsed as TEXT first, and the result expanded. This is
        # the same composition an opener performs on a path, and it is what a normalising walker
        # does: "nested/../out/*" matches nothing as written and lists the secrets once collapsed.
        # Nothing here models that walker. Bash performs the brace expansion and quote removal (with
        # globbing off, so "{..,zz}" and ".\." become ".." because a real shell says they do),
        # coreutils collapses the result, and the shell's glob engine expands what that spells.
        judge_pattern_composed() {
          local dir=$1 pat=$2 alt target lex worst=NOREACH
          local -a alts=() matches=()

          set -f
          if ! eval "alts=( $pat )" 2>/dev/null; then
            set +f
            RESULT=UNDETERMINED
            return
          fi
          set +f

          for alt in "${alts[@]}"; do
            if [[ $alt == /* ]]; then
              target=$alt
            elif [[ -n $dir ]]; then
              target=$dir/$alt
            else
              target=$alt
            fi

            lex=$(realpath -m -s -- "$target" 2>/dev/null)
            if [[ -z $lex ]]; then
              RESULT=UNDETERMINED
            else
              matches=()
              if eval "matches=( $lex )" 2>/dev/null; then
                judge_matches
              else
                RESULT=UNDETERMINED
              fi
            fi

            worse "$worst" "$RESULT"
            worst=$RESULT
          done

          RESULT=$worst
        }

        # Where a matched set of names landed. A match is judged WITHOUT following a final link:
        # naming a link leaks nothing, while a pattern that WALKED through one has already left, and
        # lstat of the walked name says so.
        judge_matches() {
          local worst=NOREACH ino seen=0

          if ((${#matches[@]} == 0)); then RESULT=NOREACH; return; fi
          if ((${#matches[@]} > max_matches)); then RESULT=UNDETERMINED; return; fi

          while IFS= read -r ino; do
            seen=$((seen + 1))
            if [[ -n ${inside[$ino]+set} ]]; then
              if [[ $worst == NOREACH ]]; then worst=INSIDE; fi
            else
              worst=ESCAPES
            fi
          done < <(stat -c '%i' -- "${matches[@]}" 2>/dev/null)

          # Every match has to be accounted for. Judging the ones that answered and staying silent
          # about the rest is the same silence this file exists to end. A word the engine left
          # standing because it holds no wildcard to expand (nullglob only drops actual patterns)
          # is the ordinary reason one did not answer, and "it is not there" is a real answer, so
          # they are asked about one at a time rather than the whole batch being written off.
          if ((seen != ${#matches[@]})); then
            judge_matches_singly
            return
          fi

          RESULT=$worst
        }

        # The same question, per match, so that a name which is simply absent is told apart from one
        # the oracle genuinely cannot place.
        judge_matches_singly() {
          local worst=NOREACH m ino why

          for m in "${matches[@]}"; do
            ino=$(stat -c '%i' -- "$m" 2>/dev/null)
            if [[ -n $ino ]]; then
              if [[ -n ${inside[$ino]+set} ]]; then
                if [[ $worst == NOREACH ]]; then worst=INSIDE; fi
              else
                worst=ESCAPES
              fi
              continue
            fi

            why=$(LC_ALL=C stat -c '%i' -- "$m" 2>&1 >/dev/null)
            case $why in
              *"No such file or directory"* | *"Not a directory"* \
                | *"Too many levels of symbolic links"* | *"File name too long"* \
                | *"Permission denied"*)
                # This match reaches nothing, which leaves the worst answer where it was.
                ;;
              *)
                worse "$worst" UNDETERMINED
                worst=$RESULT
                ;;
            esac
          done

          RESULT=$worst
        }

        # Fields are separated by the UNIT SEPARATOR the caller handed in, never a tab. Tab is IFS
        # whitespace: a run of them collapses into one, so a job whose path is EMPTY (a Glob or a Grep
        # that names only a pattern) used to arrive with its pattern in the directory slot, get judged
        # as a nonsense directory and answered NOREACH, which nothing is asserted against. A
        # non-whitespace separator delimits exactly one field per occurrence and keeps empty ones, so
        # no case can be defused by framing.
        while IFS=$sep read -r -d '' idx kind dir pat; do
          case $kind in
            path)
              judge_path "$dir"
              ;;
            pattern)
              located=NOREACH
              if [[ -n $dir ]]; then judge_path "$dir"; located=$RESULT; fi
              if [[ $located == ESCAPES ]]; then
                RESULT=ESCAPES
              else
                judge_pattern "$dir" "$pat"
                # A directory the oracle could not place makes the pattern's answer unsafe too.
                if [[ $located == UNDETERMINED ]]; then worse "$RESULT" UNDETERMINED; fi
              fi
              ;;
            *)
              RESULT=UNDETERMINED
              ;;
          esac
          printf '%s\t%s\n' "$idx" "$RESULT"
        done < "$jobs"
        """;
}
