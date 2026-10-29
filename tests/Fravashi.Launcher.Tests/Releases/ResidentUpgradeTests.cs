using System.Net;
using System.Text;
using Fravashi.Launcher.Releases;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests.Releases;

/// <summary>
/// Backlog 157: a console <c>fravashi update</c> hands the update to the running fravashi.exe's upgrade operation. Driven over a
/// scripted HTTP handler, never a socket, so no test can reach the real fravashi.exe on the machine it runs on.
/// </summary>
public sealed class ResidentUpgradeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fravashi-resident-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public ResidentUpgradeTests() => Directory.CreateDirectory(_directory);

    private HttpResidentUpgrade Resident(Scripted handler, int? port = 9097) =>
        new(_directory, () => port, new HttpClient(handler), pollInterval: TimeSpan.Zero, patience: TimeSpan.FromSeconds(5));

    private void WriteToken() => File.WriteAllText(Path.Combine(_directory, HttpResidentUpgrade.TokenFileName), "made-up-token");

    [Fact]
    public void No_recorded_port_or_no_token_means_there_is_nobody_to_hand_it_to()
    {
        var handler = new Scripted();

        Resident(handler, port: null).TryApply("1.1.0", _log.Add).Should().BeNull();
        Resident(handler).TryApply("1.1.0", _log.Add).Should().BeNull("this install has minted no upgrade token");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Nothing_listening_or_something_else_answering_falls_back()
    {
        WriteToken();

        Resident(new Scripted { Fail = true }).TryApply("1.1.0", _log.Add).Should().BeNull();
        Resident(new Scripted(new HttpResponseMessage(HttpStatusCode.Unauthorized))).TryApply("1.1.0", _log.Add).Should().BeNull();
    }

    [Fact]
    public void A_refusal_is_said_and_not_worked_around()
    {
        WriteToken();
        var refused = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("""{"error":"The update channel offers 1.2.0, not 1.1.0."}""", Encoding.UTF8, "application/json"),
        };

        var result = Resident(new Scripted(refused)).TryApply("1.1.0", _log.Add);

        result.Should().Be(new ResidentUpgradeResult(false, "The update channel offers 1.2.0, not 1.1.0."));
    }

    [Fact]
    public void An_accepted_update_is_followed_to_its_end_with_this_installs_token()
    {
        WriteToken();
        var handler = new Scripted(
            new HttpResponseMessage(HttpStatusCode.Accepted),
            Json("""{"state":"Running","version":"1.1.0","said":"Upgrading from 1.0.0."}"""),
            Json("""{"state":"Succeeded","version":"1.1.0","said":"Updated to 1.1.0. Fravashi is running the new version on port 9090."}"""));

        var result = Resident(handler).TryApply("1.1.0", _log.Add);

        result.Should().Be(new ResidentUpgradeResult(true, "Updated to 1.1.0. Fravashi is running the new version on port 9090."));
        handler.Requests.Should().Equal("POST /upgrade/apply", "GET /upgrade/status", "GET /upgrade/status");
        handler.Tokens.Should().AllBe("made-up-token");
        handler.Hosts.Should().AllBe("127.0.0.1:9097");
        _log.Should().Contain(l => l.StartsWith("Handed the update to the running Fravashi", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Answers each request with the next scripted response, and records what was asked.</summary>
    private sealed class Scripted(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _next;

        public bool Fail { get; init; }

        public List<string> Requests { get; } = [];

        public List<string> Tokens { get; } = [];

        public List<string> Hosts { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
            }

            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            Tokens.Add(request.Headers.GetValues(HttpResidentUpgrade.Header).Single());
            Hosts.Add(request.RequestUri.Authority);
            return _next < responses.Length ? responses[_next++] : responses[^1];
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }
}
