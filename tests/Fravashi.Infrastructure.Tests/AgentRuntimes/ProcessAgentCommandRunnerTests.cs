using Fravashi.Application.AgentRuntimes;
using Fravashi.Infrastructure.AgentRuntimes;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentRuntimes;

/// <summary>Plan 2d: the runner honors the command's working directory (agents run INSIDE their copy).</summary>
public sealed class ProcessAgentCommandRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "fravashi-runner-tests", Guid.NewGuid().ToString("N"));

    public ProcessAgentCommandRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task WorkingDirectory_IsTheChildProcessesCwd()
    {
        var command = OperatingSystem.IsWindows()
            ? new AgentCommand("cmd", ["/c", "cd"], TimeSpan.FromSeconds(20), WorkingDirectory: _dir)
            : new AgentCommand("sh", ["-c", "pwd"], TimeSpan.FromSeconds(20), WorkingDirectory: _dir);

        var result = await new ProcessAgentCommandRunner().RunAsync(command);

        result.Success.Should().BeTrue(result.StandardError);
        result.StandardOutput.Trim().Should().Be(Path.TrimEndingDirectorySeparator(_dir));
    }

    [Fact]
    public async Task NoWorkingDirectory_InheritsTheAppsCwd()
    {
        var command = OperatingSystem.IsWindows()
            ? new AgentCommand("cmd", ["/c", "cd"], TimeSpan.FromSeconds(20))
            : new AgentCommand("sh", ["-c", "pwd"], TimeSpan.FromSeconds(20));

        var result = await new ProcessAgentCommandRunner().RunAsync(command);

        result.Success.Should().BeTrue(result.StandardError);
        result.StandardOutput.Trim().Should().NotBe(Path.TrimEndingDirectorySeparator(_dir));
    }

    [Fact]
    public async Task A_cli_the_studio_runs_does_not_inherit_the_studios_environment()
    {
        // Backlog 72: this runner starts agent CLIs and npm, so the studio's own environment (which in
        // production carries a live host-bridge credential) must not reach them. The probe name is
        // unique so it can never collide with another test class.
        var probe = "FRAVASHI_PROBE_" + Guid.NewGuid().ToString("N");
        var marker = "value-" + Guid.NewGuid().ToString("N");
        var previous = Environment.GetEnvironmentVariable(probe);
        try
        {
            Environment.SetEnvironmentVariable(probe, marker);
            var command = OperatingSystem.IsWindows()
                ? new AgentCommand("cmd", ["/c", "set"], TimeSpan.FromSeconds(20))
                : new AgentCommand("sh", ["-c", "env"], TimeSpan.FromSeconds(20));

            var result = await new ProcessAgentCommandRunner().RunAsync(command);

            result.Success.Should().BeTrue(result.StandardError);
            result.StandardOutput.Should().ContainEquivalentOf("PATH=", "otherwise this assertion proves nothing");
            result.StandardOutput.Should().NotContainEquivalentOf(probe);
            result.StandardOutput.Should().NotContainEquivalentOf(marker);
        }
        finally
        {
            Environment.SetEnvironmentVariable(probe, previous);
        }
    }
}
