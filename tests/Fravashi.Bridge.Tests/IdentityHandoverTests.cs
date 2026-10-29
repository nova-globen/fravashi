using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Bridge;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Fravashi.Bridge.Tests;

/// <summary>
/// Backlog 94: the host side of the sign-in hand-over. The studio publishes where it signs in (a file, so a fresh
/// fravashi.exe knows it before any container runs), and takes a session the launcher obtained, exactly once, from
/// memory. Nothing secret is ever written to disk here.
/// </summary>
public sealed class IdentityHandoverTests : IAsyncLifetime
{
    private const string Token = "identity-test-token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fravashi-identity-tests", Guid.NewGuid().ToString("N"));
    private IdentityHandover _identity = null!;
    private WebApplication? _app;
    private HttpClient _authorized = null!;
    private HttpClient _anonymous = null!;

    private static readonly HandedIdentity Handed = new(
        "https://idp.example.test", "fravashi-studio", "access-1", "refresh-1", "id-1", DateTimeOffset.UtcNow.AddHours(1));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _identity = new IdentityHandover(Path.Combine(_root, IdentityHandover.FileName));
        _app = BridgeApplication.Create(
            new BridgeServerOptions(Token, ["http://127.0.0.1:0"], Identity: _identity), new WorkspaceRegistry());
        await _app.StartAsync();
        var baseUrl = _app.Urls.First();
        _anonymous = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _authorized = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _authorized.DefaultRequestHeaders.Add(BridgeApplication.TokenHeader, Token);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _authorized.Dispose();
        _anonymous.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_handed_session_is_given_out_exactly_once()
    {
        _identity.Offer(Handed);

        _identity.HasWaiting.Should().BeTrue();
        _identity.Take().Should().Be(Handed);
        _identity.Take().Should().BeNull();
        _identity.HasWaiting.Should().BeFalse();
    }

    [Fact]
    public void A_handed_session_never_reaches_the_disk()
    {
        _identity.Publish(new SignInCoordinates("https://idp.example.test", "fravashi-studio", "openid", false, DateTimeOffset.UtcNow));
        _identity.Offer(Handed);

        var written = string.Concat(Directory.GetFiles(_root).Select(File.ReadAllText));
        written.Should().NotContain("access-1").And.NotContain("refresh-1").And.NotContain("id-1");
    }

    [Fact]
    public void The_coordinates_survive_a_new_process_and_a_blank_one_is_not_written()
    {
        _identity.Publish(new SignInCoordinates("https://idp.example.test", "fravashi-studio", "openid email", true, DateTimeOffset.UtcNow))
            .Should().BeTrue();
        _identity.Publish(new SignInCoordinates("", "", "", false, DateTimeOffset.UtcNow)).Should().BeFalse();

        var reread = new IdentityHandover(Path.Combine(_root, IdentityHandover.FileName)).Coordinates();

        reread.Should().NotBeNull();
        reread!.Issuer.Should().Be("https://idp.example.test");
        reread.Scope.Should().Be("openid email");
        reread.SessionUsable.Should().BeTrue();
    }

    [Fact]
    public void An_unreadable_coordinates_file_reads_as_none()
    {
        File.WriteAllText(Path.Combine(_root, IdentityHandover.FileName), "{ not json");

        _identity.Coordinates().Should().BeNull();
    }

    [Fact]
    public async Task Both_endpoints_need_the_bridge_token()
    {
        (await _anonymous.PostAsync("/bridge/identity/take", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _anonymous.PostAsJsonAsync("/bridge/identity/coordinates", new { issuer = "x", clientId = "y" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_studio_takes_the_session_once_over_the_bridge()
    {
        _identity.Offer(Handed);

        using var first = await _authorized.PostAsync("/bridge/identity/take", null);
        using var second = await _authorized.PostAsync("/bridge/identity/take", null);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var taken = await first.Content.ReadFromJsonAsync<HandedIdentity>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        taken!.AccessToken.Should().Be("access-1");
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_studio_publishes_its_coordinates_over_the_bridge()
    {
        using var response = await _authorized.PostAsJsonAsync("/bridge/identity/coordinates", new
        {
            issuer = "https://idp.example.test",
            clientId = "fravashi-studio",
            scope = "openid profile",
            sessionUsable = false,
        });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _identity.Coordinates()!.ClientId.Should().Be("fravashi-studio");
        _identity.Coordinates()!.SessionUsable.Should().BeFalse();
    }

    [Fact]
    public async Task A_bridge_with_no_hand_over_answers_404()
    {
        await using var bare = BridgeApplication.Create(new BridgeServerOptions(Token, ["http://127.0.0.1:0"]), new WorkspaceRegistry());
        await bare.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(bare.Urls.First()) };
        client.DefaultRequestHeaders.Add(BridgeApplication.TokenHeader, Token);

        (await client.PostAsync("/bridge/identity/take", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await bare.StopAsync();
    }
}
