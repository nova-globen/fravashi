using System.Text;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 43: rebuilding a display transcript from the CLI's own conversation history, and re-reading a
/// file only when it changed. The fixtures under Sessions/ImportFixtures are SYNTHETIC, written by hand in
/// the CLI's line shape; no real conversation content is in this repository.
/// </summary>
public sealed class ImportedConversationTests
{
    private static readonly DateTimeOffset FileTime = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Sessions", "ImportFixtures", name));

    // ---- parser -----------------------------------------------------------------------------------

    [Fact]
    public void Keeps_only_what_the_user_and_the_agent_said_as_text()
    {
        var parsed = CliConversationTranscriptParser.Parse(Fixture("synthetic-cli-history.jsonl"), FileTime);

        parsed.Messages.Select(m => (m.Role, m.Text)).Should().Equal(
            (TranscriptRole.User, "How do I list the widgets?"),
            (TranscriptRole.Assistant, "Run the list command."),
            (TranscriptRole.Assistant, "There is one widget.\n\nIt is called widget-1."));
    }

    [Fact]
    public void Thinking_tool_calls_tool_results_meta_and_sidechain_lines_are_not_shown()
    {
        var text = string.Join(
            "\n",
            CliConversationTranscriptParser.Parse(Fixture("synthetic-cli-history.jsonl"), FileTime).Messages.Select(m => m.Text));

        text.Should().NotContain("private reasoning")
            .And.NotContain("widgets list")
            .And.NotContain("system-reminder")
            .And.NotContain("subagent");
    }

    [Fact]
    public void Title_prefers_the_cli_title_and_start_is_the_first_timestamp()
    {
        var parsed = CliConversationTranscriptParser.Parse(Fixture("synthetic-cli-history.jsonl"), FileTime);

        parsed.Title.Should().Be("Listing widgets");
        parsed.StartedUtc.Should().Be(new DateTimeOffset(2026, 9, 1, 9, 59, 59, TimeSpan.Zero));
        parsed.Messages[0].Timestamp.Should().Be(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_users_own_rename_wins_over_the_generated_title()
    {
        var bytes = Fixture("synthetic-cli-history.jsonl")
            .Concat(Encoding.UTF8.GetBytes("{\"type\":\"custom-title\",\"customTitle\":\"My widget chat\"}\n"))
            .ToArray();

        CliConversationTranscriptParser.Parse(bytes, FileTime).Title.Should().Be("My widget chat");
    }

    [Fact]
    public void A_file_with_no_assistant_lines_is_kept_and_titled_by_the_first_user_message()
    {
        var parsed = CliConversationTranscriptParser.Parse(Fixture("synthetic-cli-history-no-assistant.jsonl"), FileTime);

        parsed.Messages.Should().ContainSingle().Which.Role.Should().Be(TranscriptRole.User);
        parsed.Title.Should().Be("A question that was never answered");
    }

    [Fact]
    public void A_file_with_no_messages_at_all_is_still_a_conversation()
    {
        var parsed = CliConversationTranscriptParser.Parse(
            Encoding.UTF8.GetBytes("{\"type\":\"queue-operation\"}\nnot json\n"), FileTime);

        parsed.Messages.Should().BeEmpty();
        parsed.Title.Should().Be(CliConversationTranscriptParser.UntitledTitle);
        parsed.StartedUtc.Should().Be(FileTime);
    }

    // ---- catalog: re-read only on change -----------------------------------------------------------

    private sealed class CountingSource : ICliConversationSource
    {
        public Dictionary<string, (CliConversationFile File, byte[] Bytes)> Files { get; } = [];

        public Dictionary<string, int> Reads { get; } = [];

        public int Lists { get; private set; }

        public void Put(string name, byte[] bytes, DateTimeOffset modified) =>
            Files[name] = (new CliConversationFile(name, bytes.Length, modified), bytes);

        public Task<IReadOnlyList<CliConversationFile>> ListAsync(CancellationToken cancellationToken)
        {
            Lists++;
            return Task.FromResult<IReadOnlyList<CliConversationFile>>([.. Files.Values.Select(f => f.File)]);
        }

        public Task<CliConversationContent> ReadAsync(string name, CancellationToken cancellationToken)
        {
            Reads[name] = Reads.GetValueOrDefault(name) + 1;
            return Task.FromResult(Files.TryGetValue(name, out var file)
                ? new CliConversationContent(CliConversationReadStatus.Read, file.Bytes)
                : CliConversationContent.Missing);
        }
    }

    [Fact]
    public async Task An_unchanged_file_is_not_read_again()
    {
        var source = new CountingSource();
        source.Put("a.jsonl", Fixture("synthetic-cli-history.jsonl"), FileTime);
        source.Put("b.jsonl", Fixture("synthetic-cli-history-no-assistant.jsonl"), FileTime);
        var catalog = new ImportedConversationCatalog();

        var first = await catalog.ListAsync("w1", source);
        var second = await catalog.ListAsync("w1", source);

        first.Should().HaveCount(2);
        second.Should().HaveCount(2);
        source.Lists.Should().Be(2);
        source.Reads.Should().BeEquivalentTo(new Dictionary<string, int> { ["a.jsonl"] = 1, ["b.jsonl"] = 1 });
        second.Single(c => c.Name == "a.jsonl").Should().BeSameAs(first.Single(c => c.Name == "a.jsonl"));
    }

    [Fact]
    public async Task A_file_whose_modification_time_moved_is_read_and_parsed_again()
    {
        var source = new CountingSource();
        source.Put("a.jsonl", Fixture("synthetic-cli-history-no-assistant.jsonl"), FileTime);
        var catalog = new ImportedConversationCatalog();
        (await catalog.ListAsync("w1", source)).Single().Messages.Should().HaveCount(1);

        source.Put("a.jsonl", Fixture("synthetic-cli-history.jsonl"), FileTime.AddMinutes(5));
        var after = await catalog.ListAsync("w1", source);

        source.Reads["a.jsonl"].Should().Be(2);
        after.Single().Messages.Should().HaveCount(3);
        after.Single().Title.Should().Be("Listing widgets");
    }

    [Fact]
    public async Task The_cache_is_per_workspace_and_a_vanished_file_leaves_the_list()
    {
        var source = new CountingSource();
        source.Put("a.jsonl", Fixture("synthetic-cli-history.jsonl"), FileTime);
        var catalog = new ImportedConversationCatalog();
        await catalog.ListAsync("w1", source);
        await catalog.ListAsync("w2", source);
        source.Reads["a.jsonl"].Should().Be(2);

        source.Files.Clear();
        (await catalog.ListAsync("w1", source)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_over_the_cap_is_listed_with_its_problem_rather_than_dropped()
    {
        var source = new TooLargeSource();
        var catalog = new ImportedConversationCatalog();

        var listed = await catalog.ListAsync("w1", source);

        var only = listed.Should().ContainSingle().Subject;
        only.Problem.Should().Be(ImportedConversationProblem.TooLarge);
        only.Messages.Should().BeEmpty();
        only.SessionId.Should().Be("huge");
    }

    private sealed class TooLargeSource : ICliConversationSource
    {
        public Task<IReadOnlyList<CliConversationFile>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CliConversationFile>>([new CliConversationFile("huge.jsonl", long.MaxValue, FileTime)]);

        public Task<CliConversationContent> ReadAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(CliConversationContent.TooLarge);
    }
}
