using System.Net.Sockets;

namespace Fravashi.Testing.Network;

/// <summary>
/// The exceptions the real <c>SocketsHttpHandler</c> and <c>HttpClient</c> throw for each fault, built by hand.
/// Each shape is held equal to what a real loopback socket produces by <c>NetworkFailureShapesTests</c>
/// (Infrastructure.Tests), so the scripted network cannot drift into throwing something .NET never does.
/// </summary>
public static class ScriptedShapes
{
    public static Exception NameFails(string host) => new HttpRequestException(
        HttpRequestError.NameResolutionError,
        $"Name or service not known ({host}:443)",
        new SocketException((int)SocketError.HostNotFound));

    public static Exception Refused() => new HttpRequestException(
        HttpRequestError.ConnectionError,
        "Connection refused",
        new SocketException((int)SocketError.ConnectionRefused));

    public static Exception NoRoute() => new HttpRequestException(
        HttpRequestError.ConnectionError,
        "Network is unreachable",
        new SocketException((int)SocketError.NetworkUnreachable));

    public static Exception ResetBeforeSend() => new HttpRequestException(
        HttpRequestError.ConnectionError,
        "Connection reset by peer",
        new SocketException((int)SocketError.ConnectionReset));

    /// <summary>The request was written, then the connection was reset before any answer.</summary>
    public static Exception ResetAfterWrite() => new HttpRequestException(
        HttpRequestError.Unknown,
        "An error occurred while sending the request.",
        new IOException(
            "Unable to read data from the transport connection: Connection reset by peer.",
            new SocketException((int)SocketError.ConnectionReset)));

    /// <summary>HttpClient's own timeout: a cancellation with a <see cref="TimeoutException"/> inside.</summary>
    public static Exception ClientTimeout(TimeSpan timeout) => new TaskCanceledException(
        $"The request was canceled due to the configured HttpClient.Timeout of {timeout.TotalSeconds} seconds elapsing.",
        new TimeoutException("The operation was canceled.", new TaskCanceledException("The operation was canceled.")));
}
