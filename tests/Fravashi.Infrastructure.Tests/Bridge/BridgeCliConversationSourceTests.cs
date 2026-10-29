using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Bridge;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Sessions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Bridge;

/// <summary>
/// Backlog 43, end to end against the REAL bridge: the studio's source and catalog read a workspace's
/// host CLI history through the new routes, with the folder derived host-side from the registered root.
/// </summary>
public sealed class BridgeCliConversationSourceTests : IAsyncLifetime
{
    private const long Cap = 4096;

    private readonly string _base = Path.Combine(
        Path.GetTempPath(), "fravashi-cli-import-tests", Guid.NewGuid().ToString("N"));

    private HostedBridge _bridge = null!;
    private string _folder = "";

    public async Task InitializeAsync()
    {
        var workspace = Path.Combine(_base, "a workspace");
        Directory.CreateDirectory(workspace);
        var projects = Path.Combine(_base, "projects");
        _bridge = await HostedBridge.StartAsync(workspace, projects, Cap);
        _folder = Path.Combine(projects, CliProjectFolderName.Encode(_bridge.WorkspaceRoot));
        Directory.CreateDirectory(_folder);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Sessions", "ImportFixtures", "synthetic-cli-history.jsonl"),
            Path.Combine(_folder, "11111111-2222-3333-4444-555555555555.jsonl"));
        File.WriteAllText(Path.Combine(_folder, "big.jsonl"), new string('x', (int)Cap + 1));
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
    }

    [Fact]
    public async Task The_catalog_reads_the_hosts_cli_history_through_the_bridge()
    {
        var source = new BridgeCliConversationSource(_bridge.CreateClient(), _bridge.CreateContext());

        var listed = await new ImportedConversationCatalog().ListAsync(_bridge.WorkspaceId, source);

        var conversation = listed.Single(c => c.Problem == ImportedConversationProblem.None);
        conversation.Title.Should().Be("Listing widgets");
        conversation.SessionId.Should().Be("11111111-2222-3333-4444-555555555555");
        conversation.Messages.Should().HaveCount(3);
        listed.Single(c => c.Name == "big.jsonl").Problem.Should().Be(ImportedConversationProblem.TooLarge);
    }

    [Fact]
    public async Task A_missing_file_reads_as_missing()
    {
        var source = new BridgeCliConversationSource(_bridge.CreateClient(), _bridge.CreateContext());

        (await source.ReadAsync("absent.jsonl", CancellationToken.None)).Status
            .Should().Be(CliConversationReadStatus.Missing);
    }

    [Fact]
    public async Task Outside_bridge_mode_nothing_is_listed()
    {
        var context = new BridgeWorkspaceContext(new ConfigurationBuilder().Build());
        var source = new BridgeCliConversationSource(new BridgeClient(new HttpClient(), context), context);

        (await source.ListAsync(CancellationToken.None)).Should().BeEmpty();
    }
}
