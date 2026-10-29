using System.Collections.Concurrent;
using Fravashi.Infrastructure.AgentSync;
using Fravashi.Infrastructure.Tests.Bridge;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentSync;

/// <summary>
/// Backlog 266: on a machine with no git, the bridge answered 500 to every git call, <see cref="HostGitIgnore.LoadAsync"/>
/// threw as for an unreachable bridge, and no agent conversation could start in any folder. The REAL bridge runs here
/// with a git program name that does not exist, which is what a host with no git on its PATH looks like to it.
/// </summary>
public sealed class HostGitIgnoreWithoutGitTests : IAsyncLifetime
{
    private readonly string _base = Path.Combine(
        Path.GetTempPath(), "fravashi-host-ignore-no-git-tests", Guid.NewGuid().ToString("N"));
    private readonly LinesLogger _log = new();
    private string _host = null!;
    private string _copyRoot = null!;
    private HostedBridge _bridge = null!;

    public async Task InitializeAsync()
    {
        _host = Path.Combine(_base, "host");
        _copyRoot = Path.Combine(_base, "copies");
        Directory.CreateDirectory(Path.Combine(_host, "src"));
        Directory.CreateDirectory(_copyRoot);
        File.WriteAllText(Path.Combine(_host, "src", "Program.cs"), "class P { }\n");
        _bridge = await HostedBridge.StartAsync(
            _host, gitExecutable: "fravashi-no-such-git-" + Guid.NewGuid().ToString("N"));
    }

    public async Task DisposeAsync()
    {
        await _bridge.DisposeAsync();
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task With_no_git_on_the_host_there_are_no_rules_one_log_line_and_the_copy_starts()
    {
        var client = _bridge.CreateClient();

        var host = await HostGitIgnore.LoadAsync(client, _log, CancellationToken.None);

        host.Answers.Should().BeFalse();
        _log.Lines.Should().ContainSingle()
            .Which.Should().Match<(LogLevel Level, string Message)>(l =>
                l.Level == LogLevel.Information && l.Message.Contains("git is not installed", StringComparison.Ordinal));

        await using var session = new WorkspaceSyncSession(
            client, _bridge.WorkspaceId, _copyRoot, WorkspaceSyncIgnore.FromGitignoreText(string.Empty, host));
        await session.StartAsync(CancellationToken.None);
        File.Exists(Path.Combine(_copyRoot, _bridge.WorkspaceId, "src", "Program.cs")).Should().BeTrue("the copy still works");
    }

    [Fact]
    public async Task A_bridge_that_cannot_be_reached_still_throws_so_the_start_is_retried()
    {
        var stopped = await HostedBridge.StartAsync(_host);
        var client = stopped.CreateClient();
        await stopped.DisposeAsync();

        var load = () => HostGitIgnore.LoadAsync(client, _log, CancellationToken.None);

        await load.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public void The_studio_recognises_the_bridges_git_missing_answer_by_the_same_code_and_words()
    {
        Fravashi.Infrastructure.Bridge.BridgeGitResultDto.GitMissingExitCode
            .Should().Be(Fravashi.Bridge.BridgeGitResult.GitMissingExitCode);
        Fravashi.Infrastructure.Bridge.BridgeGitResultDto.GitMissingMessage
            .Should().Be(Fravashi.Bridge.BridgeGitResult.GitMissingMessage);
        new Fravashi.Infrastructure.Bridge.BridgeGitResultDto(127, string.Empty, "sh: 1: tool: not found\n")
            .IsGitMissing.Should().BeFalse("git exiting 127 on its own is not a missing git");
    }

    private sealed class LinesLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _lines = new();

        public IReadOnlyCollection<(LogLevel Level, string Message)> Lines => _lines.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Enqueue((logLevel, formatter(state, exception)));
    }
}
