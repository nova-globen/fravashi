using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Fravashi.AppRunner;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Fravashi.AppRunner.Tests;

/// <summary>
/// Backlog 87: the app runner's operations against a recording engine. What a run is made of, in what order, what
/// every object is labelled with, what happens when a step fails, and what the sweep may and may not touch.
/// </summary>
public sealed class AppRunnerTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "fravashi-apprunner-tests", Guid.NewGuid().ToString("N"), "shop");
    private readonly string _scratch;
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));

    public AppRunnerTests()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, ".devcontainer"));
        File.WriteAllText(Path.Combine(_workspace, ".devcontainer", "devcontainer.json"),
            """{ "image": "docker.io/library/python:3.12-alpine", "customizations": { "fravashi": { "command": "python -m http.server 8000", "port": 8000 } } }""");
        File.WriteAllText(Path.Combine(_workspace, "index.html"), "<h1>shop</h1>");
        _scratch = Path.Combine(Path.GetDirectoryName(_workspace)!, "scratch");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_workspace)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    internal sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    internal sealed class RecordingEngine : IEngine
    {
        public List<string> Calls { get; } = [];
        public List<EngineBuild> Builds { get; } = [];
        public List<ContainerTemplate> Containers { get; } = [];
        public List<(string Name, bool Isolated, IReadOnlyDictionary<string, string> Labels)> Networks { get; } = [];
        public List<OwnedObject> Owned { get; } = [];
        public HashSet<string> Running { get; } = [];
        public string? FailOn { get; set; }
        public long? WritableBytes { get; set; }

        public string Name => "fake";

        private Task<EngineOutcome> Result(string call)
        {
            Calls.Add(call);
            return Task.FromResult(FailOn is { } fail && call.StartsWith(fail, StringComparison.Ordinal)
                ? new EngineOutcome(1, "engine said no")
                : new EngineOutcome(0, "ok"));
        }

        public Task<EngineOutcome> BuildAsync(EngineBuild build, CancellationToken cancellationToken)
        {
            Builds.Add(build);
            // The context must hold the source and must not hold the app runner's own Dockerfile.
            build.ContextDirectory.Should().NotBe(Path.GetDirectoryName(build.DockerfilePath));
            return Result("build " + build.Tag);
        }

        public Task<EngineOutcome> CreateNetworkAsync(string name, bool isolated, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken)
        {
            Networks.Add((name, isolated, labels));
            return Result("network " + name);
        }

        public Task<EngineOutcome> StartContainerAsync(ContainerTemplate template, CancellationToken cancellationToken)
        {
            Containers.Add(template);
            Running.Add(template.Name);
            return Result("start " + template.Name);
        }

        public Task<EngineOutcome> ConnectAsync(string network, string container, CancellationToken cancellationToken) => Result($"connect {network} {container}");

        public Task<EngineOutcome> DisconnectAsync(string network, string container, CancellationToken cancellationToken) => Result($"disconnect {network} {container}");

        public Task<ContainerStatus> StatusAsync(string container, CancellationToken cancellationToken) =>
            Task.FromResult(new ContainerStatus(Running.Contains(container), Running.Contains(container), null));

        public Task<string> LogsAsync(string container, long maxBytes, CancellationToken cancellationToken) =>
            Task.FromResult(new string('x', 40));

        public Task<long?> WritableBytesAsync(string container, CancellationToken cancellationToken) => Task.FromResult(WritableBytes);

        public Task<EngineOutcome> StopAsync(string container, CancellationToken cancellationToken)
        {
            Running.Remove(container);
            return Result("stop " + container);
        }

        public Task<EngineOutcome> RemoveContainerAsync(string container, CancellationToken cancellationToken)
        {
            Running.Remove(container);
            return Result("rm " + container);
        }

        public Task<EngineOutcome> RemoveNetworkAsync(string network, CancellationToken cancellationToken) => Result("network-rm " + network);

        public Task<EngineOutcome> RemoveImageAsync(string image, CancellationToken cancellationToken) => Result("rmi " + image);

        public Task<IReadOnlyList<OwnedObject>> ListOwnedAsync(string label, string value, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OwnedObject>>([.. Owned.Where(o => o.Labels.TryGetValue(label, out var v) && v == value)]);
    }

    private AppRunner Runner(RecordingEngine engine, string? studio = null) => new(
        engine,
        new AppRunnerOptions { ScratchDirectory = _scratch, RelayImage = "fravashi:0.9.31", StudioContainer = studio },
        id => id == "ws-shop" ? _workspace : null,
        _clock);

    [Fact]
    public async Task A_devcontainer_build_is_the_image_plus_the_source_copied_in_through_the_build()
    {
        var engine = new RecordingEngine();

        var built = await Runner(engine).BuildAsync("ws-shop", null, CancellationToken.None);

        built.Built.Should().BeTrue(built.Failure);
        built.EntryPort.Should().Be(8000);
        engine.Builds.Should().ContainSingle();
        engine.Builds[0].Tag.Should().Be($"fravashi-apprun:{built.BuildId}");
        engine.Builds[0].Labels.Should().ContainKeys(AppRunner.OwnerLabel, AppRunner.IdLabel, AppRunner.DeadlineLabel);
        Directory.Exists(engine.Builds[0].ContextDirectory).Should().BeFalse("an assembled context is deleted when its build ends");
    }

    [Fact]
    public async Task A_workspace_the_host_has_not_registered_is_not_built()
    {
        var built = await Runner(new RecordingEngine()).BuildAsync("somebody-elses", null, CancellationToken.None);

        built.Built.Should().BeFalse();
        built.Failure.Should().Contain("registered");
    }

    [Fact]
    public async Task A_refused_spec_reaches_no_engine()
    {
        File.WriteAllText(Path.Combine(_workspace, ".devcontainer", "devcontainer.json"), """{ "image": "alpine", "privileged": true }""");
        var engine = new RecordingEngine();

        var built = await Runner(engine).BuildAsync("ws-shop", null, CancellationToken.None);

        built.Refusals.Should().ContainSingle().Which.Field.Should().Be("privileged");
        engine.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_is_the_application_alone_on_an_isolated_network_and_a_relay_that_is_the_one_way_in()
    {
        var engine = new RecordingEngine();
        var runner = Runner(engine, studio: "fravashi-studio");
        var built = await runner.BuildAsync("ws-shop", null, CancellationToken.None);

        var run = await runner.StartAsync(built.BuildId!, null, null, CancellationToken.None);

        run.Started.Should().BeTrue(run.Refusal);
        engine.Networks.Should().HaveCount(2);
        var isolated = engine.Networks.Single(n => n.Isolated).Name;
        var edge = engine.Networks.Single(n => !n.Isolated).Name;
        var app = engine.Containers.Single(c => c.Name == $"fravashi-run-{run.RunId}");
        app.Networks.Should().Equal([isolated], "the application has no route anywhere but its own network");
        app.Publish.Should().BeNull("the application publishes nothing");
        var relay = engine.Containers.Single(c => c.Name.EndsWith("-relay", StringComparison.Ordinal));
        relay.Image.Should().Be("fravashi:0.9.31");
        relay.Entrypoint.Should().Be("socat");
        relay.Networks.Should().BeEquivalentTo([edge, isolated]);
        relay.Command.Should().Equal($"TCP-LISTEN:{AppRunner.RelayPort},fork,reuseaddr", $"TCP:{app.Name}:8000");
        relay.ReadOnlyRoot.Should().BeTrue();
        run.LoopbackUrl.Should().Be($"http://127.0.0.1:{relay.Publish!.HostPort}");
        run.EngineUrl.Should().Be($"http://{relay.Name}:{AppRunner.RelayPort}");
        engine.Calls.Should().Contain($"connect {edge} fravashi-studio");
        run.Deadline.Should().Be(_clock.GetUtcNow().AddMinutes(10));
        engine.Networks.Should().OnlyContain(n => n.Labels[AppRunner.DeadlineLabel] == run.Deadline!.Value.UtcDateTime.ToString("O"));
    }

    [Fact]
    public async Task A_step_that_fails_takes_everything_before_it_down()
    {
        var engine = new RecordingEngine();
        var runner = Runner(engine);
        var built = await runner.BuildAsync("ws-shop", null, CancellationToken.None);
        engine.FailOn = "start fravashi-run-";

        var run = await runner.StartAsync(built.BuildId!, null, null, CancellationToken.None);

        run.Started.Should().BeFalse();
        run.Refusal.Should().Contain("application");
        engine.Calls.Should().Contain(c => c.StartsWith("network-rm", StringComparison.Ordinal));
        (await runner.ListAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_studio_that_cannot_join_the_runs_network_refuses_the_start_in_the_engines_words()
    {
        // Backlog 270: rootless Podman refuses to join a pasta container to a network. The join used to be dropped and
        // the run handed out a relay NAME the studio could never resolve, so every step failed on a DNS error.
        var engine = new RecordingEngine();
        var runner = Runner(engine, studio: "fravashi");
        var built = await runner.BuildAsync("ws-shop", null, CancellationToken.None);
        engine.FailOn = "connect ";

        var run = await runner.StartAsync(built.BuildId!, null, null, CancellationToken.None);

        run.Started.Should().BeFalse();
        run.EngineUrl.Should().BeNull("a name the studio cannot resolve is never handed out");
        run.Refusal.Should().Contain("the studio could not join the run's network").And.Contain("engine said no");
        run.Refusal.Should().NotContain("recreates", "advice is given only for the measured pasta case");
        engine.Calls.Should().Contain(c => c.StartsWith("rm fravashi-run-", StringComparison.Ordinal), "the run is taken down");
        engine.Calls.Should().Contain(c => c.StartsWith("network-rm", StringComparison.Ordinal));
        engine.Calls.Should().NotContain(c => c.StartsWith("disconnect", StringComparison.Ordinal), "it never joined");
        (await runner.ListAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_caller_may_lower_a_ceiling_for_one_run_and_never_raise_one()
    {
        var engine = new RecordingEngine();
        var runner = Runner(engine);
        var built = await runner.BuildAsync("ws-shop", null, CancellationToken.None);

        var raised = await runner.StartAsync(built.BuildId!, null, new LimitsRequest(null, 99_999, null, null, null), CancellationToken.None);
        var lowered = await runner.StartAsync(built.BuildId!, null, new LimitsRequest(null, 256, null, null, 2), CancellationToken.None);

        raised.Started.Should().BeFalse();
        raised.Refusal.Should().Contain("only lower");
        lowered.Started.Should().BeTrue();
        engine.Containers.Single(c => c.Name == $"fravashi-run-{lowered.RunId}").Ceilings.MemoryMb.Should().Be(256);
        lowered.Deadline.Should().Be(_clock.GetUtcNow().AddMinutes(2));
    }

    [Fact]
    public async Task Ids_the_app_runner_never_minted_act_on_nothing()
    {
        var engine = new RecordingEngine();
        var runner = Runner(engine);

        (await runner.StartAsync("fravashi-studio", 80, null, CancellationToken.None)).Refusal.Should().Contain("no build");
        (await runner.RemoveAsync("fravashi-studio", CancellationToken.None)).Should().BeEmpty();
        await runner.StopAsync("fravashi-studio", CancellationToken.None);
        (await runner.ReadyAsync("fravashi-studio", CancellationToken.None)).Known.Should().BeFalse();
        engine.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_takes_the_run_down_and_is_idempotent()
    {
        var engine = new RecordingEngine();
        var runner = Runner(engine, studio: "fravashi-studio");
        var built = await runner.BuildAsync("ws-shop", null, CancellationToken.None);
        var run = await runner.StartAsync(built.BuildId!, null, null, CancellationToken.None);

        (await runner.RemoveAsync(run.RunId!, CancellationToken.None)).Should().BeEmpty();
        var callsAfterFirst = engine.Calls.Count;
        (await runner.RemoveAsync(run.RunId!, CancellationToken.None)).Should().BeEmpty();

        engine.Calls.Count.Should().Be(callsAfterFirst, "a second remove does nothing");
        engine.Calls.Should().Contain(c => c.StartsWith("disconnect", StringComparison.Ordinal), "the studio leaves the run's network first");
        (await runner.ListAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_sweep_removes_only_what_it_owns_and_only_once_its_deadline_has_passed()
    {
        var engine = new RecordingEngine();
        var past = _clock.GetUtcNow().AddMinutes(-1).UtcDateTime.ToString("O");
        var future = _clock.GetUtcNow().AddMinutes(5).UtcDateTime.ToString("O");
        Dictionary<string, string> Owned(string deadline) => new() { [AppRunner.OwnerLabel] = "default", [AppRunner.IdLabel] = "rold", [AppRunner.DeadlineLabel] = deadline };
        engine.Owned.AddRange(
        [
            new OwnedObject("image", "fravashi-apprun:bold", Owned(past)),
            new OwnedObject("network", "fravashi-run-rold", Owned(past)),
            new OwnedObject("container", "fravashi-run-rold", Owned(past)),
            new OwnedObject("container", "fravashi-run-rnew", Owned(future)),
            new OwnedObject("container", "someone-elses", new Dictionary<string, string> { [AppRunner.DeadlineLabel] = past }),
            new OwnedObject("container", "another-install", new Dictionary<string, string> { [AppRunner.OwnerLabel] = "other", [AppRunner.DeadlineLabel] = past }),
        ]);

        var stuck = await Runner(engine).SweepAsync(CancellationToken.None);

        stuck.Should().BeEmpty();
        engine.Calls.Should().Equal("rm fravashi-run-rold", "network-rm fravashi-run-rold", "rmi fravashi-apprun:bold");
    }

    [Fact]
    public async Task What_an_app_runner_left_before_the_rename_is_removed_whatever_its_deadline()
    {
        // The bridge release: those objects carry the FORMER owner label, which the sweep never matches.
        var engine = new RecordingEngine();
        var future = _clock.GetUtcNow().AddHours(1).UtcDateTime.ToString("O");
        Dictionary<string, string> Former() => new()
        {
            [Fravashi.Domain.FormerNames.AppRunnerOwnerLabel] = Fravashi.Domain.FormerNames.AppRunnerOwner,
            [AppRunner.DeadlineLabel] = future,
        };
        engine.Owned.AddRange(
        [
            new OwnedObject("image", "old-apprun:b1", Former()),
            new OwnedObject("network", "old-run-r1", Former()),
            new OwnedObject("container", "old-run-r1", Former()),
            new OwnedObject("container", "fravashi-run-r2", new Dictionary<string, string> { [AppRunner.OwnerLabel] = "default", [AppRunner.DeadlineLabel] = future }),
            new OwnedObject("container", "someone-elses", new Dictionary<string, string> { [AppRunner.DeadlineLabel] = future }),
        ]);

        var stuck = await Runner(engine).RemoveFormerLeftoversAsync(CancellationToken.None);

        stuck.Should().BeEmpty();
        engine.Calls.Should().Equal("rm old-run-r1", "network-rm old-run-r1", "rmi old-apprun:b1");
        (await Runner(engine).SweepAsync(CancellationToken.None)).Should().BeEmpty();
        engine.Calls.Should().NotContain("rm fravashi-run-r2", "the current runs keep their deadline");
    }

    [Fact]
    public async Task What_the_sweep_cannot_remove_is_reported_not_retried_in_silence()
    {
        var engine = new RecordingEngine { FailOn = "rm " };
        engine.Owned.Add(new OwnedObject("container", "fravashi-run-rstuck", new Dictionary<string, string>
        {
            [AppRunner.OwnerLabel] = "default",
            [AppRunner.DeadlineLabel] = _clock.GetUtcNow().AddMinutes(-1).UtcDateTime.ToString("O"),
        }));

        (await Runner(engine).SweepAsync(CancellationToken.None)).Should().Equal("container fravashi-run-rstuck");
    }

    [Fact]
    public async Task A_run_whose_writable_layer_passes_its_ceiling_is_stopped_and_says_why()
    {
        var engine = new RecordingEngine();
        var runner = Runner(engine);
        var built = await runner.BuildAsync("ws-shop", null, CancellationToken.None);
        var run = await runner.StartAsync(built.BuildId!, null, null, CancellationToken.None);
        engine.WritableBytes = 3L * 1024 * 1024 * 1024;

        await runner.SweepAsync(CancellationToken.None);

        engine.Calls.Should().Contain($"stop fravashi-run-{run.RunId}");
        (await runner.ReadyAsync(run.RunId!, CancellationToken.None)).Stopped.Should().Contain("writable layer");
    }

    [Fact]
    public async Task A_run_left_by_an_earlier_process_is_listed_as_an_orphan()
    {
        var engine = new RecordingEngine();
        engine.Owned.Add(new OwnedObject("container", "fravashi-run-rgone", new Dictionary<string, string>
        {
            [AppRunner.OwnerLabel] = "default",
            [AppRunner.IdLabel] = "rgone",
            [AppRunner.DeadlineLabel] = _clock.GetUtcNow().AddMinutes(3).UtcDateTime.ToString("O"),
        }));

        var listed = await Runner(engine).ListAsync(CancellationToken.None);

        listed.Should().ContainSingle().Which.State.Should().Be("orphaned");
    }

    // ---- the HTTP surface -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_api_needs_its_own_token_and_answers_a_refusal_with_the_fields_it_names()
    {
        File.WriteAllText(Path.Combine(_workspace, ".devcontainer", "devcontainer.json"), """{ "image": "alpine", "mounts": [] }""");
        await using var app = AppRunnerApplication.Create("runner-token", ["http://127.0.0.1:0"], Runner(new RecordingEngine()));
        await app.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        using var bridgeToken = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        bridgeToken.DefaultRequestHeaders.Add("X-Fravashi-Bridge", "runner-token");
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        client.DefaultRequestHeaders.Add(AppRunnerToken.Header, "runner-token");

        (await anonymous.GetAsync("/apprunner/runs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await bridgeToken.GetAsync("/apprunner/runs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the bridge's header is not this one's");
        var refused = await client.PostAsJsonAsync("/apprunner/builds", new { workspaceId = "ws-shop" });
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("\"field\":\"mounts\"");
        (await client.GetAsync("/apprunner/runs")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/apprunner/exec", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound, "there is no operation beyond the seven");
        await app.StopAsync();
    }
}
