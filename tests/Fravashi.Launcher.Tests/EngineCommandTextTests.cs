using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Backlog 133. Met in the owner's install on 2026-09-21: a failed <c>podman run</c> wrote its whole command
/// line into <c>launcher-*.log</c> and into the exception the splash shows, and that command line carries the
/// host-bridge token, the window token and the integrity key as <c>--env NAME=value</c>. The values below are
/// made up.
/// </summary>
public sealed class EngineCommandTextTests
{
    [Fact]
    public void Every_environment_value_is_masked_and_every_name_is_kept()
    {
        string[] args =
        [
            "run", "--detach", "--name", "fravashi",
            "--env", "FRAVASHI_BRIDGE_TOKEN=not-a-real-token-0123456789",
            "--env", "FRAVASHI_DATA_DIR=/data",
            "-e", "FRAVASHI_WINDOW_TOKEN=also-not-real",
            "--env=FRAVASHI_INTEGRITY_KEY=made-up-key",
            "fravashi:latest",
        ];

        var text = EngineCommandText.ForMessages("podman", args);

        text.Should().NotContain("not-a-real-token").And.NotContain("also-not-real").And.NotContain("made-up-key");
        text.Should().Contain("FRAVASHI_BRIDGE_TOKEN=***")
            .And.Contain("FRAVASHI_WINDOW_TOKEN=***")
            .And.Contain("FRAVASHI_INTEGRITY_KEY=***");

        // Masked whatever the name, on purpose: a list of "secret" names is a list somebody forgets to extend.
        text.Should().Contain("FRAVASHI_DATA_DIR=***");

        // What a person needs to understand the failure is still there.
        text.Should().StartWith("podman run --detach --name fravashi").And.EndWith("fravashi:latest");
    }

    [Fact]
    public void A_name_passed_through_without_a_value_is_left_as_it_is()
    {
        var text = EngineCommandText.ForMessages("docker", ["run", "--env", "ANTHROPIC_API_KEY", "image"]);

        text.Should().Be("docker run --env ANTHROPIC_API_KEY image");
    }

    [Fact]
    public void A_command_with_no_environment_reads_exactly_as_it_was_run() =>
        EngineCommandText.ForMessages("podman", ["rm", "--force", "fravashi"]).Should().Be("podman rm --force fravashi");
}

/// <summary>
/// The same thing through the REAL runtime class, because a helper nobody calls protects nothing. `dotnet`
/// stands in for the engine: `dotnet run --detach ...` fails at once, and what comes back is the exception the
/// launcher really throws, logs and shows.
/// </summary>
public sealed class FailedEngineCommandTests
{
    [Fact]
    public void A_failed_run_names_the_variables_it_passed_and_none_of_their_values()
    {
        var engine = new DockerCompatibleRuntime("stand-in", "dotnet");
        var spec = new ContainerRunSpec("fravashi", "fravashi:latest", 9090, 8080, @"C:\data")
        {
            WindowToken = "window-token-not-real-0123456789",
            BridgeUrl = "http://172.0.0.1:9091",
            BridgeToken = "bridge-token-not-real-0123456789",
        };

        var act = () => engine.RunContainer(spec);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("FRAVASHI_BRIDGE_TOKEN=***").And.Contain("FRAVASHI_WINDOW_TOKEN=***");
        message.Should().NotContain("bridge-token-not-real").And.NotContain("window-token-not-real");
    }
}

/// <summary>Backlog 134: the failure that took the owner's studio down after installing v0.9.25.</summary>
public sealed class EngineFailuresTests
{
    // The engine's sentence, verbatim from launcher-2026-09-21.log, with the id shortened.
    private const string NameHeld =
        "Error: creating container storage: the container name \"fravashi\" is already in use by f22cad0f528d. "
        + "You have to remove that container to be able to reuse that name: that name is already in use by an external entity";

    [Fact]
    public void A_name_held_by_a_leftover_in_storage_is_explained_with_what_clears_it_and_that_no_data_is_in_it()
    {
        var said = EngineFailures.Explain("podman", "fravashi", NameHeld);

        said.Should().NotBeNull();
        said.Should().Contain("podman rm -f fravashi");
        said.Should().Contain("None of your data is in it");
        said.Should().Contain("-type d -empty -delete", "the repair it names can only remove empty folders");
    }

    [Theory]
    [InlineData("Error: no such image fravashi:latest")]
    [InlineData("Error: the container name \"something-else\" is already in use by abc")]
    [InlineData("")]
    public void Anything_else_is_left_in_the_engines_own_words(string engineSaid) =>
        EngineFailures.Explain("podman", "fravashi", engineSaid).Should().BeNull();
}

