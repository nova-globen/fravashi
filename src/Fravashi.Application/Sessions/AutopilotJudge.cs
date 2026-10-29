using System.Text.Json.Nodes;

namespace Fravashi.Application.Sessions;

/// <summary>
/// A "System One" model answering fixed yes/no (noul) questions about one autopilot command (ADR 0031
/// decision 8). This is the studio's own copy of the broker's <c>ISystemOneModel</c> shape
/// (<c>src/Fravashi.Broker/PromptAudit/ISystemOneModel.cs</c>), narrowed to noul answers only, because
/// <c>Fravashi.Broker</c> stays a standalone deployable and this studio-side judge takes no project
/// reference to it. Replacing TypeSafe is a new adapter against this same port, exactly as the
/// using-typesafe skill describes for the broker's own copy.
/// <para>
/// An adapter's contract: send <c>state</c> and <c>questions</c> as given, and answer every question with
/// a value from 0 (no) to 1 (yes). Anything else (unreachable, a refusal, an unreadable answer, a question
/// left unanswered) is an <see cref="AutopilotJudgeUnavailableException"/>, never a guess: the caller's
/// whole job is deciding what happens when there is no judgement.
/// </para>
/// </summary>
public interface IAutopilotJudgeModel
{
    /// <summary>A short, secret-free name for the record, e.g. <c>typesafe</c>.</summary>
    string Name { get; }

    /// <summary>Asks every question in <paramref name="questions"/> about <paramref name="state"/>.</summary>
    Task<IReadOnlyDictionary<string, double>> EvaluateAsync(
        JsonObject state, JsonObject questions, CancellationToken cancellationToken);
}

/// <summary>
/// There is no judgement to be had: the model could not be reached, refused, answered late, or answered in
/// a way that cannot be read. <see cref="Reason"/> is short and secret-free, fit for the attention record;
/// no message ever carries the service's key or the command.
/// </summary>
public sealed class AutopilotJudgeUnavailableException(string reason, Exception? inner = null)
    : Exception($"The autopilot's judge gave no usable answer ({reason}).", inner)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// Where the judge's own key lives: the studio's <c>ISecretStore</c>, under this fixed reference, stored the
/// same way every provider credential is (never an environment variable: this is the studio, not the
/// broker, so there is no baked config for it). This slice stores and reads it through that one interface
/// only; a Settings UI control to enter it is deliberately not built here (wiki @115 records why) and is a
/// fast-follow once a dispatch form exists to need it from. No key stored means the adapter's own call
/// throws <c>no_key</c>, which <see cref="AutopilotCommandJudge"/> turns into
/// <see cref="AutopilotJudgeVerdict.Unavailable"/>, which asks.
/// </summary>
public static class AutopilotJudgeSecret
{
    public const string Reference = "autopilot:judge:typesafe";
}

/// <summary>
/// The fixed, atomic questions the judge is asked about one command (ADR 0031 decision 8), named so the
/// weighing in <see cref="AutopilotCommandJudge"/> can read them without caring about their order.
/// </summary>
public static class AutopilotJudgeQuestions
{
    /// <summary>Does it write anywhere outside the current working folder?</summary>
    public const string WritesOutsideCopy = "writes_outside_copy";

    /// <summary>Does it make or accept a network connection?</summary>
    public const string ReachesNetwork = "reaches_network";

    /// <summary>Does it read a credential, API key, token or key file?</summary>
    public const string ReadsCredentialOrKey = "reads_credential_or_key";

    /// <summary>Does it delete or overwrite anything other than the project's own build output?</summary>
    public const string DeletesOrOverwrites = "deletes_or_overwrites";

    /// <summary>Does it change git state itself, rather than through the studio's own git tools?</summary>
    public const string ChangesGitState = "changes_git_state";

    /// <summary>Does it install or download software, a package or a dependency?</summary>
    public const string InstallsSoftware = "installs_software";

    /// <summary>Does it start a server or leave a process running in the background?</summary>
    public const string StartsBackgroundProcess = "starts_background_process";

    /// <summary>Does it hide what it does through encoding, obfuscation or indirection?</summary>
    public const string HidesWhatItDoes = "hides_what_it_does";

    /// <summary>Every question id, in the order they are asked.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        WritesOutsideCopy, ReachesNetwork, ReadsCredentialOrKey, DeletesOrOverwrites,
        ChangesGitState, InstallsSoftware, StartsBackgroundProcess, HidesWhatItDoes,
    ];

    private static readonly IReadOnlyDictionary<string, (string Question, string Focus)> Wording =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [WritesOutsideCopy] = (
                "Does running `command` write anywhere outside the current working folder?",
                "Look for an absolute path elsewhere, a parent-directory traversal, or a redirection to one."),
            [ReachesNetwork] = (
                "Does running `command` make or accept a network connection?",
                "Look for a URL, a download, an upload, or a tool whose job is talking to another host."),
            [ReadsCredentialOrKey] = (
                "Does running `command` read a credential, API key, token or key file?",
                "Look for a secret store, an environment variable holding one, or a file named like a key or credential."),
            [DeletesOrOverwrites] = (
                "Does running `command` delete or overwrite anything other than the project's own build output "
                + "(bin/, obj/, node_modules/, test output)?",
                "A plain build or test run's own output does not count; anything else deleted or overwritten does."),
            [ChangesGitState] = (
                "Does running `command` change git state itself, rather than through the studio's own git tools?",
                "Look for git invoked directly, or anything that stages, commits, resets or rewrites history by hand."),
            [InstallsSoftware] = (
                "Does running `command` install or download software, a package or a dependency?",
                "Look for a package manager, a fetched installer, or anything adding to what is already on disk."),
            [StartsBackgroundProcess] = (
                "Does running `command` start a server or leave a process running in the background?",
                "Look for a listener, a daemon, a detached process, or anything that does not simply finish."),
            [HidesWhatItDoes] = (
                "Does running `command` hide what it does through encoding, obfuscation or indirection?",
                "Look for base64, eval, a fetched script piped to a shell, or anything built to be hard to read."),
        };

    /// <summary>The question set as the judge's JSON shape, built fresh each time so a caller may not mutate it.</summary>
    public static JsonObject Load()
    {
        var questions = new JsonObject();
        foreach (var id in All)
        {
            var (question, focus) = Wording[id];
            questions[id] = new JsonObject
            {
                ["type"] = "noul",
                ["instructions"] = new JsonObject { ["question"] = question, ["focus"] = focus },
            };
        }

        return questions;
    }
}

/// <summary>How the judge decided (ADR 0031 decision 8). Only <see cref="Safe"/> runs the command unattended.</summary>
public enum AutopilotJudgeVerdict
{
    /// <summary>Every question came back below the safe threshold. The command runs.</summary>
    Safe,

    /// <summary>At least one question did not come back clearly safe. A person is asked.</summary>
    Unclear,

    /// <summary>There was no judgement to be had (no key, unreachable, a timeout, an unreadable answer). A person is asked.</summary>
    Unavailable,
}

/// <summary>One judgement of one command, with enough to record on the attention line but no secret and no transcript.</summary>
/// <param name="Verdict">What was decided.</param>
/// <param name="Reason">A short, secret-free reason.</param>
/// <param name="Answers">Every question's answer, when there was one.</param>
public sealed record AutopilotJudgeOutcome(
    AutopilotJudgeVerdict Verdict, string Reason, IReadOnlyDictionary<string, double>? Answers = null)
{
    public static AutopilotJudgeOutcome Unavailable(string reason) => new(AutopilotJudgeVerdict.Unavailable, reason);
}

/// <summary>
/// Configuration for <see cref="AutopilotCommandJudge"/>: the threshold a noul answer must stay below to
/// count as a clear "no", and how long one judgement may take.
/// </summary>
public sealed class AutopilotJudgeOptions
{
    /// <summary>
    /// A noul answer below this counts as a clear "no" for that question. The judge never refuses on its
    /// own account, only decides between running unattended and asking a person, and a down or unsure
    /// judge must ask MORE, never less (ADR 0031 decision 9), so this is set low: 0.1, recorded on wiki @115
    /// with its reasoning. That reasoning is a starting value, not a measurement: the broker's own
    /// prompt-audit calibration (the using-typesafe skill's table, jev-1.13.0, 2026-09-23) put a benign
    /// PROMPT's category nouls at 0.01 to 0.02 against a DIFFERENT question set, so 0.1 leaves headroom
    /// without being close to a real "maybe"; no sample of these eight command questions has been run
    /// against the real model yet, which a live tier (matching the broker's own PromptAuditLiveTests
    /// pattern) would need to do before this threshold ships to a real run.
    /// </summary>
    public double SafeBelow { get; set; } = 0.1;

    /// <summary>How long one judgement may take, retries included.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Judges one autopilot command against the fixed question set (ADR 0031 decision 8). What leaves the
/// machine is the command line, the workspace folder's NAME and the item's title: never file content or
/// the transcript. Since backlog 324 (ADR 0031 decision 8's addendum) <c>AgentApprovalBroker</c>, the one
/// caller of <see cref="JudgeAsync"/>, rewrites every occurrence of the session's own copy root that
/// stands as its own shell word to <c>.</c> before calling it, and withholds the call to
/// <see cref="JudgeAsync"/> entirely, asking a person instead, when an occurrence fused to something else
/// (a brace expansion, a flag glued to its value, a colon list, a glob) survives the rewrite. So a command
/// AgentApprovalBroker hands this class never contains the root's literal spelling; this class itself does
/// not check, so a different caller that skipped that step would not be guarded against. Since backlog 326
/// (decision 8's second addendum) it also removes a plain <c>&gt;</c>/<c>&gt;&gt;</c>, a leading
/// file-descriptor digit run, or an <c>&amp;&gt;</c>/<c>&amp;&gt;&gt;</c> redirection whose target is
/// exactly <c>/dev/null</c> before calling it, so that redirection is no longer part of the text this
/// class is asked about (that stripping rule, <c>DevNullRedirects</c>, moved to the Domain project on backlog
/// 337, shared since then with <c>AgentReadRedirect</c>); #326's own v0.9.87 live check measured the judge
/// clearing such a read. Since backlog 337, a pure read ending in a /dev/null discard never reaches this
/// class at all: <c>AgentReadRedirect</c> answers it first. The stripping above still matters for a command
/// <c>AgentReadRedirect</c> does not recognise as a read (<c>npm install 2&gt;/dev/null</c>, say).
/// <para>
/// A model that is not configured (<paramref name="model"/> is null, e.g. no key stored) makes every
/// judgement <see cref="AutopilotJudgeVerdict.Unavailable"/>, which asks a person: a judge that is down
/// asks more, never less.
/// </para>
/// </summary>
public sealed class AutopilotCommandJudge(IAutopilotJudgeModel? model, AutopilotJudgeOptions options, TimeProvider clock)
{
    private readonly AutopilotJudgeOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>True when a judge is configured, so a command can actually be asked about.</summary>
    public bool IsOn => model is not null;

    /// <summary>Judges one command. Never throws: every failure to decide comes back as <see cref="AutopilotJudgeVerdict.Unavailable"/>.</summary>
    public async Task<AutopilotJudgeOutcome> JudgeAsync(
        string command, string workspaceName, string itemTitle, CancellationToken cancellationToken)
    {
        if (model is null)
        {
            return AutopilotJudgeOutcome.Unavailable("no_judge_configured");
        }

        var state = new JsonObject
        {
            ["command"] = command,
            ["workspace"] = workspaceName,
            ["item_title"] = itemTitle,
        };

        using var deadline = new CancellationTokenSource(_options.Timeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var answers = await model.EvaluateAsync(state, AutopilotJudgeQuestions.Load(), linked.Token)
                .ConfigureAwait(false);
            return Assess(answers);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AutopilotJudgeOutcome.Unavailable("timeout");
        }
        catch (AutopilotJudgeUnavailableException ex)
        {
            return AutopilotJudgeOutcome.Unavailable(ex.Reason);
        }
    }

    private AutopilotJudgeOutcome Assess(IReadOnlyDictionary<string, double> answers)
    {
        var flagged = new List<string>();
        foreach (var id in AutopilotJudgeQuestions.All)
        {
            if (!answers.TryGetValue(id, out var value))
            {
                return AutopilotJudgeOutcome.Unavailable("answer_missing_" + id);
            }

            if (value >= _options.SafeBelow)
            {
                flagged.Add(id);
            }
        }

        return flagged.Count == 0
            ? new AutopilotJudgeOutcome(AutopilotJudgeVerdict.Safe, "judge_safe", answers)
            : new AutopilotJudgeOutcome(
                AutopilotJudgeVerdict.Unclear, "judge_unclear_" + string.Join(",", flagged), answers);
    }
}
