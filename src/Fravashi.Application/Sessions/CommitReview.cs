using System.Globalization;
using System.Text;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Framework;
using Fravashi.Application.Persistence;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>
/// Who reviews an agent's commit before a person is asked (backlog 243, decided 2026-10-07): a ROLE, not a
/// model. It names a runtime from <see cref="AgentRuntimeCatalog"/> and a model, and it can be switched off, which
/// every commit card then records as "not reviewed".
/// </summary>
/// <param name="Enabled">Whether a review runs at all.</param>
/// <param name="Runtime">The runtime the review conversation runs on.</param>
/// <param name="Model">The model passed to that runtime.</param>
public sealed record CommitReviewerRole(bool Enabled, AgentTool Runtime, string Model)
{
    /// <summary>What the role is called wherever it is named to a person or an agent.</summary>
    public const string RoleName = "commit reviewer";

    /// <summary>"Claude Code/claude-fable-5-1": which runtime and model filled the role.</summary>
    public string Filler =>
        (AgentRuntimeCatalog.All.FirstOrDefault(d => d.Tool == Runtime)?.DisplayName ?? Runtime.ToString()) + "/" + Model;
}

/// <summary>
/// The commit reviewer role as the settings table holds it (Settings &gt; Agents &gt; Commit reviewer). It is
/// generic over <see cref="AgentRuntimeCatalog"/> on purpose (the owner: "we might add codex and copilot in the
/// future ... could we ask the user to provide the verifier in the settings? while we have one as default"): a runtime
/// the catalog offers can fill it, one it no longer offers falls back to the default rather than throwing, and a
/// runtime returning from docs/v2.md section 1 appears in the choice with nothing here changing.
/// </summary>
public static class CommitReviewerSetting
{
    /// <summary>"true" or "false". Absent or unreadable is the default, ON.</summary>
    public const string EnabledKey = "CommitReviewer:Enabled";

    /// <summary>The runtime's <see cref="AgentTool"/> name.</summary>
    public const string RuntimeKey = "CommitReviewer:Runtime";

    /// <summary>The model id passed to that runtime.</summary>
    public const string ModelKey = "CommitReviewer:Model";

    /// <summary>
    /// The shipped default: ON, the first runtime the catalog offers (this version ships one), on its most capable
    /// model. A runtime whose list marks no most capable model gets its first one, and the CLI's own default when its
    /// list is empty.
    /// </summary>
    public static CommitReviewerRole Default => DefaultFor(AgentRuntimeCatalog.Tools[0]);

    /// <summary>The default for one runtime: on, its most capable model.</summary>
    public static CommitReviewerRole DefaultFor(AgentTool runtime) =>
        new(true, runtime, DefaultModelFor(runtime));

    /// <summary>The model the role starts on for a runtime.</summary>
    public static string DefaultModelFor(AgentTool runtime) =>
        AgentModelCatalog.MostCapable(runtime)?.Id
        ?? AgentModelCatalog.For(runtime).FirstOrDefault()?.Id
        ?? "default";

    /// <summary>The runtimes that can fill the role: every one the catalog offers, in its order.</summary>
    public static IReadOnlyList<AgentRuntimeDescriptor> Runtimes => AgentRuntimeCatalog.All;

    /// <summary>The role as stored, with every missing or unusable part replaced by the default's.</summary>
    public static CommitReviewerRole Read(IConfigurationStore config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var enabled = !bool.TryParse(config.Get(EnabledKey), out var on) || on;

        // Read THROUGH the catalog: a stored runtime this version does not offer (a retired one, or a typo) is the
        // default's runtime, never an exception and never a runtime nothing can start.
        var offered = Enum.TryParse<AgentTool>(config.Get(RuntimeKey), ignoreCase: true, out var stored)
            && AgentRuntimeCatalog.Tools.Contains(stored);
        var runtime = offered ? stored : Default.Runtime;

        // A model stored for ANOTHER runtime would be passed to this one, so the model is kept only together with the
        // runtime it was stored with. A blank one is the runtime's default.
        var model = config.Get(ModelKey)?.Trim();
        if (string.IsNullOrEmpty(model) || !offered)
        {
            model = DefaultModelFor(runtime);
        }

        return new CommitReviewerRole(enabled, runtime, model);
    }

    /// <summary>Stores the role.</summary>
    public static void Write(IConfigurationStore config, CommitReviewerRole role)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(role);

        config.Set(EnabledKey, role.Enabled ? "true" : "false");
        config.Set(RuntimeKey, role.Runtime.ToString());
        config.Set(ModelKey, string.IsNullOrWhiteSpace(role.Model) ? DefaultModelFor(role.Runtime) : role.Model.Trim());
    }
}

/// <summary>How a review ended.</summary>
public enum CommitReviewOutcome
{
    /// <summary>The reviewer found nothing to change. The person is asked, with its summary on the card.</summary>
    Passed,

    /// <summary>The reviewer found something. The commit is refused with the findings and no person is asked.</summary>
    Failed,

    /// <summary>The role is off. The person is asked, and the card says the commit was not reviewed.</summary>
    Off,

    /// <summary>The review ran past its time or token bound. The person is asked, and the card says so.</summary>
    TimedOut,

    /// <summary>The review could not run or ended without a verdict. The person is asked, and the card says why.</summary>
    Errored,
}

/// <summary>One thing the reviewer says must change before the commit.</summary>
/// <param name="File">The repository-relative file.</param>
/// <param name="Line">The line, when there is one.</param>
/// <param name="Rule">The rule, ADR or acceptance criterion it breaks.</param>
/// <param name="Change">What to change.</param>
/// <param name="Summary">What is wrong, in a sentence.</param>
/// <param name="AnswersFindingNumber">
/// Work item #341: set when this finding is the reviewer KEEPING a finding the builder disputed, naming the
/// number (from the previous review's refusal) it addresses. Null for an ordinary, undisputed finding.
/// </param>
public sealed record CommitReviewFinding(
    string File, int? Line, string Rule, string Change, string Summary, int? AnswersFindingNumber = null)
{
    /// <summary>
    /// Work item #341: whether <paramref name="candidate"/> is plausibly the SAME underlying problem as
    /// <paramref name="original"/>, used both by <c>AgentCommitReviewer.KeptIndex</c> (to read a reviewer's
    /// un-tagged re-listing, in its OWN new findings list, as a keep of the finding it was just told the builder
    /// disputed) and by <c>AgentApprovalBroker</c>'s cross-round carry-forward (<c>SettledFindings</c>): the ONE
    /// shared definition, because a looser one for either caller has the same failure shape item #341 exists to
    /// remove. File, line (both present and equal, or both absent) AND rule must ALL agree: a shared rule alone
    /// is not enough (a string such as "CLAUDE.md" or "the item's acceptance" is common and repeated across
    /// unrelated findings), and neither is a shared file-and-line alone (a reviewer can withdraw the disputed
    /// finding and list a genuinely NEW, unrelated one at the very same line in the SAME round; reading that as a
    /// keep would tell the builder its answer was rejected, quote the new finding's own summary as if it were the
    /// reviewer's reason, and make the new finding impossible to ever dispute).
    /// </summary>
    public static bool MatchesSameDispute(CommitReviewFinding original, CommitReviewFinding candidate)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(candidate);

        return string.Equals(original.File, candidate.File, StringComparison.Ordinal)
            && original.Line == candidate.Line
            && string.Equals(original.Rule, candidate.Rule, StringComparison.Ordinal);
    }
}

/// <summary>
/// Work item #341: whether the reviewer, told a builder disputed one of its findings, still thinks it holds.
/// </summary>
public enum CommitReviewAnswerVerdict
{
    /// <summary>The reviewer agreed the finding does not hold and dropped it; it no longer blocks the commit.</summary>
    Withdrawn,

    /// <summary>The reviewer re-asserts the finding; it still blocks the commit.</summary>
    Kept,

    /// <summary>
    /// The review that read this answer ended with no real verdict (a timeout, a token bound, no document, an
    /// unreadable one): neither withdrawn nor kept, because nobody actually decided. A person is asked either
    /// way (that outcome always asks), and this is how the card and the transcript say the dispute is still
    /// open rather than implying either side settled it.
    /// </summary>
    Undecided,
}

/// <summary>
/// Work item #341: one finding the builder disputed, sent to the NEXT review alongside its own content so the
/// reviewer can decide it on its merits rather than from the number alone.
/// </summary>
/// <param name="FindingNumber">The finding's number in the review that made it (the one the builder is answering).</param>
/// <param name="Finding">That finding's own content, read from the review that made it.</param>
/// <param name="BuilderReason">
/// Why the builder believes it is wrong. The BUILDER's own text, read by the next review as a claim to check
/// against the work item, the plan and the code, never as an instruction, whatever it says.
/// </param>
public sealed record CommitReviewAnsweredFinding(int FindingNumber, CommitReviewFinding Finding, string BuilderReason)
{
    /// <summary>The most characters a builder's answer may carry; a longer one is refused, not cut.</summary>
    public const int MaxReasonLength = 500;
}

/// <summary>
/// Work item #341: the reviewer's verdict on one answered finding, recorded on the review that decided it so a
/// person (and the building agent) can see the disagreement, whichever way it went.
/// </summary>
/// <param name="FindingNumber">The finding's number in the review that made it.</param>
/// <param name="Finding">That finding's own content.</param>
/// <param name="BuilderReason">Why the builder believed it was wrong.</param>
/// <param name="Verdict">Whether the reviewer withdrew it or kept it.</param>
/// <param name="ReviewerReason">The reviewer's own reason for keeping it, when it was kept.</param>
/// <param name="KeptAsFindingNumber">
/// Work item #341: when <paramref name="Verdict"/> is Kept, the number THIS SAME finding now carries in the
/// new review's own findings list, so a kept finding can be refused a second answer under its new number too,
/// not only its original one. A LATER round recognises it again only for as long as it keeps re-listing it at
/// the same file, line and rule (<see cref="CommitReviewFinding.MatchesSameDispute"/>). A finding that differs
/// in any of the three, or whose answer ended Withdrawn or Undecided, is a fresh dispute, answerable on its own
/// number. Null otherwise.
/// </param>
public sealed record CommitReviewAnswerOutcome(
    int FindingNumber,
    CommitReviewFinding Finding,
    string BuilderReason,
    CommitReviewAnswerVerdict Verdict,
    string? ReviewerReason,
    int? KeptAsFindingNumber = null);

/// <summary>Where <see cref="CommitReviewAnswerOutcome"/> and its Undecided mapping are built from.</summary>
public static class CommitReviewAnswerOutcomes
{
    /// <summary>
    /// Work item #341: the Undecided verdict for every answer in <paramref name="answers"/>, or null when
    /// there were none. The ONE place this mapping is built, so every path that reads an answer but ends
    /// with no real verdict (a timeout, a token bound, no document, an unreadable one, a review that could
    /// not even start, a reviewer that throws, the role being off) reports it the same way, rather than
    /// some of those paths silently dropping the dispute because they forgot to build it themselves.
    /// </summary>
    public static IReadOnlyList<CommitReviewAnswerOutcome>? Undecided(IReadOnlyList<CommitReviewAnsweredFinding>? answers) =>
        answers is not { Count: > 0 }
            ? null
            : [.. answers.Select(a => new CommitReviewAnswerOutcome(a.FindingNumber, a.Finding, a.BuilderReason, CommitReviewAnswerVerdict.Undecided, null))];
}

/// <summary>
/// What a commit review found, and everything the studio records about it (backlog 243 and 244: the verdict, the
/// duration and the tokens, on the BUILDER's conversation, so a host can count findings made, missed and false).
/// </summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Role">The role as it was when the review ran.</param>
/// <param name="Summary">The reviewer's own summary, on a pass (and on a fail, when it gave one).</param>
/// <param name="Findings">What must change, on a fail.</param>
/// <param name="Problem">Why there is no verdict, on a timeout or an error.</param>
/// <param name="Conversation">The review conversation, kept like any conversation; null when none started.</param>
/// <param name="Duration">How long the review took.</param>
/// <param name="Tokens">Billed tokens (input and output, cache reads excluded).</param>
/// <param name="CostUsd">What the runtime reported it cost.</param>
/// <param name="NotRunInContainer">
/// Backlog 295: test classes the studio found, from the test sources themselves, not run in the container the
/// review ran in, each as "ClassName (reason)". Computed by the studio, never by the reviewer model, and beside
/// the verdict: it never changes <see cref="Outcome"/>. Null or empty when nothing was found or nothing applies.
/// </param>
/// <param name="AnswerOutcomes">
/// Work item #341: the reviewer's verdict on each finding the builder disputed in the commit call that led to
/// this review. Null or empty when the builder disputed nothing this round. Recording this, never only
/// inferring it from the findings list, is what lets a disagreement neither side gives up stay visible: on a
/// <see cref="CommitReviewOutcome.Passed"/> every disputed finding is Withdrawn (nothing is listed to keep it);
/// on a <see cref="CommitReviewOutcome.Failed"/> this is what tells a KEPT dispute, with the reviewer's own
/// reason, apart from a brand new finding the builder never raised; on every other outcome (a timeout, an
/// error, the role off) nothing was decided, so each disputed finding is Undecided rather than silently
/// dropped.
/// </param>
public sealed record CommitReview(
    CommitReviewOutcome Outcome,
    CommitReviewerRole Role,
    string? Summary = null,
    IReadOnlyList<CommitReviewFinding>? Findings = null,
    string? Problem = null,
    AgentSessionId? Conversation = null,
    TimeSpan Duration = default,
    long Tokens = 0,
    decimal CostUsd = 0m,
    IReadOnlyList<string>? NotRunInContainer = null,
    IReadOnlyList<CommitReviewAnswerOutcome>? AnswerOutcomes = null)
{
    /// <summary>The most characters of findings a refusal carries; the rest is said to be cut.</summary>
    public const int MaxReasonLength = 8_000;

    /// <summary>What was not run in the container, never null.</summary>
    public IReadOnlyList<string> AllNotRunInContainer => NotRunInContainer ?? [];

    /// <summary>The verdict on every finding the builder disputed this round, never null (work item #341).</summary>
    public IReadOnlyList<CommitReviewAnswerOutcome> AllAnswerOutcomes => AnswerOutcomes ?? [];

    /// <summary>
    /// "N answered finding(s): W withdrawn, K kept, U undecided." appended to the card and the transcript line
    /// when the builder disputed anything this round, nothing otherwise (work item #341). "Undecided" is never
    /// silently dropped: a review that read an answer but gave no real verdict still says so, rather than
    /// reading as though nobody had disputed anything.
    /// </summary>
    private string AnswerSuffix
    {
        get
        {
            if (AllAnswerOutcomes.Count == 0)
            {
                return string.Empty;
            }

            var withdrawn = AllAnswerOutcomes.Count(a => a.Verdict == CommitReviewAnswerVerdict.Withdrawn);
            var kept = AllAnswerOutcomes.Count(a => a.Verdict == CommitReviewAnswerVerdict.Kept);
            var undecided = AllAnswerOutcomes.Count - withdrawn - kept;
            return $" {AllAnswerOutcomes.Count.ToString(CultureInfo.InvariantCulture)} answered finding(s): "
                + $"{withdrawn.ToString(CultureInfo.InvariantCulture)} withdrawn, {kept.ToString(CultureInfo.InvariantCulture)} kept, "
                + $"{undecided.ToString(CultureInfo.InvariantCulture)} undecided.";
        }
    }

    /// <summary>
    /// "Not run in the container: X (reason); Y (reason)." appended to the card and the transcript line when
    /// anything was found, nothing otherwise.
    /// </summary>
    private string NotRunSuffix => AllNotRunInContainer.Count == 0
        ? string.Empty
        : " Not run in the container: " + string.Join("; ", AllNotRunInContainer) + ".";

    /// <summary>A review the role being off stood in for.</summary>
    /// <param name="answers">
    /// Work item #341: findings the builder disputed on the very call the role being off answered, if any.
    /// The role being off means no review ran at all, so none of them was either withdrawn or kept; recorded
    /// Undecided so the dispute is not silently dropped just because the role happened to be off this time.
    /// </param>
    public static CommitReview NotReviewed(CommitReviewerRole role, IReadOnlyList<CommitReviewAnsweredFinding>? answers = null) =>
        new(CommitReviewOutcome.Off, role, AnswerOutcomes: CommitReviewAnswerOutcomes.Undecided(answers));

    /// <summary>What must change, never null.</summary>
    public IReadOnlyList<CommitReviewFinding> AllFindings => Findings ?? [];

    /// <summary>
    /// True when the commit goes to a person. Only a FAIL stops it short of one; a review that could not decide is
    /// never a pass and never a refusal, so the person decides and is told the review did not.
    /// </summary>
    public bool AsksPerson => Outcome != CommitReviewOutcome.Failed;

    /// <summary>The headline the commit card shows.</summary>
    public string CardLine => Outcome switch
    {
        CommitReviewOutcome.Passed => $"Reviewed by {CommitReviewerRole.RoleName} ({Role.Filler}): pass",
        CommitReviewOutcome.Off =>
            "Not reviewed: the commit reviewer role is off in Settings > Agents > Commit reviewer.",
        CommitReviewOutcome.TimedOut =>
            $"Not reviewed: the {CommitReviewerRole.RoleName} ({Role.Filler}) gave no verdict. {Problem} Read the change yourself before approving.",
        CommitReviewOutcome.Errored =>
            $"Not reviewed: the {CommitReviewerRole.RoleName} ({Role.Filler}) could not finish. {Problem} Read the change yourself before approving.",
        _ => $"Reviewed by {CommitReviewerRole.RoleName} ({Role.Filler}): fail",
    } + AnswerSuffix + NotRunSuffix;

    /// <summary>
    /// The sentence the building agent reads as its refused <c>git_commit</c>'s result (#220's channel): every finding
    /// with its file, line, rule and change, and what to do next.
    /// </summary>
    public string DenialReason
    {
        get
        {
            var text = new StringBuilder();
            text.Append("The ").Append(CommitReviewerRole.RoleName).Append(" (").Append(Role.Filler).Append(") found ")
                .Append(AllFindings.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" problem(s) with this commit, so nothing was committed and no person was asked. Fix each one, ")
                .Append("then call git_commit again; a new review runs then.");
            if (!string.IsNullOrWhiteSpace(Summary))
            {
                text.Append("\nReviewer's summary: ").Append(Summary.Trim());
            }

            // Work item #341: the builder's own answer, read back beside its verdict, before the numbered list
            // of what still blocks, so a kept dispute is never confused with a brand new finding the builder
            // has not yet seen.
            foreach (var outcome in AllAnswerOutcomes)
            {
                text.Append("\nYour answer to finding ").Append(outcome.FindingNumber.ToString(CultureInfo.InvariantCulture)).Append(": ");
                text.Append(outcome.Verdict switch
                {
                    CommitReviewAnswerVerdict.Withdrawn => "accepted; that finding was withdrawn.",
                    CommitReviewAnswerVerdict.Kept => "not accepted; the reviewer kept it"
                        + (outcome.ReviewerReason is { Length: > 0 } reviewerReason ? ": " + reviewerReason.Trim() : "."),
                    _ => "undecided; the review gave no verdict on it.",
                });
            }

            var number = 0;
            foreach (var finding in AllFindings)
            {
                number++;
                text.Append('\n').Append(number.ToString(CultureInfo.InvariantCulture)).Append(". ").Append(finding.File);
                if (finding.Line is { } line)
                {
                    text.Append(':').Append(line.ToString(CultureInfo.InvariantCulture));
                }

                text.Append(": ").Append(finding.Summary.Trim())
                    .Append(" Breaks: ").Append(finding.Rule.Trim())
                    .Append(" Change: ").Append(finding.Change.Trim());
            }

            if (text.Length <= MaxReasonLength)
            {
                return text.ToString();
            }

            const string Cut = "\n(The rest of the findings were cut; read the review conversation for all of them.)";
            return text.ToString(0, MaxReasonLength - Cut.Length) + Cut;
        }
    }

    /// <summary>
    /// The one line written on the BUILDER's conversation for every review (backlog 244): verdict, role, duration,
    /// tokens, cost, finding count and the review conversation, which is what the findings are counted against.
    /// </summary>
    public string TranscriptNote
    {
        get
        {
            var verdict = Outcome switch
            {
                CommitReviewOutcome.Passed => "pass",
                CommitReviewOutcome.Failed => "fail",
                CommitReviewOutcome.Off => "not reviewed (the role is off)",
                CommitReviewOutcome.TimedOut => "no verdict (bound reached)",
                _ => "no verdict (error)",
            };

            if (Outcome == CommitReviewOutcome.Off)
            {
                return "Commit review: " + verdict + "." + AnswerSuffix + NotRunSuffix;
            }

            var text = new StringBuilder("Commit review: ").Append(verdict)
                .Append(", by the ").Append(CommitReviewerRole.RoleName).Append(" (").Append(Role.Filler).Append("), ")
                .Append(((int)Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture)).Append(" s, ")
                .Append(Tokens.ToString("N0", CultureInfo.InvariantCulture)).Append(" tokens, $")
                .Append(CostUsd.ToString("0.00", CultureInfo.InvariantCulture)).Append(", ")
                .Append(AllFindings.Count.ToString(CultureInfo.InvariantCulture)).Append(" finding(s)");
            if (Conversation is { } conversation)
            {
                text.Append(", review conversation ").Append(conversation.Value.ToString("D"));
            }

            if (Problem is { Length: > 0 } problem)
            {
                text.Append(". ").Append(problem);
            }

            return text.Append('.').ToString() + AnswerSuffix + NotRunSuffix;
        }
    }
}

/// <summary>
/// What the reviewer is asked about: the arguments of the agent commit being reviewed (a <c>git_commit</c>, or since
/// backlog 250 an <c>attempt_commit</c>, whose summary is its message and which never names paths).
/// </summary>
/// <param name="Message">The commit message the agent gave.</param>
/// <param name="Paths">The paths it named, or null when it named none (then: what the session was approved to change).</param>
/// <param name="Answers">
/// Work item #341: findings the builder disputed from its latest failed review, each with its own content and
/// the builder's reason, resolved and validated by the approval broker before this request is built. Null or
/// empty when the builder disputed nothing.
/// </param>
public sealed record CommitReviewRequest(
    string? Message, IReadOnlyList<string>? Paths, IReadOnlyList<CommitReviewAnsweredFinding>? Answers = null);

/// <summary>
/// Reviews an agent's commit with a second agent before a person is asked (backlog 243). The approval broker calls
/// it for every agent commit (<c>git_commit</c> or <c>attempt_commit</c>) that would otherwise reach a person, and it never throws: every way a review can
/// fail to decide comes back as <see cref="CommitReviewOutcome.TimedOut"/> or <see cref="CommitReviewOutcome.Errored"/>,
/// which still asks the person.
/// </summary>
public interface ICommitReviewer
{
    /// <summary>The role as it stands now, read at each call so a change in Settings applies to the next commit.</summary>
    CommitReviewerRole Role { get; }

    /// <summary>Runs the review for the building session's commit.</summary>
    Task<CommitReview> ReviewAsync(
        AgentSessionId builder, CommitReviewRequest request, CancellationToken cancellationToken = default);
}
