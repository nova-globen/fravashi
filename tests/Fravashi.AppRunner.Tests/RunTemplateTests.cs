using AwesomeAssertions;
using Fravashi.AppRunner;
using Xunit;

namespace Fravashi.AppRunner.Tests;

/// <summary>
/// ADR 0027: every engine argument comes from a FIXED template. These read exactly what an engine is told, and hold
/// the things it must never be told whatever a spec says.
/// </summary>
public sealed class RunTemplateTests
{
    private static ContainerTemplate App(LoopbackPublish? publish = null, string? user = null) => new(
        "fravashi-run-r1", "fravashi-apprun:b1", ["fravashi-run-r1"],
        new Dictionary<string, string> { [AppRunner.OwnerLabel] = "default" },
        new Dictionary<string, string> { ["MODE"] = "a b; --privileged" },
        user, ["/bin/sh", "-c", "serve --port 3000"], null, publish, Ceilings.Default, ReadOnlyRoot: false);

    [Fact]
    public void A_run_drops_every_capability_but_the_few_an_ordinary_image_needs()
    {
        var args = CliEngine.CreateArgs(App());

        args.Should().ContainInConsecutiveOrder("--cap-drop", "ALL");
        args.Should().ContainInConsecutiveOrder("--cap-add", "CHOWN,DAC_OVERRIDE,FOWNER,FSETID,KILL,NET_BIND_SERVICE,SETGID,SETUID");
        args.Should().ContainInConsecutiveOrder("--security-opt", "no-new-privileges");
        string.Join(' ', args).Should().NotContain("NET_RAW").And.NotContain("SYS_ADMIN").And.NotContain("MKNOD");
    }

    [Fact]
    public void Every_ceiling_is_on_the_command_line_and_swap_is_off()
    {
        var args = CliEngine.CreateArgs(App());

        args.Should().ContainInConsecutiveOrder("--cpus", "2");
        args.Should().ContainInConsecutiveOrder("--memory", "2048m");
        args.Should().ContainInConsecutiveOrder("--memory-swap", "2048m");
        args.Should().ContainInConsecutiveOrder("--pids-limit", "512");
    }

    [Fact]
    public void Nothing_that_reaches_the_host_is_ever_in_a_run()
    {
        var args = CliEngine.CreateArgs(App(new LoopbackPublish(49152, 8080), user: "1000"));

        foreach (var forbidden in new[] { "--privileged", "--volume", "-v", "--mount", "--device", "--pid", "--ipc", "--uts", "--userns", "--add-host" })
        {
            args.Should().NotContain(forbidden);
        }

        args.Should().NotContain(a => a.StartsWith("--network=host", StringComparison.Ordinal) || a == "host");
        args.Where(a => a.Contains("127.0.0.1:", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().Be("127.0.0.1:49152:8080", "the one way in is published on loopback, on a port the app runner picked");
    }

    [Fact]
    public void A_value_is_one_argument_whatever_it_contains()
    {
        var args = CliEngine.CreateArgs(App());

        args.Should().ContainInConsecutiveOrder("--env", "MODE=a b; --privileged");
        args.Should().NotContain("--privileged");
    }

    [Fact]
    public void An_isolated_network_has_no_route_out()
    {
        CliEngine.NetworkCreateArgs("n1", isolated: true, new Dictionary<string, string>()).Should().Contain("--internal");
        CliEngine.NetworkCreateArgs("n1", isolated: false, new Dictionary<string, string>()).Should().NotContain("--internal");
    }

    [Fact]
    public void A_build_takes_only_what_the_app_runner_decided()
    {
        var args = CliEngine.BuildArgs(new EngineBuild(
            "/tmp/ctx", "/tmp/ctx/Dockerfile", "fravashi-apprun:b1", "dev",
            new Dictionary<string, string> { ["NODE_ENV"] = "test" },
            new Dictionary<string, string> { [AppRunner.OwnerLabel] = "default" }, TimeSpan.FromMinutes(15)));

        args.Should().Equal(
            "build", "--file", "/tmp/ctx/Dockerfile", "--tag", "fravashi-apprun:b1", "--target", "dev",
            "--build-arg", "NODE_ENV=test", "--label", $"{AppRunner.OwnerLabel}=default", "/tmp/ctx");
    }

    [Fact]
    public void The_application_layer_copies_the_source_in_and_nothing_else()
    {
        var spec = new RunSpec(".devcontainer/devcontainer.json", "alpine:3.20", null, string.Empty, null,
            new Dictionary<string, string>(), null, 8000, "1000:1000", new Dictionary<string, string>(),
            "/workspaces/shop", Ceilings.Default, []);

        AppRunner.LayerDockerfile("alpine:3.20", spec).Should().Be(
            "FROM alpine:3.20\nCOPY --chown=1000:1000 . /workspaces/shop\nWORKDIR /workspaces/shop\n");
    }
}
