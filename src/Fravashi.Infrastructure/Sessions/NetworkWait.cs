using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Network;
using Fravashi.Application.Time;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>What a waiting conversation is waiting for (backlog 235).</summary>
public enum WaitReason
{
    /// <summary>The network: the model call failed on it (backlog 229).</summary>
    Network,

    /// <summary>
    /// A fresh broker token: the broker refused the model call because the token the agent process started with had
    /// expired (backlog 235, plan @60). Only where the broker carries the runtime.
    /// </summary>
    Credentials,
}

/// <summary>
/// A conversation waiting for the network (backlog 229, plan @55): its turn, or its process, failed on the network,
/// and the studio continues it by itself once the model's host answers again. Held on the live session so every
/// surface showing it says the same thing. Since backlog 235 the same wait also covers a turn the broker refused for an
/// expired token (<see cref="WaitReason.Credentials"/>): same banner, same Stop and Retry now, same bounds.
/// </summary>
/// <param name="Since">When the failure was seen.</param>
/// <param name="Failure">What failed, in slice A's kinds (<see cref="NetworkFailure.None"/> for a credential wait).</param>
/// <param name="Host">The host the probe asks about: the one the agent CLI really calls.</param>
/// <param name="Attempt">Which automatic continue this would be, from 1.</param>
/// <param name="ProcessEnded">True when the agent process ended with the failure, so continuing means resuming it.</param>
/// <param name="Probe">The probe's last answer, or null before the first one.</param>
/// <param name="Reason">What the conversation is waiting for.</param>
/// <param name="Credential">The last mint attempt's words while waiting for a token (never the token), or null.</param>
/// <param name="RefusedTokenId">
/// The id (never the value) of the token the broker refused, so the wait asks for a DIFFERENT one; null when no token
/// was refused (a network wait whose token simply needs renewing on the way back).
/// </param>
/// <param name="CredentialOutage">
/// The broker outage behind <paramref name="Credential"/> when the last mint could not reach the broker, so the page can
/// say its retry time in the person's zone (backlog 246); null otherwise.
/// </param>
public sealed record NetworkWait(
    DateTimeOffset Since,
    NetworkFailure Failure,
    string Host,
    int Attempt,
    bool ProcessEnded,
    NetworkReachabilityState? Probe = null,
    WaitReason Reason = WaitReason.Network,
    string? Credential = null,
    string? RefusedTokenId = null,
    BrokerOutage? CredentialOutage = null)
{
    /// <summary>How long one outage is waited out before the studio stops waiting (plan @48 S2: 30 minutes).</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How many automatic continues run in a row with no turn succeeding in between. A network that comes back only
    /// long enough for the probe would otherwise have the studio send a prompt into it every few minutes for ever.
    /// </summary>
    public const int MaxContinues = 3;

    /// <summary>The words of a credential wait before any mint attempt has said more.</summary>
    public const string TokenRefusedWords = "the broker refused this conversation's token, which had expired";

    /// <summary>What the wait is for, as the dashboard leads with it.</summary>
    public string Headline => Reason == WaitReason.Credentials ? "Waiting for a fresh broker token" : "Waiting for the network";

    /// <summary>What failed, as the transcript and the banner say it: "could not reach api.anthropic.com: name not resolved".</summary>
    public string What => Reason == WaitReason.Credentials
        ? (Credential ?? TokenRefusedWords).TrimEnd('.')
        : $"could not reach {Host}: {Failure.Words}";

    /// <summary>The banner's sentence, as a log line or the transcript says it.</summary>
    public string Words => Build(StampedWords.Of(What)).Plain();

    /// <summary>
    /// The banner's sentence as parts, so the page says the times in the person's zone (backlog 246). While waiting for
    /// a token through a broker outage, the mint's words are built from <see cref="CredentialOutage"/>, so its "next
    /// attempt at" is in the person's zone too.
    /// </summary>
    public StampedWords Sentence => Build(WhatSentence);

    /// <summary><see cref="What"/> as the page says it (backlog 246): the mint's words as parts during a broker outage.</summary>
    public StampedWords WhatSentence =>
        Reason == WaitReason.Credentials && CredentialOutage is { } outage
            ? outage.MintWords.TrimEnd('.')
            : StampedWords.Of(What);

    private StampedWords Build(StampedWords what) =>
        StampedWords.Of(
            $"{Headline} since ",
            StampedWords.At(Since),
            ": ",
            what,
            ". ",
            Reason == WaitReason.Credentials
                ? $"{ProductName.Short} continues this conversation by itself once a fresh token is minted."
                : $"{ProductName.Short} continues this conversation by itself when it is back.",
            Attempt > 1 ? $" Automatic attempt {Attempt} of {MaxContinues}." : null);

    /// <summary>The transcript line when a turn fails on the network and the process is still there.</summary>
    public static string DroppedNotice(NetworkFailure failure, string host) =>
        $"The network dropped while the agent was working ({WhatFor(failure, host)}). {ProductName.Short} will continue when it is back.";

    /// <summary>The transcript line when the broker refuses a turn's expired token (backlog 235).</summary>
    public const string TokenRefusedNotice =
        $"The broker refused this conversation's token, which had expired. {ProductName.Short} will continue once a fresh token is minted.";

    /// <summary>The ending a process that died with the network carries; it is resumable.</summary>
    public static string EndedNotice(NetworkFailure failure, string host) =>
        $"The agent process ended when the network dropped ({WhatFor(failure, host)}). {ProductName.Short} will continue when it is back, "
        + "and Continue resumes this conversation.";

    /// <summary>The ending a process carries when it ended after the broker refused its expired token; it is resumable.</summary>
    public const string TokenEndedNotice =
        $"The agent process ended after the broker refused its expired token. {ProductName.Short} will continue once a fresh token is "
        + "minted, and Continue resumes this conversation.";

    /// <summary>The transcript line when the probe answers again, just before the conversation is continued.</summary>
    public static string BackNotice(string host) =>
        $"The network is back ({host} answers again). The agent is asked to check its last step, then continue.";

    /// <summary>The transcript line when a fresh broker token is in hand, just before the conversation is continued.</summary>
    public const string TokenBackNotice =
        "A fresh broker token was minted, and the agent process takes it. The agent is asked to check its last step, then continue.";

    /// <summary>
    /// The prompt the agent gets on recovery, in the shape of the restart prompt (backlog 218): the reason first, then
    /// check, then go on. It is a NEW turn; the failed one is never sent again.
    /// </summary>
    public static string ContinuePrompt(NetworkFailure failure, string host) =>
        LiveSessionRegistry.InterruptedTurnPromptFor(
            $"The network dropped ({WhatFor(failure, host)}) and is back now.");

    /// <summary>The prompt after a credential wait (backlog 235).</summary>
    public static string TokenContinuePrompt =>
        LiveSessionRegistry.InterruptedTurnPromptFor(
            "The model call was refused because this conversation's broker token had expired, and a fresh one has been minted now.");

    /// <summary>The prompt this wait continues with.</summary>
    public string ContinueWith => Reason == WaitReason.Credentials ? TokenContinuePrompt : ContinuePrompt(Failure, Host);

    /// <summary>The line this wait says just before it continues.</summary>
    public string BackWords => Reason == WaitReason.Credentials ? TokenBackNotice : BackNotice(Host);

    /// <summary>The transcript line when the studio stops waiting after <see cref="GiveUpAfter"/>.</summary>
    public static string GaveUpNotice(bool processEnded) => GaveUpNotice(processEnded, WaitReason.Network);

    /// <summary>The transcript line when the studio stops waiting after <see cref="GiveUpAfter"/>, for either reason.</summary>
    public static string GaveUpNotice(bool processEnded, WaitReason reason) =>
        (reason == WaitReason.Credentials
            ? $"No fresh broker token could be minted for over 30 minutes, so {ProductName.Short} stopped waiting. "
            : $"The network was down for over 30 minutes, so {ProductName.Short} stopped waiting. ")
        + Resume(processEnded);

    /// <summary>
    /// The transcript line when the broker REFUSES the fresh token (backlog 235): said once, and nothing waits on it,
    /// because asking again would be refused again. <paramref name="refusal"/> is the broker's refusal in plain words.
    /// </summary>
    public static string TokenRefusedStopNotice(string refusal, bool processEnded) =>
        $"{ProductName.Short} stopped waiting for a fresh broker token: {refusal.TrimEnd('.')}. " + Resume(processEnded);

    /// <summary>The transcript line when the studio does not wait because it already continued <see cref="MaxContinues"/> times.</summary>
    public static string TooManyNotice(NetworkFailure failure, string host, bool processEnded) =>
        $"The network failed again ({WhatFor(failure, host)}) after {MaxContinues} automatic attempts, so {ProductName.Short} is not "
        + "continuing by itself. "
        + Resume(processEnded);

    /// <summary>The same, for a credential wait.</summary>
    public static string TooManyTokenNotice(bool processEnded) =>
        $"The broker refused this conversation's token again after {MaxContinues} automatic attempts, so {ProductName.Short} is not "
        + "continuing by itself. "
        + Resume(processEnded);

    /// <summary>The transcript line when the person stops a wait whose process had already ended.</summary>
    public const string StoppedNotice = "Stopped waiting for the network. Continue resumes this conversation.";

    private static string Resume(bool processEnded) =>
        processEnded ? "Continue resumes this conversation." : "Send a message to continue this conversation.";

    private static string WhatFor(NetworkFailure failure, string host) => $"could not reach {host}: {failure.Words}";
}
