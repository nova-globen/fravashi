using System.Net;
using System.Text;

namespace Fravashi.Infrastructure.Tests;

/// <summary>A test handler that answers requests from a responder keyed on the request.</summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> responder)
    : HttpMessageHandler
{
    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        var (status, body) = responder(request);
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
