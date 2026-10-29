// RETIRED to v2. Decided, 2026-09-22: Codex and Copilot are not part of this version (docs/v2.md,
// section 1). This helper has no other caller: both runtimes that spoke JSON-RPC over stdio were the two
// retired ones, so it is disabled with them rather than left as code that compiles and nothing runs.
#if V2_CODEX_COPILOT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// One agent CLI child that speaks JSON-RPC 2.0, one message per line, over its stdin and stdout (backlog 66).
/// Both the Codex app-server and the Copilot ACP server are this shape: the studio sends REQUESTS and waits
/// for their responses, the CLI sends NOTIFICATIONS (what happened) and its own REQUESTS (what it wants the
/// studio to decide, an approval above all).
/// <para>
/// A server request is handed to its handler on the thread pool and answered when the handler finishes, so an
/// approval that waits hours for the user never stalls the read loop behind it. A handler that throws is
/// answered with a JSON-RPC error rather than left unanswered, and every request still pending when the child's
/// output closes is failed, so nothing awaiting a response can hang past the process.
/// </para>
/// </summary>
public sealed class JsonRpcStdioChild
{
    /// <summary>JSON-RPC's "method not found", the answer to a request nobody taught the studio to handle.</summary>
    public const int MethodNotFound = -32601;

    /// <summary>The generic server-error code used for a refusal the studio decided on.</summary>
    public const int Refused = -32000;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly bool _stampVersion;
    private long _nextId;

    /// <param name="process">The started child, with stdin and stdout redirected.</param>
    /// <param name="stampVersion">
    /// True to send <c>"jsonrpc": "2.0"</c> on every message. ACP requires it; the Codex app-server's generated
    /// envelope schema (0.146.0) has no such field, so it is left off there.
    /// </param>
    public JsonRpcStdioChild(Process process, bool stampVersion)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        _stampVersion = stampVersion;
    }

    public Process Process { get; }

    /// <summary>
    /// Reads the child's stdout until it closes. Notifications go to <paramref name="onNotification"/> in order,
    /// on this loop; server requests go to <paramref name="onRequest"/> off it, and its result (or its exception,
    /// as an error) is written back under the request's own id.
    /// </summary>
    public async Task ReadAsync(
        Func<string, JsonElement, Task> onNotification,
        Func<string, JsonElement, Task<JsonRpcAnswer>> onRequest,
        Action<string>? onUnparsable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onNotification);
        ArgumentNullException.ThrowIfNull(onRequest);

        try
        {
            while (await Process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonElement message;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    message = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    onUnparsable?.Invoke(line);
                    continue;
                }

                if (message.ValueKind != JsonValueKind.Object)
                {
                    onUnparsable?.Invoke(line);
                    continue;
                }

                var hasMethod = message.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String;
                var hasId = message.TryGetProperty("id", out var id)
                    && id.ValueKind is JsonValueKind.String or JsonValueKind.Number;
                var parameters = message.TryGetProperty("params", out var p) ? p : default;

                if (hasMethod && hasId)
                {
                    var name = method.GetString()!;
                    var requestId = id.Clone();
                    _ = Task.Run(() => AnswerAsync(name, requestId, parameters, onRequest, cancellationToken), CancellationToken.None);
                }
                else if (hasMethod)
                {
                    await onNotification(method.GetString()!, parameters).ConfigureAwait(false);
                }
                else if (hasId && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number)
                    && _pending.TryRemove(number, out var waiter))
                {
                    if (message.TryGetProperty("error", out var error))
                    {
                        waiter.TrySetException(new JsonRpcException(ErrorMessage(error)));
                    }
                    else
                    {
                        waiter.TrySetResult(message.TryGetProperty("result", out var result) ? result.Clone() : default);
                    }
                }
            }
        }
        finally
        {
            foreach (var key in _pending.Keys.ToList())
            {
                if (_pending.TryRemove(key, out var waiter))
                {
                    waiter.TrySetException(new JsonRpcException("The agent CLI closed its output before answering."));
                }
            }
        }
    }

    /// <summary>Sends a request and waits for its result. Throws <see cref="JsonRpcException"/> on an error response.</summary>
    public async Task<JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        var message = Envelope();
        message["id"] = id;
        message["method"] = method;
        message["params"] = parameters;

        if (!await WriteAsync(message, cancellationToken).ConfigureAwait(false))
        {
            _pending.TryRemove(id, out _);
            throw new JsonRpcException("The agent CLI is no longer reading its input.");
        }

        using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
        return await waiter.Task.ConfigureAwait(false);
    }

    /// <summary>Sends a notification (no response expected). False when the child is gone.</summary>
    public Task<bool> NotifyAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var message = Envelope();
        message["method"] = method;
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return WriteAsync(message, cancellationToken);
    }

    private async Task AnswerAsync(
        string method,
        JsonElement id,
        JsonElement parameters,
        Func<string, JsonElement, Task<JsonRpcAnswer>> onRequest,
        CancellationToken cancellationToken)
    {
        JsonRpcAnswer answer;
        try
        {
            answer = await onRequest(method, parameters).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fail closed: a handler that broke is a refusal, never silence and never an accidental allow.
            answer = JsonRpcAnswer.Error(Refused, "The studio could not decide this request: " + ex.Message);
        }

        var message = Envelope();
        message["id"] = JsonNode.Parse(id.GetRawText());
        if (answer.Result is { } result)
        {
            message["result"] = result;
        }
        else
        {
            message["error"] = new JsonObject
            {
                ["code"] = answer.ErrorCode,
                ["message"] = answer.ErrorMessage ?? "Refused.",
            };
        }

        await WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private JsonObject Envelope()
    {
        var message = new JsonObject();
        if (_stampVersion)
        {
            message["jsonrpc"] = "2.0";
        }

        return message;
    }

    private async Task<bool> WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var line = message.ToJsonString();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Process.HasExited)
            {
                return false;
            }

            await Process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await Process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string ErrorMessage(JsonElement error)
    {
        var builder = new StringBuilder();
        if (error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString());
            }

            if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number)
            {
                builder.Append(" (code ").Append(code.GetRawText()).Append(')');
            }
        }

        return builder.Length == 0
            ? "The agent CLI refused the request."
            : builder.ToString();
    }
}

/// <summary>What the studio answers a CLI's request with: a result, or an error.</summary>
public sealed record JsonRpcAnswer(JsonNode? Result, int ErrorCode, string? ErrorMessage)
{
    public static JsonRpcAnswer Ok(JsonNode result) => new(result, 0, null);

    public static JsonRpcAnswer Error(int code, string message) => new(null, code, message);
}

/// <summary>An error response from the CLI, or a request that could not be delivered.</summary>
public sealed class JsonRpcException : Exception
{
    public JsonRpcException()
    {
    }

    public JsonRpcException(string message)
        : base(message)
    {
    }

    public JsonRpcException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

#endif
