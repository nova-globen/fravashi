using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Fravashi.AppRunner;
using Fravashi.Infrastructure.AppRuns;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AppRuns;

/// <summary>
/// Backlog 87: the studio's client against the REAL app runner server, over HTTP, with only the engine faked. The
/// fake engine opens a real listener where the relay would publish, so "ready" is measured by the same connection
/// test the product uses rather than asserted.
/// </summary>
public sealed class AppRunnerClientTests : IAsyncLifetime
{
    private const string Token = "runner-token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fravashi-apprunner-client", Guid.NewGuid().ToString("N"));
    private readonly ListeningEngine _engine = new();
    private WebApplication? _server;
    private AppRunnerClient _client = null!;

    public async Task InitializeAsync()
    {
        var shop = Path.Combine(_root, "shop");
        Directory.CreateDirectory(Path.Combine(shop, ".devcontainer"));
        await File.WriteAllTextAsync(Path.Combine(shop, ".devcontainer", "devcontainer.json"),
            """{ "image": "alpine:3.20", "customizations": { "fravashi": { "port": 8000 } } }""");
        var refused = Path.Combine(_root, "refused");
        Directory.CreateDirectory(refused);
        await File.WriteAllTextAsync(Path.Combine(refused, ".devcontainer.json"), """{ "image": "alpine", "privileged": true }""");

        var runner = new AppRunner.AppRunner(
            _engine,
            new AppRunnerOptions { ScratchDirectory = Path.Combine(_root, "scratch"), RelayImage = "fravashi:test", StudioContainer = "fravashi" },
            id => id switch { "ws-shop" => shop, "ws-refused" => refused, _ => null });
        _server = AppRunnerApplication.Create(Token, ["http://127.0.0.1:0"], runner);
        await _server.StartAsync();

        _client = new AppRunnerClient(new HttpClient(), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AppRunnerClient.UrlKey] = _server.Urls.First(),
            [AppRunnerClient.TokenKey] = Token,
        }).Build());
    }

    public async Task DisposeAsync()
    {
        _engine.Dispose();
        if (_server is not null)
        {
            await _server.StopAsync();
            await _server.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task The_client_builds_starts_waits_for_the_application_and_removes_it()
    {
        var run = await _client.StartAsync("ws-shop", CancellationToken.None);

        run.Started.Should().BeTrue(run.Failure);
        run.Url.Should().MatchRegex(@"^http://fravashi-run-r[0-9a-f]{12}-relay:8080$",
            "a step reaches the relay by name on the run's edge network, which the studio was joined to");
        _engine.Connected.Should().ContainSingle(c => c.Container == "fravashi");

        await _client.RemoveAsync(run, CancellationToken.None);

        _engine.Removed.Should().Contain(name => name.EndsWith("-relay", StringComparison.Ordinal));
        _engine.Disconnected.Should().ContainSingle(c => c.Container == "fravashi");
    }

    [Fact]
    public async Task A_studio_the_engine_will_not_join_to_the_run_is_told_so_in_the_engines_words()
    {
        // Backlog 270, as measured on rootless Podman: the studio ran in pasta mode and the step later failed on
        // "Could not resolve host: fravashi-run-...-relay", which a person had to decode.
        _engine.ConnectRefusal = "Error: \"pasta\" is not supported: invalid network mode";

        var run = await _client.StartAsync("ws-shop", CancellationToken.None);

        run.Started.Should().BeFalse();
        run.Failure.Should().Contain("the studio could not join the run's network").And.Contain("\"pasta\" is not supported");
        run.Failure.Should().NotContain("resolve");
        run.Failure.Should().Contain("pasta network mode").And.Contain("without a VPN sidecar");
    }

    [Fact]
    public async Task A_refused_spec_comes_back_as_the_fields_it_names()
    {
        var run = await _client.StartAsync("ws-refused", CancellationToken.None);

        run.Started.Should().BeFalse();
        run.Failure.Should().Contain("privileged");
    }

    [Fact]
    public async Task A_workspace_the_host_never_registered_is_said_to_be_one()
    {
        var run = await _client.StartAsync("ws-nobody", CancellationToken.None);

        run.Started.Should().BeFalse();
        run.Failure.Should().Contain("registered");
    }

    [Fact]
    public async Task A_client_with_no_app_runner_configured_says_so_without_calling_anything()
    {
        var bare = new AppRunnerClient(new HttpClient(), new ConfigurationBuilder().Build());

        bare.Available.Should().BeFalse();
        (await bare.StartAsync("ws-shop", CancellationToken.None)).Failure.Should().Contain("no app runner");
    }

    [Fact]
    public async Task A_run_comes_back_with_the_marker_minted_for_it_and_the_live_studios_token_never_travels()
    {
        // Backlog 226: the live studio's token is in this client's configuration on purpose; nothing may carry it.
        const string live = "live-studio-window-token-0123456789abcdef";
        var recorded = new RecordingHandler();
        var client = new AppRunnerClient(new HttpClient(recorded), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AppRunnerClient.UrlKey] = _server!.Urls.First(),
            [AppRunnerClient.TokenKey] = Token,
            ["FRAVASHI_WINDOW_TOKEN"] = live,
        }).Build());

        var run = await client.StartAsync("ws-shop", CancellationToken.None);

        run.Started.Should().BeTrue(run.Failure);
        var app = _engine.Templates.Single(t => t.Publish is null);
        var minted = app.SecretEnvironment[VerificationRunField.Name];
        minted.Should().MatchRegex("^[0-9a-f]{64}$").And.NotBe(live);
        app.SecretEnvironment[VerificationRunField.WindowTokenName].Should().Be(minted);

        run.Marker.Should().NotBeNull();
        run.Marker!.Covers(run.Url).Should().BeTrue();
        run.Marker.UserAgent("Mozilla/5.0").Should().Be($"Mozilla/5.0 FravashiWindow/1.0 tk/{minted}");
        recorded.Requests.Should().Contain(r => r.Path == "/apprunner/runs" && r.Body.Contains("\"verification\":true", StringComparison.Ordinal));
        recorded.Requests.Where(r => r.Path != "/apprunner/runs")
            .Should().OnlyContain(r => !r.Body.Contains("verification", StringComparison.Ordinal), "only the start asks for a verification run");
        recorded.Requests.Should().OnlyContain(r => !r.Text.Contains(live, StringComparison.Ordinal));
        _engine.Templates.Should().OnlyContain(t => !t.SecretEnvironment.Values.Contains(live) && !t.Environment.Values.Contains(live));
        run.ToString().Should().NotContain(minted);
        run.Marker.ToString().Should().NotContain(minted).And.Contain("withheld");

        await client.RemoveAsync(run, CancellationToken.None);
    }

    /// <summary>Keeps every request the client sends, whole: method, path, headers and body.</summary>
    private sealed class RecordingHandler() : DelegatingHandler(new HttpClientHandler())
    {
        public List<(string Path, string Body, string Text)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = string.Join("\n", request.Headers.Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));
            lock (Requests)
            {
                Requests.Add((request.RequestUri!.AbsolutePath, body, $"{request.Method} {request.RequestUri}\n{headers}\n{body}"));
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>An engine that answers every call, and LISTENS on a relay's published port the way a real relay would.</summary>
    private sealed class ListeningEngine : IEngine, IDisposable
    {
        private readonly List<TcpListener> _listeners = [];
        private readonly HashSet<string> _running = [];

        public List<ContainerTemplate> Templates { get; } = [];

        public List<(string Network, string Container)> Connected { get; } = [];
        public List<(string Network, string Container)> Disconnected { get; } = [];
        public List<string> Removed { get; } = [];

        public string Name => "listening";

        public Task<EngineOutcome> BuildAsync(EngineBuild build, CancellationToken cancellationToken) => Ok();

        public Task<EngineOutcome> CreateNetworkAsync(string name, bool isolated, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken) => Ok();

        public Task<EngineOutcome> StartContainerAsync(ContainerTemplate template, CancellationToken cancellationToken)
        {
            _running.Add(template.Name);
            Templates.Add(template);
            if (template.Publish is { } publish)
            {
                var listener = new TcpListener(IPAddress.Loopback, publish.HostPort);
                listener.Start();
                _listeners.Add(listener);
                _ = HoldAsync(listener);
            }

            return Ok();
        }

        /// <summary>Accepts and keeps connections open, which is what an application answering behind the relay looks like.</summary>
        private static async Task HoldAsync(TcpListener listener)
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync();
                    _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => client.Dispose(), TaskScheduler.Default);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
        }

        /// <summary>What the engine says when it refuses the studio's join (backlog 270); null joins it.</summary>
        public string? ConnectRefusal { get; set; }

        public Task<EngineOutcome> ConnectAsync(string network, string container, CancellationToken cancellationToken)
        {
            if (ConnectRefusal is { } refusal)
            {
                return Task.FromResult(new EngineOutcome(125, refusal));
            }

            Connected.Add((network, container));
            return Ok();
        }

        public Task<EngineOutcome> DisconnectAsync(string network, string container, CancellationToken cancellationToken)
        {
            Disconnected.Add((network, container));
            return Ok();
        }

        public Task<ContainerStatus> StatusAsync(string container, CancellationToken cancellationToken) =>
            Task.FromResult(new ContainerStatus(_running.Contains(container), _running.Contains(container), null));

        public Task<string> LogsAsync(string container, long maxBytes, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task<long?> WritableBytesAsync(string container, CancellationToken cancellationToken) => Task.FromResult<long?>(0);

        public Task<EngineOutcome> StopAsync(string container, CancellationToken cancellationToken) => Ok();

        public Task<EngineOutcome> RemoveContainerAsync(string container, CancellationToken cancellationToken)
        {
            _running.Remove(container);
            Removed.Add(container);
            return Ok();
        }

        public Task<EngineOutcome> RemoveNetworkAsync(string network, CancellationToken cancellationToken) => Ok();

        public Task<EngineOutcome> RemoveImageAsync(string image, CancellationToken cancellationToken) => Ok();

        public Task<IReadOnlyList<OwnedObject>> ListOwnedAsync(string label, string value, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OwnedObject>>([]);

        private static Task<EngineOutcome> Ok() => Task.FromResult(new EngineOutcome(0, string.Empty));

        public void Dispose()
        {
            foreach (var listener in _listeners)
            {
                listener.Stop();
            }
        }
    }
}
