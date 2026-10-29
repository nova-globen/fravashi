using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Fravashi.Application.Network;
using Fravashi.Testing.Network;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Network;

/// <summary>
/// Backlog 228: what the real <c>SocketsHttpHandler</c> throws for each fault, MEASURED on loopback sockets rather
/// than assumed, and that <see cref="NetworkFailure"/> reads each the way plan @49 says. Plan @48 marked as INFERRED
/// the risk that a reset after the request was written comes back as a connection error, which the four provider
/// adapters' never-left rule would call safe to resend; the reset test here is the measurement. The scripted
/// handler's shapes are asserted equal to these, so a test that plays a fault plays what .NET really throws.
/// </summary>
public sealed class NetworkFailureShapesTests
{
    [Fact]
    public async Task A_closed_port_is_a_refused_connection_that_never_left()
    {
        var port = FreePort();
        using var client = new HttpClient();

        var thrown = await Catch(() => client.PostAsync(new Uri($"http://127.0.0.1:{port}/"), new StringContent("x")));

        var http = thrown.Should().BeOfType<HttpRequestException>().Subject;
        http.HttpRequestError.Should().Be(HttpRequestError.ConnectionError);
        var failure = NetworkFailure.Classify(thrown);
        failure.Kind.Should().Be(NetworkFailureKind.Refused);
        failure.NeverLeft.Should().BeTrue();
        NetworkFailure.Classify(ScriptedShapes.Refused()).Should().Be(failure);
    }

    [Fact]
    public async Task A_reset_after_the_whole_request_arrived_is_not_a_connection_error_and_left_with_no_answer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        const string body = "{\"create\":\"one issue\"}";

        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            var received = await ReadRequestAsync(socket, body);
            socket.LingerState = new LingerOption(true, 0); // close with RST, not FIN
            socket.Close();
            return received;
        });

        using var client = new HttpClient();
        var thrown = await Catch(() => client.PostAsync(new Uri($"http://127.0.0.1:{port}/"), new StringContent(body, Encoding.UTF8)));
        var received = await server;

        received.Should().Contain(body, "the server had the whole request before it reset");
        var http = thrown.Should().BeOfType<HttpRequestException>().Subject;
        http.HttpRequestError.Should().NotBe(HttpRequestError.ConnectionError);
        var failure = NetworkFailure.Classify(thrown);
        failure.Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer);
        failure.NeverLeft.Should().BeFalse();
        ((HttpRequestException)ScriptedShapes.ResetAfterWrite()).HttpRequestError.Should().Be(http.HttpRequestError);
        NetworkFailure.Classify(ScriptedShapes.ResetAfterWrite()).Kind.Should().Be(failure.Kind);
    }

    [Fact]
    public async Task The_clients_own_timeout_left_with_no_answer_and_the_callers_cancellation_is_not_network()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = Task.Run(async () =>
        {
            var socket = await listener.AcceptSocketAsync();
            return socket; // accepts, reads nothing, never answers
        });

        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(300) };
        var timedOut = await Catch(() => client.GetAsync(new Uri($"http://127.0.0.1:{port}/")));

        timedOut.Should().BeAssignableTo<OperationCanceledException>();
        NetworkFailure.Classify(timedOut).Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer);
        NetworkFailure.Classify(ScriptedShapes.ClientTimeout(client.Timeout)).Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer);

        using var patient = new HttpClient();
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var cancelled = await Catch(() => patient.GetAsync(new Uri($"http://127.0.0.1:{port}/"), caller.Token));

        NetworkFailure.Classify(cancelled, caller.Token).Kind.Should().Be(NetworkFailureKind.NotNetwork);
        (await accepted).Dispose();
    }

    [Fact]
    public async Task A_name_that_cannot_exist_is_a_name_resolution_failure()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // RFC 6761: a resolver answers no such name for .invalid; with no resolver at all it is EAI_AGAIN, the same kind.
        var thrown = await Catch(() => client.GetAsync(new Uri("http://netfault-228.invalid/")));

        thrown.Should().BeOfType<HttpRequestException>().Which.HttpRequestError.Should().Be(HttpRequestError.NameResolutionError);
        NetworkFailure.Classify(thrown).Kind.Should().Be(NetworkFailureKind.NameNotResolved);
        NetworkFailure.Classify(ScriptedShapes.NameFails("netfault-228.invalid")).Kind.Should().Be(NetworkFailureKind.NameNotResolved);
    }

    private static async Task<Exception> Catch(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("The call was expected to fail and it did not.");
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static async Task<string> ReadRequestAsync(Socket socket, string expectedBody)
    {
        var buffer = new byte[8192];
        var seen = new StringBuilder();
        while (!seen.ToString().Contains(expectedBody, StringComparison.Ordinal))
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None);
            if (read == 0)
            {
                break;
            }

            seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return seen.ToString();
    }
}
