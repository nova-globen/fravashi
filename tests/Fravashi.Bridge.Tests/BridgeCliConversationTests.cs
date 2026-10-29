using System.Net;
using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Fravashi.Bridge.Tests;

/// <summary>
/// Backlog 43: the read-only route over the host CLI's conversation folder for a REGISTERED workspace.
/// Everything is a temp tree: a fake projects root, a workspace folder, and synthetic JSONL files.
/// </summary>
public sealed class BridgeCliConversationTests : IAsyncLifetime
{
    private const string Token = "cli-conversations-test-token";
    private const long Cap = 64;

    private readonly string _base = Path.Combine(
        Path.GetTempPath(), "fravashi-bridge-cli-tests", Guid.NewGuid().ToString("N"));

    private readonly WorkspaceRegistry _registry = new();
    private WebApplication? _app;
    private HttpClient _anonymous = null!;
    private HttpClient _authorized = null!;
    private string _id = "";
    private string _projects = "";
    private string _folder = "";

    public async Task InitializeAsync()
    {
        // A space in the workspace path on purpose: the encoded folder must still be found.
        var workspace = Path.Combine(_base, "my workspace");
        Directory.CreateDirectory(workspace);
        _id = _registry.Register(workspace).Id;

        _projects = Path.Combine(_base, "projects");
        _folder = Path.Combine(_projects, CliProjectFolderName.Encode(_registry.TryGet(_id)!.RootPath));
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "aaaa-1111.jsonl"), "{\"type\":\"user\"}\n");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not a conversation");
        Directory.CreateDirectory(Path.Combine(_folder, "aaaa-1111"));
        File.WriteAllText(Path.Combine(_folder, "aaaa-1111", "nested.jsonl"), "{}");

        // A sibling workspace's conversation the route must never reach.
        var sibling = Path.Combine(_projects, "C--somewhere-else");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "secret.jsonl"), "{\"type\":\"assistant\"}");

        _app = BridgeApplication.Create(
            new BridgeServerOptions(
                Token, ["http://127.0.0.1:0"], CliProjectsRoot: _projects, CliConversationMaxBytes: Cap),
            _registry);
        await _app.StartAsync();
        var baseUrl = _app.Urls.First();
        _anonymous = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _authorized = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _authorized.DefaultRequestHeaders.Add(BridgeApplication.TokenHeader, Token);
    }

    public async Task DisposeAsync()
    {
        _anonymous.Dispose();
        _authorized.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Both_routes_need_the_token()
    {
        (await _anonymous.GetAsync($"/bridge/ws/{_id}/cli-conversations")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await _anonymous.GetAsync($"/bridge/ws/{_id}/cli-conversations/aaaa-1111.jsonl")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Lists_only_top_level_jsonl_files_in_the_workspace_folder()
    {
        var listed = await _authorized.GetFromJsonAsync<List<BridgeCliConversation>>(
            $"/bridge/ws/{_id}/cli-conversations");

        listed.Should().ContainSingle().Which.Name.Should().Be("aaaa-1111.jsonl");
        listed![0].Size.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Reads_a_listed_file_with_its_stamp()
    {
        var response = await _authorized.GetAsync($"/bridge/ws/{_id}/cli-conversations/aaaa-1111.jsonl");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"user\"");
        response.Headers.Contains(BridgeApplication.ModifiedUtcHeader).Should().BeTrue();
    }

    [Fact]
    public async Task An_unregistered_workspace_is_404_and_a_workspace_with_no_cli_history_lists_nothing()
    {
        (await _authorized.GetAsync("/bridge/ws/0000000000000000/cli-conversations")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        var other = Path.Combine(_base, "never-ran-the-cli");
        Directory.CreateDirectory(other);
        var otherId = _registry.Register(other).Id;
        (await _authorized.GetFromJsonAsync<List<BridgeCliConversation>>($"/bridge/ws/{otherId}/cli-conversations"))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("..%2FC--somewhere-else%2Fsecret.jsonl")]
    [InlineData("..%5CC--somewhere-else%5Csecret.jsonl")]
    [InlineData("aaaa-1111%2Fnested.jsonl")]
    [InlineData("notes.txt")]
    [InlineData("..jsonl")]
    [InlineData(".hidden.jsonl")]
    [InlineData("a.b.jsonl")]
    public async Task A_name_that_is_not_a_plain_conversation_file_is_refused(string escapedName)
    {
        var response = await _authorized.GetAsync($"/bridge/ws/{_id}/cli-conversations/{escapedName}");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("assistant");
    }

    [Fact]
    public async Task A_missing_conversation_is_404()
        => (await _authorized.GetAsync($"/bridge/ws/{_id}/cli-conversations/absent.jsonl")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public void The_service_refuses_names_with_separators_or_dots_before_touching_the_disk()
    {
        var service = new BridgeCliConversationService(_registry, _projects, Cap);

        foreach (var name in new[] { "../x.jsonl", @"..\x.jsonl", "x/y.jsonl", "x.jsonl\n", "x.JSONL.txt", "" })
        {
            var read = () => service.Read(_id, name);
            read.Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public void A_file_over_the_cap_is_refused_whole_rather_than_cut()
    {
        File.WriteAllText(Path.Combine(_folder, "big.jsonl"), new string('x', (int)Cap + 1), Encoding.ASCII);
        var service = new BridgeCliConversationService(_registry, _projects, Cap);

        var read = () => service.Read(_id, "big.jsonl");

        read.Should().Throw<BridgeCliConversationTooLargeException>();
        service.Read(_id, "aaaa-1111.jsonl").Bytes.Length.Should().BeLessThanOrEqualTo((int)Cap);
    }

    [Fact]
    public async Task The_route_answers_413_over_the_cap()
    {
        File.WriteAllText(Path.Combine(_folder, "huge.jsonl"), new string('x', (int)Cap + 1), Encoding.ASCII);

        (await _authorized.GetAsync($"/bridge/ws/{_id}/cli-conversations/huge.jsonl")).StatusCode
            .Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public void A_linked_conversation_file_is_neither_listed_nor_read()
    {
        var target = Path.Combine(_projects, "C--somewhere-else", "secret.jsonl");
        var link = Path.Combine(_folder, "linked.jsonl");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Creating a symlink needs Developer Mode or elevation on Windows; nothing to test without one.
            return;
        }

        var service = new BridgeCliConversationService(_registry, _projects, Cap);

        service.List(_id).Select(c => c.Name).Should().NotContain("linked.jsonl");
        var read = () => service.Read(_id, "linked.jsonl");
        read.Should().Throw<UnauthorizedAccessException>();
    }
}
