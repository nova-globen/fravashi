using System.Net.Http;
using System.Net.Sockets;

namespace Fravashi.Application.Network;

/// <summary>
/// What kind of network failure a call met (backlog 228, plan @49). The never-left kinds are the ones where the
/// request provably did not reach the other side, and each is answered ONLY when that is proven; when the phase is not
/// known, an unreachable route, a timeout or a reset is <see cref="LeftWithNoAnswer"/>, the kind where it may have.
/// </summary>
public enum NetworkFailureKind
{
    /// <summary>Not a network failure at all: a refusal, a bad request, the caller's own cancellation.</summary>
    NotNetwork,

    /// <summary>The name could not be looked up. Never left.</summary>
    NameNotResolved,

    /// <summary>The other side refused the connection. Never left.</summary>
    Refused,

    /// <summary>No route to the other side (the link is down, or the network does not lead there). Never left.</summary>
    Unreachable,

    /// <summary>The connection was not made in time. Never left.</summary>
    TimedOutConnecting,

    /// <summary>The connection, or its TLS or proxy setup, broke before the request was handed over. Never left.</summary>
    ResetBeforeSend,

    /// <summary>
    /// The request was on the wire and no answer came back: a reset after it was written, a premature end, or the
    /// client's own timeout. The other side may have acted on it.
    /// </summary>
    LeftWithNoAnswer,
}

/// <summary>
/// The one place that decides what counts as "the network failed" (backlog 228, slice A of 219, plan @49), so the
/// surfaces 219 names stop deciding it each on their own. It classifies an exception from an <c>HttpClient</c> call or
/// a raw socket, and the error text the agent CLI prints.
/// <para>
/// For an <c>HttpClient</c> call, "never left" and "left with no answer" are told apart by the PHASE
/// <c>SocketsHttpHandler</c> stamps on its <see cref="HttpRequestException.HttpRequestError"/>: a name, connection,
/// TLS or proxy-tunnel error is raised only while the connection is being made, before a byte of the request is
/// written. Anything after that (a reset while writing or reading, a premature end, the client's own timeout) is
/// left with no answer, which is the safe side for a write: it is looked up, never resent blindly. The handler never
/// says how many bytes arrived, so this is INFERRED from the phase. Two cases stay inferred on the safe side: a
/// client timeout that in fact expired while connecting, and an exception from some other handler with no phase.
/// A reset after the request was written was MEASURED on a real socket and is not a connection error
/// (<c>NetworkFailureShapesTests</c> in Infrastructure.Tests pins it).
/// </para>
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Detail">The low-level reason, for a log line (a socket error, an error code, the phase).</param>
public sealed record NetworkFailure(NetworkFailureKind Kind, string Detail)
{
    /// <summary>The answer for anything that is not a network failure.</summary>
    public static NetworkFailure None { get; } = new(NetworkFailureKind.NotNetwork, "");

    /// <summary>True for every kind but <see cref="NetworkFailureKind.NotNetwork"/>.</summary>
    public bool IsNetwork => Kind != NetworkFailureKind.NotNetwork;

    /// <summary>
    /// True when the request provably did not reach the other side, so sending it again cannot do it twice.
    /// False for <see cref="NetworkFailureKind.LeftWithNoAnswer"/> and for anything that is not a network failure.
    /// </summary>
    public bool NeverLeft => Kind is NetworkFailureKind.NameNotResolved or NetworkFailureKind.Refused
        or NetworkFailureKind.Unreachable or NetworkFailureKind.TimedOutConnecting or NetworkFailureKind.ResetBeforeSend;

    /// <summary>Plain words for the kind, written to follow "could not reach X" or stand in brackets after it.</summary>
    public string Words => WordsFor(Kind);

    /// <summary>Plain words for each kind.</summary>
    public static string WordsFor(NetworkFailureKind kind) => kind switch
    {
        NetworkFailureKind.NameNotResolved => "name not resolved",
        NetworkFailureKind.Refused => "connection refused",
        NetworkFailureKind.Unreachable => "no network route",
        NetworkFailureKind.TimedOutConnecting => "timed out connecting",
        NetworkFailureKind.ResetBeforeSend => "connection broke before the request was sent",
        NetworkFailureKind.LeftWithNoAnswer => "no answer came back, and the request may have arrived",
        _ => "not a network failure",
    };

    /// <summary>
    /// Classifies an exception. <paramref name="callerToken"/> is the token the caller passed to the call: a
    /// cancellation it asked for is never a network failure, while the client's own timeout is.
    /// </summary>
    public static NetworkFailure Classify(Exception exception, CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException cancelled)
        {
            // HttpClient's own timeout carries a TimeoutException inside (since .NET 5); a cancellation the caller
            // asked for does not, and wins even when both happened.
            return !callerToken.IsCancellationRequested && Contains<TimeoutException>(cancelled)
                ? new(NetworkFailureKind.LeftWithNoAnswer, "the client's own timeout")
                : None;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                var found = Classify(inner, callerToken);
                if (found.IsNetwork)
                {
                    return found;
                }
            }

            return None;
        }

        if (exception is HttpRequestException http)
        {
            return FromHttp(http);
        }

        if (exception is SocketException socket)
        {
            return FromSocket(socket.SocketErrorCode, phaseKnownToBeConnect: false);
        }

        if (exception is IOException)
        {
            // A stream error: the connection existed, so whatever was written may have arrived, whatever the socket
            // error inside says.
            return new(NetworkFailureKind.LeftWithNoAnswer, "the connection broke while in use");
        }

        return exception.InnerException is { } next ? Classify(next, callerToken) : None;
    }

    private static NetworkFailure FromHttp(HttpRequestException http)
    {
        switch (http.HttpRequestError)
        {
            case HttpRequestError.NameResolutionError:
                return new(NetworkFailureKind.NameNotResolved, nameof(HttpRequestError.NameResolutionError));

            case HttpRequestError.ConnectionError:
                if (Find<SocketException>(http) is { } socket
                    && FromSocket(socket.SocketErrorCode, phaseKnownToBeConnect: true) is { IsNetwork: true } connect)
                {
                    return connect;
                }

                return Contains<TimeoutException>(http) || Contains<OperationCanceledException>(http)
                    ? new(NetworkFailureKind.TimedOutConnecting, "connect timeout")
                    : new(NetworkFailureKind.ResetBeforeSend, nameof(HttpRequestError.ConnectionError));

            case HttpRequestError.SecureConnectionError:
            case HttpRequestError.ProxyTunnelError:
                return new(NetworkFailureKind.ResetBeforeSend, http.HttpRequestError.ToString());

            case HttpRequestError.UserAuthenticationError:
            case HttpRequestError.ConfigurationLimitExceeded:
            case HttpRequestError.VersionNegotiationError:
                return None;

            default:
                // ResponseEnded, HttpProtocolError, InvalidResponse, or Unknown with a reset inside: the connection
                // was made, so the request may have arrived.
                return new(NetworkFailureKind.LeftWithNoAnswer, http.HttpRequestError.ToString());
        }
    }

    /// <summary>
    /// Classifies a socket error from a connect, where the phase is known: nothing was written yet, so a reset or a
    /// timeout here never left. An error that is no network failure still answers <see cref="NetworkFailureKind.ResetBeforeSend"/>,
    /// because a connect that failed did not leave whatever the reason.
    /// </summary>
    public static NetworkFailure ClassifyConnect(SocketError error) =>
        FromSocket(error, phaseKnownToBeConnect: true) is { IsNetwork: true } found
            ? found
            : new(NetworkFailureKind.ResetBeforeSend, error.ToString());

    // A never-left kind is answered only where the phase is PROVEN (the outbox's rule 3 rests on it). Without a known
    // connect phase, only two errors still prove it: a name error comes from the lookup, which precedes any socket, and
    // TCP reports a refusal only to a connection attempt. An unreachable route, a timeout or a reset can each hit a
    // connection already in use (Linux keeps an ICMP error on an established socket and reports EHOSTUNREACH or
    // ENETUNREACH on its next read or write), so with the phase unknown they may have left.
    private static NetworkFailure FromSocket(SocketError error, bool phaseKnownToBeConnect)
    {
        NetworkFailureKind whenConnecting;
        switch (error)
        {
            case SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData or SocketError.NoRecovery:
                return new(NetworkFailureKind.NameNotResolved, error.ToString());
            case SocketError.ConnectionRefused:
                return new(NetworkFailureKind.Refused, error.ToString());
            case SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.HostDown:
                whenConnecting = NetworkFailureKind.Unreachable;
                break;
            case SocketError.TimedOut:
                whenConnecting = NetworkFailureKind.TimedOutConnecting;
                break;
            case SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown
                or SocketError.NotConnected or SocketError.NetworkReset:
                whenConnecting = NetworkFailureKind.ResetBeforeSend;
                break;
            default:
                return None;
        }

        return phaseKnownToBeConnect
            ? new(whenConnecting, error.ToString())
            : new(NetworkFailureKind.LeftWithNoAnswer, $"{error}, on a connection that may already have been in use");
    }

    // The agent CLI is node: its system error codes are exact, upper case words, and node writes the system call in
    // front of one ("getaddrinfo EAI_AGAIN host", "connect ETIMEDOUT ip:port", "read ECONNRESET"). Codes are checked
    // before phrases, because "Can't reach the API server" usually carries one in brackets and the code is more exact.
    // The same never-left rule as for sockets: a name error and a refusal prove it on their own; an unreachable route
    // and a timeout prove it only when node says the call was "connect", and otherwise (a read or a write on a
    // connection in use) may have left.
    private static readonly (string Code, NetworkFailureKind Kind, bool OnlyFromConnect)[] CliCodes =
    [
        ("EAI_AGAIN", NetworkFailureKind.NameNotResolved, false),
        ("ENOTFOUND", NetworkFailureKind.NameNotResolved, false),
        ("EAI_NODATA", NetworkFailureKind.NameNotResolved, false),
        ("ECONNREFUSED", NetworkFailureKind.Refused, false),
        ("ENETUNREACH", NetworkFailureKind.Unreachable, true),
        ("EHOSTUNREACH", NetworkFailureKind.Unreachable, true),
        ("ENETDOWN", NetworkFailureKind.Unreachable, true),
        ("ETIMEDOUT", NetworkFailureKind.TimedOutConnecting, true),
        ("ECONNRESET", NetworkFailureKind.LeftWithNoAnswer, false),
        ("ECONNABORTED", NetworkFailureKind.LeftWithNoAnswer, false),
        ("EPIPE", NetworkFailureKind.LeftWithNoAnswer, false),
    ];

    // Phrases with no code say nothing about the phase, so apart from the lookup they may have left. "Can't reach the
    // API server" is matched without its apostrophe, which may be a typographic one.
    private static readonly (string Phrase, NetworkFailureKind Kind)[] CliPhrases =
    [
        ("getaddrinfo", NetworkFailureKind.NameNotResolved),
        ("socket hang up", NetworkFailureKind.LeftWithNoAnswer),
        ("Request timed out", NetworkFailureKind.LeftWithNoAnswer),
        ("reach the API server", NetworkFailureKind.LeftWithNoAnswer),
        ("Connection error.", NetworkFailureKind.LeftWithNoAnswer),
    ];

    /// <summary>
    /// Classifies the error text the agent CLI printed (a failed turn's result, or its stderr). A never-left kind is
    /// answered only where the text proves the phase (see the code table). Anything that names none of these (a 401,
    /// a 400, a budget, a validation error) is not a network failure.
    /// </summary>
    public static NetworkFailure ClassifyCliText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return None;
        }

        foreach (var (code, kind, onlyFromConnect) in CliCodes)
        {
            var at = FindWord(text, code);
            if (at < 0)
            {
                continue;
            }

            if (onlyFromConnect && !text.AsSpan(0, at).TrimEnd().EndsWith("connect", StringComparison.Ordinal))
            {
                return new(NetworkFailureKind.LeftWithNoAnswer, $"{code}, not from a connect");
            }

            return new(kind, code);
        }

        foreach (var (phrase, kind) in CliPhrases)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return new(kind, phrase);
            }
        }

        return None;
    }

    private static int FindWord(string text, string word)
    {
        for (var at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 || !IsCodeChar(text[at - 1]);
            var end = at + word.Length;
            var after = end == text.Length || !IsCodeChar(text[end]);
            if (before && after)
            {
                return at;
            }
        }

        return -1;
    }

    private static bool IsCodeChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static bool Contains<T>(Exception exception) where T : Exception => Find<T>(exception) is not null;

    private static T? Find<T>(Exception exception) where T : Exception
    {
        for (var e = exception.InnerException; e is not null; e = e.InnerException)
        {
            if (e is T found)
            {
                return found;
            }
        }

        return null;
    }
}
