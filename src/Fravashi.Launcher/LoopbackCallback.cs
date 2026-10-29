using System.Net;
using System.Net.Sockets;
using System.Text;
using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// A one-shot HTTP listener for the identity provider's redirect back to <c>http://localhost:PORT/PATH</c>
/// (backlog 94). It exists for ONE request: the browser arriving with the authorization code. It answers that
/// request with a short page telling the person to go back to Fravashi, and is gone.
/// <para>
/// Hand-rolled on <see cref="TcpListener"/> rather than <c>HttpListener</c> or Kestrel: the launcher library
/// takes no web dependency, <c>HttpListener</c> is a Windows URL-reservation story of its own, and a GET line
/// is all there is to read. It binds BOTH loopback addresses, because a browser asked for <c>localhost</c> may
/// try IPv6 first, and it never binds anything but loopback.
/// </para>
/// </summary>
public sealed class LoopbackCallback : IDisposable
{
    private readonly List<TcpListener> _listeners = [];
    private readonly string _path;

    private LoopbackCallback(string path) => _path = "/" + path.Trim('/');

    /// <summary>
    /// Listens on <paramref name="port"/> for <paramref name="path"/>, or null when the port cannot be taken on
    /// the IPv4 loopback (something else is serving it, which for the studio's port means the studio is up).
    /// </summary>
    public static LoopbackCallback? TryListen(int port, string path)
    {
        var callback = new LoopbackCallback(path);
        try
        {
            var v4 = new TcpListener(IPAddress.Loopback, port);
            v4.Start();
            callback._listeners.Add(v4);
        }
        catch (SocketException)
        {
            callback.Dispose();
            return null;
        }

        try
        {
            var v6 = new TcpListener(IPAddress.IPv6Loopback, port);
            v6.Start();
            callback._listeners.Add(v6);
        }
        catch (SocketException)
        {
            // No IPv6 loopback on this machine, or the port is taken there: the IPv4 one still answers, and a
            // browser that fails on ::1 falls back to 127.0.0.1.
        }

        return callback;
    }

    /// <summary>
    /// Waits for the redirect and returns its query parameters. Every connection is served on its own, with a read
    /// timeout, and anything that goes wrong on one connection ends THAT connection only. Measured on 2026-09-23 in
    /// the installed v0.9.31: Edge opens speculative "preconnect" sockets that send nothing, the first version served
    /// one connection at a time, so it sat on an idle preconnect while the real redirect waited behind it, and when
    /// Edge closed the idle socket a minute later the reply to it failed and took the whole sign-in down with it.
    /// Anything that is not the callback (a favicon, a stray probe, an empty preconnect) is answered 404 or dropped.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var answered = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = linked.Token.Register(() => answered.TrySetCanceled(linked.Token));
        var accepting = _listeners.Select(l => AcceptLoopAsync(l, answered, linked.Token)).ToList();
        try
        {
            return await answered.Task.ConfigureAwait(false);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task AcceptLoopAsync(
        TcpListener listener, TaskCompletionSource<IReadOnlyDictionary<string, string>> answered, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            // Served on its own: the next connection is accepted straight away, whatever this one does.
            _ = ServeAsync(client, answered, ct);
        }
    }

    private async Task ServeAsync(
        TcpClient client, TaskCompletionSource<IReadOnlyDictionary<string, string>> answered, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
                read.CancelAfter(ReadTimeout);
                var stream = client.GetStream();
                var target = await ReadRequestTargetAsync(stream, read.Token).ConfigureAwait(false);
                if (target.Length == 0)
                {
                    return; // A preconnect that never spoke, or something that is not HTTP: nothing to answer.
                }

                var (path, query) = Split(target);
                if (!string.Equals(path, _path, StringComparison.Ordinal))
                {
                    await RespondAsync(stream, 404, "Not found.", ct).ConfigureAwait(false);
                    return;
                }

                var parameters = ParseQuery(query);
                var ok = parameters.ContainsKey("code") && !parameters.ContainsKey("error");
                await RespondAsync(
                    stream,
                    200,
                    ok
                        ? $"You are signed in. Go back to {ProductName.Short}; this tab can be closed."
                        : $"The sign-in did not complete. Go back to {ProductName.Short}, which will ask you to sign in there.",
                    ct).ConfigureAwait(false);
                answered.TrySetResult(parameters);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // This connection's trouble is this connection's: the wait goes on for the real redirect.
            }
        }
    }

    /// <summary>How long one connection may take to send its request line before it is dropped.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The request target of the first line, e.g. <c>/auth/callback/idp?code=..</c>; empty when unreadable.</summary>
    private static async Task<string> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                var firstLine = text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
                var parts = firstLine.Split(' ');
                return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : string.Empty;
            }
        }

        return string.Empty;
    }

    private static (string Path, string Query) Split(string target)
    {
        var question = target.IndexOf('?', StringComparison.Ordinal);
        return question < 0 ? (target, string.Empty) : (target[..question], target[(question + 1)..]);
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            var value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            parameters.TryAdd(key, value);
        }

        return parameters;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string message, CancellationToken ct)
    {
        var body = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{ProductName.Short}</title></head>"
            + "<body style=\"font-family:system-ui,sans-serif;margin:3rem\"><p>"
            + WebUtility.HtmlEncode(message) + "</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {bytes.Length}\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            listener.Stop();
            listener.Dispose();
        }

        _listeners.Clear();
    }
}
