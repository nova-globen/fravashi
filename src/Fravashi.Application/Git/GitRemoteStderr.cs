using System.Globalization;
using System.Text.RegularExpressions;
using Fravashi.Application.Network;

namespace Fravashi.Application.Git;

/// <summary>
/// A remote's real answer that refused the operation (backlog 231). Each is an ANSWER: origin was reached, so none of
/// them is the network and none is retried by itself.
/// </summary>
public enum GitRefusal
{
    /// <summary>Not a refusal.</summary>
    None,

    /// <summary>The push was not a fast-forward (origin has commits this branch does not).</summary>
    Rejected,

    /// <summary>Origin's own rules (a hook, a protected branch) refused it.</summary>
    HookDeclined,

    /// <summary>Origin did not accept the credentials, or there were none to give.</summary>
    Authentication,

    /// <summary>The credentials were accepted and are not allowed to do this.</summary>
    Permission,

    /// <summary>Origin has no such repository, or hides it from these credentials.</summary>
    NotFound,

    /// <summary>Origin's certificate was not trusted. A trust question, not the network.</summary>
    Certificate,

    /// <summary>Origin answered with a server error before anything was sent; trying again is harmless.</summary>
    ServerError,
}

/// <summary>What git's own error text says about a remote operation (backlog 231).</summary>
/// <param name="Network">Slice A's kind when it was the network; <see cref="NetworkFailureKind.NotNetwork"/> otherwise.</param>
/// <param name="Refusal">The remote's refusal when it answered with one.</param>
/// <param name="Reason">Plain words for the bracket in "Could not reach origin (...)", or empty.</param>
public sealed record GitRemoteVerdict(NetworkFailureKind Network, GitRefusal Refusal, string Reason)
{
    /// <summary>Neither the network nor a refusal: an error git reported that this does not recognise.</summary>
    public static GitRemoteVerdict Other { get; } = new(NetworkFailureKind.NotNetwork, GitRefusal.None, "");

    /// <summary>True when the network failed.</summary>
    public bool IsNetwork => Network != NetworkFailureKind.NotNetwork;

    /// <summary>True when the request provably did not reach origin (slice A's rule).</summary>
    public bool NeverLeft => new NetworkFailure(Network, "").NeverLeft;
}

/// <summary>
/// The one place that reads git's stderr for a fetch, pull or push (backlog 231, slice D of 219, plan @57), so the Git
/// page and the agent tools never disagree. (The wiki access probe keeps its own narrower credential and not-found
/// reader, <c>Infrastructure.Providers.GitRemoteFailure</c>.) A network failure answers slice A's <see cref="NetworkFailureKind"/>, and
/// "never left" is answered only where git's text proves the phase, the same rule <see cref="NetworkFailure"/> keeps.
/// <para>
/// What was MEASURED (git 2.43 with curl 8.5 in the studio container, 2026-10-07, fixtures under
/// <c>tests/Fravashi.Application.Tests/Git/Fixtures</c>): over http(s) a failure during ref discovery, before any pack
/// is sent, is <c>fatal: unable to access '&lt;url&gt;': &lt;curl's words&gt;</c>, and one after the pack went out
/// is <c>error: RPC failed; curl NN ...</c> followed by <c>send-pack: unexpected disconnect while reading sideband
/// packet</c> and <c>fatal: the remote end hung up unexpectedly</c> (and then, misleadingly, <c>Everything
/// up-to-date</c>). curl 8.5 words EVERY connect failure "Couldn't connect to server": a refused port, an unroutable
/// address and a black hole after 134 seconds read the same, so only the elapsed time it prints tells a timeout apart.
/// Phrasings for ssh are git's and OpenSSH's source text (the container has no ssh client): with no phase in them a
/// reset over ssh may have left.
/// </para>
/// </summary>
public static class GitRemoteStderr
{
    /// <summary>A connect that took at least this long before it failed is called a timeout.</summary>
    private static readonly TimeSpan ConnectTimeoutFloor = TimeSpan.FromSeconds(5);

    private static readonly Regex AfterMilliseconds = new(@"after (\d+) ms", RegexOptions.CultureInvariant);

    /// <summary>Classifies git's stderr for a fetch, pull or push.</summary>
    public static GitRemoteVerdict Classify(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return GitRemoteVerdict.Other;
        }

        var text = stderr;

        // Answers first: a refusal means origin was reached, whatever else the text says.
        if (Has(text, "[remote rejected]") || Has(text, "hook declined"))
        {
            return Refused(GitRefusal.HookDeclined);
        }

        if (Has(text, "[rejected]") || Has(text, "non-fast-forward") || Has(text, "(fetch first)"))
        {
            return Refused(GitRefusal.Rejected);
        }

        if (Has(text, "Authentication failed") || Has(text, "could not read Username") || Has(text, "could not read Password")
            || Has(text, "Invalid username or password") || Has(text, "returned error: 401") || Has(text, "Permission denied (publickey"))
        {
            return Refused(GitRefusal.Authentication);
        }

        if (Has(text, "returned error: 403") || (Has(text, "Permission to ") && Has(text, " denied")))
        {
            return Refused(GitRefusal.Permission);
        }

        if ((Has(text, "repository '") && Has(text, "' not found")) || Has(text, "Repository not found")
            || Has(text, "does not appear to be a git repository") || Has(text, "returned error: 404"))
        {
            return Refused(GitRefusal.NotFound);
        }

        if (Has(text, "SSL certificate problem") || Has(text, "certificate verification failed")
            || Has(text, "server certificate"))
        {
            return Refused(GitRefusal.Certificate);
        }

        // A name error precedes any socket, so it never left wherever it appears.
        if (Has(text, "Could not resolve host") || Has(text, "unable to look up") || Has(text, "Name or service not known")
            || Has(text, "Temporary failure in name resolution") || Has(text, "nodename nor servname"))
        {
            return Net(NetworkFailureKind.NameNotResolved);
        }

        // The pack went out (git's RPC to receive-pack failed, or send-pack lost the connection): it may have arrived.
        if (Has(text, "RPC failed") || Has(text, "send-pack: unexpected disconnect"))
        {
            return new(NetworkFailureKind.LeftWithNoAnswer, GitRefusal.None, AfterSendReason(text));
        }

        if (Has(text, "unable to access '") && Has(text, "returned error: 5"))
        {
            return Refused(GitRefusal.ServerError);
        }

        // A connect that failed: curl says "Failed to connect to", git's own transport "unable to connect to", ssh
        // "connect to host". Nothing was written.
        if (Has(text, "Failed to connect to") || Has(text, "unable to connect to") || Has(text, "connect to host"))
        {
            if (Has(text, "Connection refused"))
            {
                return Net(NetworkFailureKind.Refused);
            }

            if (Has(text, "Network is unreachable") || Has(text, "No route to host"))
            {
                return Net(NetworkFailureKind.Unreachable);
            }

            if (Has(text, "timed out"))
            {
                return Net(NetworkFailureKind.TimedOutConnecting);
            }

            if (AfterMilliseconds.Match(text) is { Success: true } after
                && long.TryParse(after.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
                && TimeSpan.FromMilliseconds(ms) >= ConnectTimeoutFloor)
            {
                return new(NetworkFailureKind.TimedOutConnecting, GitRefusal.None,
                    $"timed out connecting after {ms / 1000} s");
            }

            // curl 8 no longer says whether the port was closed or the route missing.
            return new(NetworkFailureKind.Unreachable, GitRefusal.None, "could not connect to the server");
        }

        // Ref discovery over http(s) broke: no pack had been sent yet.
        if (Has(text, "unable to access '") && (Has(text, "Connection reset") || Has(text, "Empty reply")
                || Has(text, "Recv failure") || Has(text, "Send failure") || Has(text, "SSL") || Has(text, "TLS")
                || Has(text, "gnutls") || Has(text, "timed out") || Has(text, "Proxy")))
        {
            return new(NetworkFailureKind.ResetBeforeSend, GitRefusal.None,
                Has(text, "timed out") ? "timed out before anything was sent" : NetworkFailure.WordsFor(NetworkFailureKind.ResetBeforeSend));
        }

        // Anything else that broke a connection names no phase: it may have left.
        if (Has(text, "Connection reset") || Has(text, "Connection closed by") || Has(text, "remote end hung up unexpectedly")
            || Has(text, "early EOF") || Has(text, "Broken pipe") || Has(text, "timed out"))
        {
            return new(NetworkFailureKind.LeftWithNoAnswer, GitRefusal.None, AfterSendReason(text));
        }

        return GitRemoteVerdict.Other;
    }

    private static string AfterSendReason(string text) =>
        Has(text, "Connection reset") ? "the connection was reset"
        : Has(text, "Empty reply") ? "the server closed the connection without answering"
        : Has(text, "curl 28") || Has(text, "too slow") || Has(text, "timed out") ? "the transfer timed out"
        : Has(text, "returned error: 5") || Has(text, "HTTP 5") ? "origin answered with a server error mid-transfer"
        : "the connection broke during the transfer";

    private static GitRemoteVerdict Refused(GitRefusal refusal) => new(NetworkFailureKind.NotNetwork, refusal, "");

    private static GitRemoteVerdict Net(NetworkFailureKind kind) => new(kind, GitRefusal.None, NetworkFailure.WordsFor(kind));

    private static bool Has(string text, string phrase) => text.Contains(phrase, StringComparison.OrdinalIgnoreCase);
}
