using System.Net;

namespace Fravashi.Testing.Network;

/// <summary>One call the scripted handler played.</summary>
/// <param name="At">When it was made, on the fake clock.</param>
/// <param name="Behaviour">What the network did with it.</param>
/// <param name="Method">The request's method.</param>
/// <param name="Uri">The request's address.</param>
/// <param name="BodyArrived">
/// The body as the other side received it, or null when nothing arrived. A reset after write HAS a body here: the
/// request arrived and only the answer was lost, which is what makes resending a creation make two.
/// </param>
public sealed record ScriptedCall(DateTimeOffset At, NetworkBehaviour Behaviour, HttpMethod Method, Uri? Uri, string? BodyArrived)
{
    /// <summary>True when the other side received the request.</summary>
    public bool Arrived => BodyArrived is not null;
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> that plays a <see cref="NetworkScript"/> (backlog 228). Each fault throws the
/// shape the real handler throws (<see cref="ScriptedShapes"/>). A hung call never waits: it moves the fake clock on by
/// <see cref="ClientTimeout"/> and throws HttpClient's timeout shape, so a five minute outage runs in microseconds.
/// Healthy calls are answered by the function given, or 200 with an empty JSON object.
/// </summary>
public sealed class ScriptedHttpHandler(NetworkScript script, Func<HttpRequestMessage, HttpResponseMessage>? answer = null)
    : HttpMessageHandler
{
    private readonly List<ScriptedCall> _journal = [];
    private readonly object _lock = new();

    /// <summary>The timeout a hung call runs out, as the HttpClient in front of this handler would. 100 s is HttpClient's default.</summary>
    public TimeSpan ClientTimeout { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>Every call, in order.</summary>
    public IReadOnlyList<ScriptedCall> Journal
    {
        get
        {
            lock (_lock)
            {
                return [.. _journal];
            }
        }
    }

    /// <summary>How many requests the other side received, answered or not.</summary>
    public int Arrived => Journal.Count(c => c.Arrived);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var at = script.Clock.GetUtcNow();
        var behaviour = script.Next();
        string? body = null;
        if (behaviour is NetworkBehaviour.Healthy or NetworkBehaviour.ResetAfterWrite)
        {
            body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            _journal.Add(new(at, behaviour, request.Method, request.RequestUri, body));
        }

        switch (behaviour)
        {
            case NetworkBehaviour.Healthy:
                return answer?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}"),
                    RequestMessage = request,
                };
            case NetworkBehaviour.NameFails:
                throw ScriptedShapes.NameFails(request.RequestUri?.Host ?? "");
            case NetworkBehaviour.Refuses:
                throw ScriptedShapes.Refused();
            case NetworkBehaviour.NoRoute:
                throw ScriptedShapes.NoRoute();
            case NetworkBehaviour.ResetBeforeSend:
                throw ScriptedShapes.ResetBeforeSend();
            case NetworkBehaviour.ResetAfterWrite:
                throw ScriptedShapes.ResetAfterWrite();
            case NetworkBehaviour.Hangs:
                script.Clock.Advance(ClientTimeout);
                throw ScriptedShapes.ClientTimeout(ClientTimeout);
            default:
                throw new InvalidOperationException($"No shape for {behaviour}.");
        }
    }
}
