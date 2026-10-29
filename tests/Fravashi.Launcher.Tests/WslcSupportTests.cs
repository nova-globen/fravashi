using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// What running on wslc alone needs from the launcher (ADR 0028, measured on the dev host with wslc 3.0.1 on 2026-10-04):
/// finding the executable off PATH, the loopback-only listeners, one image id in two spellings, and the JSON wslc
/// prints where Docker takes Go templates.
/// </summary>
public sealed class WslcSupportTests
{
    // ---- the executable ----

    // Folder names without a drive colon: on Linux, where CI runs, PATH is split on ':'.
    private static readonly string Tools = Path.Combine("root", "tools");
    private static readonly string OnPath = Path.Combine("root", "wslbin");
    private static readonly string ProgramFiles = Path.Combine("root", "Program Files");
    private static readonly string Installed = Path.Combine(ProgramFiles, "WSL", "wslc.exe");

    [Fact]
    public void Wslc_on_PATH_wins_and_is_used_by_its_bare_name()
    {
        var path = string.Join(Path.PathSeparator, Tools, OnPath);
        WslcExecutable.Resolve(path, ProgramFiles, f => f == Path.Combine(OnPath, "wslc.exe") || f == Installed)
            .Should().Be("wslc", "a person who put it on PATH keeps theirs");
    }

    [Fact]
    public void Wslc_off_PATH_is_found_where_WSL_installs_it()
    {
        WslcExecutable.Resolve(Tools, ProgramFiles, f => f == Installed)
            .Should().Be(Installed, "WSL does not put its folder on PATH, which made the detector report wslc absent");
    }

    [Fact]
    public void No_wslc_anywhere_leaves_the_bare_name_so_the_probe_says_missing()
    {
        WslcExecutable.Resolve(Tools, ProgramFiles, _ => false).Should().Be("wslc");
        WslcExecutable.Resolve(string.Empty, programFiles: string.Empty, _ => false).Should().Be("wslc");
    }

    // ---- where the listeners bind ----

    [Fact]
    public void On_wslc_the_listeners_bind_loopback_only_and_containers_use_the_wslc_host_name()
    {
        var endpoint = ContainerHostEndpoint.For(RuntimeNames.Wslc, () => "172.28.240.1")!;

        endpoint.ListenUrls(9091).Should().Equal("http://127.0.0.1:9091");
        endpoint.ContainerUrl(9091).Should().Be("http://host.wslc.internal:9091");
    }

    [Fact]
    public void On_wslc_no_WSL_address_is_needed()
    {
        ContainerHostEndpoint.For(RuntimeNames.Wslc, () => null).Should().NotBeNull();
    }

    [Theory]
    [InlineData(RuntimeNames.Docker)]
    [InlineData(RuntimeNames.Podman)]
    public void Docker_and_podman_keep_loopback_plus_the_WSL_address(string runtime)
    {
        var endpoint = ContainerHostEndpoint.For(runtime, () => "172.28.240.1")!;

        endpoint.ListenUrls(9091).Should().Equal("http://127.0.0.1:9091", "http://172.28.240.1:9091");
        endpoint.ContainerUrl(9091).Should().Be("http://172.28.240.1:9091");
    }

    [Fact]
    public void Docker_or_podman_without_a_WSL_address_has_no_container_endpoint()
    {
        ContainerHostEndpoint.For(RuntimeNames.Podman, () => null).Should().BeNull();
    }

    // ---- one image id, two spellings ----

    [Theory]
    [InlineData("94f1fa54a1ff786c846942b7445e3a9fc889a3df22853c6e8d4741a5a92702f5",
        "sha256:94f1fa54a1ff786c846942b7445e3a9fc889a3df22853c6e8d4741a5a92702f5")]
    [InlineData("sha256:ABC123", "sha256:abc123")]
    [InlineData("abc123", "abc123")]
    public void The_same_image_matches_in_either_spelling(string left, string right)
    {
        ImageIds.Same(left, right).Should().BeTrue();
        ImageIds.Same(right, left).Should().BeTrue();
    }

    [Theory]
    [InlineData("sha256:abc123", "sha256:abc124")]
    [InlineData("abc123", "sha256:abc1234")]
    [InlineData("sha256:", "")]
    [InlineData(null, "abc")]
    [InlineData("abc", null)]
    public void A_different_or_missing_id_never_matches(string? left, string? right)
    {
        ImageIds.Same(left, right).Should().BeFalse("the integrity pin must still fail closed on any other image");
    }

    // ---- reading wslc's JSON (shapes recorded from wslc 3.0.1) ----

    private const string ImageInspect = """
        [{"Architecture":"amd64","Config":{"Labels":{"org.fravashi.version":"0.9.49","org.fravashi.commit":"eb1bde0"}},
          "Id":"sha256:94f1fa54a1ff786c846942b7445e3a9fc889a3df22853c6e8d4741a5a92702f5",
          "RepoTags":["localhost/fravashi:latest","fravashi:latest"],"Size":2260000000}]
        """;

    private const string ContainerInspect = """
        [{"Config":{},"Id":"407acba7fb9d","Image":"sha256:94f1fa54a1ff786c846942b7445e3a9fc889a3df22853c6e8d4741a5a92702f5",
          "Name":"fravashi","State":{"Running":true,"Status":"running"}}]
        """;

    [Fact]
    public void The_tags_naming_an_image_are_read()
    {
        WslcRuntime.ReadRepoTags(ImageInspect).Should().Equal("localhost/fravashi:latest", "fravashi:latest");
    }

    [Fact]
    public void The_labels_are_read()
    {
        WslcRuntime.ReadLabel(ImageInspect, ImageLabels.Version).Should().Be("0.9.49");
        WslcRuntime.ReadLabel(ImageInspect, ImageLabels.NoPrivilegeRaisingBinaries)
            .Should().BeNull("an image built before the strip step carries no such stamp");
    }

    // Backlog 251: the variant label, read through the same inspect as the version.
    [Fact]
    public void The_variant_label_is_read_from_the_image_inspect_wslc_answers()
    {
        WslcRuntime.BuildImageInspectArgs("fravashi:latest").Should().Equal("inspect", "--type", "image", "fravashi:latest");

        const string corp = """
            [{"Config":{"Labels":{"org.fravashi.version":"0.9.64","org.fravashi.variant":"corp-broker"}},"Id":"sha256:abc"}]
            """;
        WslcRuntime.ReadLabel(corp, ImageLabels.Variant).Should().Be("corp-broker");
        WslcRuntime.ReadLabel(ImageInspect, ImageLabels.Variant).Should().BeNull("an image without the label says nothing");
        WslcRuntime.ReadLabel("""[{"Config":{"Labels":{"org.fravashi.variant":" "}}}]""", ImageLabels.Variant)
            .Should().BeNull("an empty label is not a variant");
    }

    [Fact]
    public void The_image_a_container_was_made_from_is_read()
    {
        WslcRuntime.ReadString(ContainerInspect, "Image")
            .Should().Be("sha256:94f1fa54a1ff786c846942b7445e3a9fc889a3df22853c6e8d4741a5a92702f5");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("Object not found: nothing-here")]
    [InlineData("""[{"Config":{"Labels":null},"RepoTags":null}]""")]
    [InlineData("")]
    public void Anything_else_reads_as_cannot_say(string answer)
    {
        WslcRuntime.ReadRepoTags(answer).Should().BeEmpty();
        WslcRuntime.ReadLabel(answer, ImageLabels.Version).Should().BeNull();
        WslcRuntime.ReadString(answer, "Image").Should().BeNull();
    }
}
