using System.Net;
using AwesomeAssertions;
using Fravashi.AppRunner;
using Xunit;
using Xunit.Abstractions;

namespace Fravashi.AppRunner.Tests;

/// <summary>
/// Backlog 87 against a REAL engine: two projects that are not this one are built, started, reached through the
/// relay, checked for the one property that matters most (the application has no internet), and removed, each
/// twice, with nothing left behind. It runs when <see cref="EngineVariable"/> names an engine executable
/// (<c>podman</c> or <c>docker</c>) and skips with its reason otherwise; it never searches for one. It pulls two small
/// public images at BUILD time, which is where fetching belongs, and builds a throwaway relay image with socat,
/// standing in for the studio image that carries it in production.
/// </summary>
[Collection(ProcessEnvironment.Collection)]
public sealed class AppRunnerLiveTests(ITestOutputHelper output) : IDisposable
{
    public const string EngineVariable = "FRAVASHI_APPRUNNER_LIVE_ENGINE";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fravashi-apprunner-live", Guid.NewGuid().ToString("N"));
    private readonly string _owner = "livetest-" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Project(string name, params (string Path, string Content)[] files)
    {
        var folder = Path.Combine(_root, name);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return folder;
    }

    [LiveEngineFact]
    public async Task Two_projects_run_repeatedly_through_the_relay_with_no_internet_and_leave_nothing_behind()
    {
        var engineExe = Environment.GetEnvironmentVariable(EngineVariable)!;
        var engine = new CliEngine(engineExe);

        // The relay: in production the studio's own image, which carries socat. Here a throwaway one.
        var relayDir = Project("relay", ("Dockerfile", "FROM docker.io/library/alpine:3.20\nRUN apk add --no-cache socat\n"));
        var relayTag = "fravashi-apprunner-live-relay:" + _owner;
        var relay = await engine.BuildAsync(new EngineBuild(relayDir, Path.Combine(relayDir, "Dockerfile"), relayTag, null,
            new Dictionary<string, string>(), new Dictionary<string, string>(), TimeSpan.FromMinutes(10)), CancellationToken.None);
        relay.Ok.Should().BeTrue(relay.Output);

        // Project one: a devcontainer on a public image. Its start command tries the internet FIRST and writes down
        // whether it got there, so the answer comes out through the product's own front door.
        var shop = Project("shop",
            (".devcontainer/devcontainer.json", """
                {
                  "name": "shop",
                  "image": "docker.io/library/python:3.12-alpine",
                  "customizations": {
                    "vscode": { "extensions": [] },
                    "fravashi": {
                      "command": "wget -q -T 4 -O /dev/null http://example.com && echo reached > egress.txt; exec python -m http.server 8000",
                      "port": 8000
                    }
                  }
                }
                """),
            ("index.html", "<h1>the shop front</h1>"));

        // Project two: a bare Dockerfile, the application as its author built it. nginx starts as root and switches
        // to its own user, which is exactly what the kept capability set exists for.
        var blog = Project("blog",
            ("Dockerfile", "FROM docker.io/library/nginx:alpine\nCOPY index.html /usr/share/nginx/html/index.html\nEXPOSE 80\n"),
            ("index.html", "<h1>the blog</h1>"));

        var workspaces = new Dictionary<string, string> { ["ws-shop"] = shop, ["ws-blog"] = blog };
        var runner = new AppRunner(
            engine,
            new AppRunnerOptions { ScratchDirectory = Path.Combine(_root, "scratch"), RelayImage = relayTag, Owner = _owner },
            id => workspaces.GetValueOrDefault(id));

        var images = new List<string>();
        try
        {
            foreach (var (workspace, expected) in new[] { ("ws-shop", "the shop front"), ("ws-blog", "the blog") })
            {
                var built = await runner.BuildAsync(workspace, null, CancellationToken.None);
                built.Built.Should().BeTrue($"{workspace}: {built.Failure} {string.Join("; ", built.Refusals)}\n{built.Output}");
                images.Add($"fravashi-apprun:{built.BuildId}");
                images.Add($"fravashi-apprun:{built.BuildId}-base");
                output.WriteLine($"{workspace}: built {built.BuildId}, entry port {built.EntryPort}, not read [{string.Join(", ", built.NotRead)}]");

                for (var round = 1; round <= 2; round++)
                {
                    var run = await runner.StartAsync(built.BuildId!, null, null, CancellationToken.None);
                    run.Started.Should().BeTrue($"{workspace} round {round}: {run.Refusal}\n{run.Output}");

                    var ready = false;
                    for (var wait = 0; wait < 90 && !ready; wait++)
                    {
                        ready = (await runner.ReadyAsync(run.RunId!, CancellationToken.None)).Ready;
                        if (!ready)
                        {
                            await Task.Delay(1000);
                        }
                    }

                    var logs = await runner.LogsAsync(run.RunId!, 0, CancellationToken.None);
                    ready.Should().BeTrue($"{workspace} round {round} never became ready; its output:\n{logs.Text}");

                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var page = await http.GetStringAsync(run.LoopbackUrl + "/");
                    page.Should().Contain(expected);
                    output.WriteLine($"{workspace} round {round}: {run.RunId} answered at {run.LoopbackUrl}");

                    if (workspace == "ws-shop")
                    {
                        var egress = await http.GetAsync(run.LoopbackUrl + "/egress.txt");
                        egress.StatusCode.Should().Be(HttpStatusCode.NotFound, "the application reached the internet, which a run must not");
                    }

                    (await runner.RemoveAsync(run.RunId!, CancellationToken.None)).Should().BeEmpty();
                    (await runner.ReadyAsync(run.RunId!, CancellationToken.None)).Known.Should().BeFalse();
                }
            }

            var left = await engine.ListOwnedAsync(AppRunner.OwnerLabel, _owner, CancellationToken.None);
            left.Where(o => o.Kind is "container" or "network").Should().BeEmpty("a removed run leaves no container and no network");
        }
        finally
        {
            foreach (var image in images.Append(relayTag))
            {
                await engine.RemoveImageAsync(image, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Backlog 226 against a REAL engine: the field reaches a verification run's application through <c>--env NAME</c>
    /// with the value in the engine command's own environment, the run's minted token wins over a different value this
    /// process carries, and an ordinary run gets neither. The application writes what its environment holds into files
    /// it serves, so the answer comes out through the relay.
    /// </summary>
    [LiveEngineFact]
    public async Task A_verification_run_receives_its_minted_token_through_the_engine_and_an_ordinary_run_receives_nothing()
    {
        var engineExe = Environment.GetEnvironmentVariable(EngineVariable)!;
        var engine = new CliEngine(engineExe);
        var relayDir = Project("relay", ("Dockerfile", "FROM docker.io/library/alpine:3.20\nRUN apk add --no-cache socat\n"));
        var relayTag = "fravashi-apprunner-live-relay:" + _owner;
        (await engine.BuildAsync(new EngineBuild(relayDir, Path.Combine(relayDir, "Dockerfile"), relayTag, null,
            new Dictionary<string, string>(), new Dictionary<string, string>(), TimeSpan.FromMinutes(10)), CancellationToken.None)).Ok.Should().BeTrue();

        var echo = Project("echo",
            (".devcontainer/devcontainer.json", """
                {
                  "image": "docker.io/library/python:3.12-alpine",
                  "customizations": { "fravashi": {
                    "command": "printf '%s' \"$FRAVASHI_VERIFICATION_RUN\" > flag.txt; printf '%s' \"$FRAVASHI_WINDOW_TOKEN\" > window.txt; exec python -m http.server 8000",
                    "port": 8000 } }
                }
                """));
        var runner = new AppRunner(
            engine,
            new AppRunnerOptions { ScratchDirectory = Path.Combine(_root, "scratch"), RelayImage = relayTag, Owner = _owner },
            id => id == "ws-echo" ? echo : null);

        const string live = "live-studio-token-this-process-carries";
        var before = Environment.GetEnvironmentVariable(VerificationRunField.WindowTokenName);
        Environment.SetEnvironmentVariable(VerificationRunField.WindowTokenName, live);
        var images = new List<string> { relayTag };
        try
        {
            var built = await runner.BuildAsync("ws-echo", null, CancellationToken.None);
            built.Built.Should().BeTrue($"{built.Failure} {string.Join("; ", built.Refusals)}\n{built.Output}");
            images.Add($"fravashi-apprun:{built.BuildId}");

            foreach (var verification in new[] { true, false })
            {
                var run = await runner.StartAsync(built.BuildId!, null, null, CancellationToken.None, verification);
                run.Started.Should().BeTrue($"{run.Refusal}\n{run.Output}");
                var ready = false;
                for (var wait = 0; wait < 90 && !ready; wait++)
                {
                    ready = (await runner.ReadyAsync(run.RunId!, CancellationToken.None)).Ready;
                    if (!ready)
                    {
                        await Task.Delay(1000);
                    }
                }

                ready.Should().BeTrue((await runner.LogsAsync(run.RunId!, 0, CancellationToken.None)).Text);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var flag = await http.GetStringAsync(run.LoopbackUrl + "/flag.txt");
                var window = await http.GetStringAsync(run.LoopbackUrl + "/window.txt");
                if (verification)
                {
                    flag.Should().Be(run.WindowToken!.Value, "the field arrived through the engine with the minted value");
                    window.Should().Be(run.WindowToken.Value, "the run's token, never the one this process carries");
                }
                else
                {
                    flag.Should().BeEmpty();
                    window.Should().BeEmpty("an ordinary run is given no window token, not even one inherited from fravashi.exe");
                }

                output.WriteLine($"verification={verification}: flag {(flag.Length == 0 ? "absent" : "present")}, window token {(window.Length == 0 ? "absent" : "present")}");
                (await runner.RemoveAsync(run.RunId!, CancellationToken.None)).Should().BeEmpty();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(VerificationRunField.WindowTokenName, before);
            foreach (var image in images)
            {
                await engine.RemoveImageAsync(image, CancellationToken.None);
            }
        }
    }
}

/// <summary>A fact that runs only with a named engine, set in the constructor because Assert.Skip needs xunit v3.</summary>
internal sealed class LiveEngineFactAttribute : FactAttribute
{
    public LiveEngineFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppRunnerLiveTests.EngineVariable)))
        {
            Skip = $"Live engine tier: set {AppRunnerLiveTests.EngineVariable} to podman or docker to run it.";
        }
    }
}
