using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Backlog 190: what a <c>load</c> says it named, and the name the launcher creates from being made to answer to it.
/// </summary>
public sealed class LoadedImageNameTests
{
    [Fact]
    public void Parse_reads_the_line_printed_by_wslc()
    {
        // Measured 2026-10-05, wslc 3.0.1: `wslc load --input <tar.gz>` printed exactly this.
        LoadedImages.Parse("Loaded image: localhost/fravashi:latest\n")
            .Should().Equal("localhost/fravashi:latest");
    }

    [Fact]
    public void Parse_reads_docker_and_both_podman_shapes_and_skips_an_untagged_id()
    {
        LoadedImages.Parse("abc: Loading layer  1.2MB/1.2MB\r\nLoaded image: fravashi:latest\r\n")
            .Should().Equal("fravashi:latest");
        LoadedImages.Parse("Loaded image(s): localhost/fravashi:latest, localhost/fravashi:1.0")
            .Should().Equal("localhost/fravashi:latest", "localhost/fravashi:1.0");
        LoadedImages.Parse("Loaded image ID: sha256:0123").Should().BeEmpty();
        LoadedImages.Parse("").Should().BeEmpty();
        LoadedImages.Parse(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("localhost/fravashi:latest", "fravashi:latest")]
    [InlineData("docker.io/library/fravashi:latest", "fravashi:latest")]
    [InlineData("registry.example:5000/team/fravashi:1", "team/fravashi:1")]
    [InlineData("team/fravashi:1", "team/fravashi:1")]
    [InlineData("fravashi:latest", "fravashi:latest")]
    public void Unqualified_drops_only_a_registry(string reference, string expected) =>
        LoadedImageName.Unqualified(reference).Should().Be(expected);

    [Fact]
    public void Answer_falls_back_to_the_localhost_guess_only_when_the_engine_printed_nothing_and_it_resolves()
    {
        var runtime = new FakeContainerRuntime { LoadedImageId = "sha256:new" };
        runtime.Names["fravashi:latest"] = "sha256:old";
        runtime.Names["localhost/fravashi:latest"] = "sha256:new";
        var log = new List<string>();

        LoadedImageName.Answer(runtime, [], "fravashi:latest", log.Add).Should().Be("sha256:new");

        runtime.Tags.Should().Equal(("localhost/fravashi:latest", "fravashi:latest"));
        log.Should().ContainSingle().Which.Should().Contain("also named it fravashi:latest");
    }

    [Fact]
    public void Answer_tags_nothing_when_the_target_already_names_the_loaded_image()
    {
        var runtime = new FakeContainerRuntime();
        runtime.Names["fravashi:latest"] = "sha256:new";
        runtime.Names["localhost/fravashi:latest"] = "new";

        LoadedImageName.Answer(runtime, ["localhost/fravashi:latest"], "fravashi:latest", _ => { })
            .Should().Be("new");

        runtime.Tags.Should().BeEmpty("an id with and without its sha256: prefix is the same image");
    }
}
