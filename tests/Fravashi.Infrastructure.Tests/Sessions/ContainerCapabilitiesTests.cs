using AwesomeAssertions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 295: one place, one fact per entry, each the studio checking its OWN process and filesystem.
/// </summary>
public sealed class ContainerCapabilitiesTests
{
    [Fact]
    public void IsWindows_reflects_the_runtime_platform()
    {
        ContainerCapabilities.IsWindows.Should().Be(OperatingSystem.IsWindows());
    }

    [Fact]
    public void IsOnPath_finds_a_file_placed_on_a_scratch_PATH() =>
        OnScratchPath(["made-up-tool"], scratch =>
        {
            ContainerCapabilities.IsOnPath("made-up-tool", scratch).Should().BeTrue();
            ContainerCapabilities.IsOnPath("no-such-tool-anywhere", scratch).Should().BeFalse();
        });

    [Fact]
    public void HasPowerShellOnPathIn_flips_true_with_pwsh_on_a_scratch_PATH_and_false_with_neither_there()
    {
        OnScratchPath(["no-such-tool-anywhere"], scratch => ContainerCapabilities.HasPowerShellOnPathIn(scratch).Should().BeFalse());
        OnScratchPath(["pwsh"], scratch => ContainerCapabilities.HasPowerShellOnPathIn(scratch).Should().BeTrue());
        OnScratchPath(["powershell"], scratch => ContainerCapabilities.HasPowerShellOnPathIn(scratch).Should().BeTrue());
    }

    [Fact]
    public void HasContainerEngineOnPathIn_flips_true_with_podman_on_a_scratch_PATH_and_false_with_neither_there()
    {
        OnScratchPath(["no-such-tool-anywhere"], scratch => ContainerCapabilities.HasContainerEngineOnPathIn(scratch).Should().BeFalse());
        OnScratchPath(["podman"], scratch => ContainerCapabilities.HasContainerEngineOnPathIn(scratch).Should().BeTrue());
        OnScratchPath(["docker"], scratch => ContainerCapabilities.HasContainerEngineOnPathIn(scratch).Should().BeTrue());
    }

    /// <summary>
    /// Runs <paramref name="assert"/> with a scratch folder holding exactly these names, NEVER by mutating the
    /// process-wide PATH: the Infrastructure test assembly runs classes in parallel (no CollectionBehavior is
    /// set), and other classes concurrently resolve git, rg, node and pwsh through PATH, so replacing it even
    /// for an instant is a flake nobody could reproduce on demand (the kind that cost v0.9.68, backlog 263).
    /// </summary>
    private static void OnScratchPath(IReadOnlyList<string> fileNames, Action<string> assert)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "fravashi-caps-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            foreach (var name in fileNames)
            {
                File.WriteAllText(Path.Combine(scratch, name), "#!/bin/sh\n");
            }

            assert(scratch);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void EnvVarIsSet_is_false_for_blank_and_true_for_a_set_value_an_agent_would_also_see()
    {
        // CLAUDE_ is a prefix AgentProcessEnvironment allows wholesale, so this name reaches an agent too.
        const string Name = "CLAUDE_TEST_CAPABILITY_VAR";
        var original = Environment.GetEnvironmentVariable(Name);
        try
        {
            Environment.SetEnvironmentVariable(Name, null);
            ContainerCapabilities.EnvVarIsSet(Name).Should().BeFalse();

            Environment.SetEnvironmentVariable(Name, "   ");
            ContainerCapabilities.EnvVarIsSet(Name).Should().BeFalse("whitespace-only is not set");

            Environment.SetEnvironmentVariable(Name, "1");
            ContainerCapabilities.EnvVarIsSet(Name).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, original);
        }
    }

    [Fact]
    public void EnvVarIsSet_reports_a_FRAVASHI_prefixed_variable_as_not_inherited_even_when_the_studio_process_has_it()
    {
        // Backlog 108: no FRAVASHI_ variable is INHERITED by an agent child, so a variable the IMAGE bakes for the
        // studio's own process (FRAVASHI_EMBED_MODEL_DIR, for one) is still not run for a test an agent executes
        // with no special opt-in, which is the exact gap #295 exists to mark rather than paper over. This does
        // NOT mean the variable can never reach an agent any other way (CLAUDE.md tells it to set some of them
        // explicitly on the test command), only that it is not already there by inheritance.
        const string Name = "FRAVASHI_TEST_CAPABILITY_VAR";
        var original = Environment.GetEnvironmentVariable(Name);
        try
        {
            Environment.SetEnvironmentVariable(Name, "a-real-value");
            ContainerCapabilities.EnvVarIsSet(Name).Should().BeFalse(
                "a FRAVASHI_ variable is never inherited by an agent child, whatever the studio's own process has");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, original);
        }
    }

    [Fact]
    public void FileExists_reads_the_real_filesystem()
    {
        ContainerCapabilities.FileExists(Path.Combine(Path.GetTempPath(), "no-such-file-" + Guid.NewGuid().ToString("N")))
            .Should().BeFalse();
        var real = Path.GetTempFileName();
        try
        {
            ContainerCapabilities.FileExists(real).Should().BeTrue();
        }
        finally
        {
            File.Delete(real);
        }
    }
}
