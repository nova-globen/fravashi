using System.Threading.Channels;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 52 (plan @43): "New design from a drawing" starts a conversation whose first turn carries the drawing and
/// the studio's instruction, recorded as the studio's with the drawing shown; a runtime whose descriptor does not
/// say it sees images is refused before anything starts; and nothing here writes the image anywhere but the turn.
/// </summary>
public sealed class DesignFromDrawingStarterTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-drawing-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteSessionStore _store;
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly HeldRunner _runner = new();
    private readonly LiveSessionRegistry _registry;

    /// <summary>The smallest bytes the studio takes for a PNG: the signature, then anything.</summary>
    private static readonly AgentImageAttachment Drawing =
        AgentImageAttachment.TryCreate([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4], out _)!;

    public DesignFromDrawingStarterTests()
    {
        Directory.CreateDirectory(_directory);
        var factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(factory);
        _store = new SqliteSessionStore(factory);
        _registry = new LiveSessionRegistry(_runner, _store, _broker);
    }

    public void Dispose()
    {
        _registry.Dispose();
        _broker.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task The_drawing_and_the_instruction_start_a_new_conversation_in_the_workspace_copy()
    {
        var starter = new DesignFromDrawingStarter(_registry);

        var started = starter.Start("w1", "/workspaces/w1", Drawing, "A sign-up form.");

        started.Refusal.Should().BeNull();
        var session = started.Session!;
        session.Title.Should().Be(DesignFromDrawing.ConversationTitle);

        await Eventually(() => _runner.LastRequest is not null);
        var request = _runner.LastRequest!;
        request.SessionId.Should().Be(session.Id);
        request.WorkspaceId.Should().Be("w1");
        request.WorkingDirectory.Should().Be("/workspaces/w1");
        request.Prompt.Should().Be(DesignFromDrawing.Instruction("A sign-up form."));
        request.PromptImages.Should().ContainSingle().Which.Should().Be(Drawing);
        request.ResumeSessionId.Should().BeNull("it is a new conversation, never a turn in one already open");
    }

    [Fact]
    public void The_transcript_shows_the_drawing_above_the_studios_words_and_never_says_the_person_wrote_them()
    {
        var session = new DesignFromDrawingStarter(_registry).Start("w1", "/workspaces/w1", Drawing, null).Session!;

        foreach (var transcript in new[] { session.Transcript, _store.Transcript(session.Id) })
        {
            var attached = transcript.OfType<UserImagesAttached>().Should().ContainSingle().Subject;
            attached.Images.Should().ContainSingle().Which.Should().Be(Drawing);

            var messages = transcript.OfType<MessageEmitted>().ToList();
            messages.Should().ContainSingle();
            messages[0].Role.Should().Be(TranscriptRole.StudioPrompt);
            messages[0].Text.Should().Be(DesignFromDrawing.Instruction(null));

            var at = transcript.ToList().IndexOf(messages[0]);
            transcript[at - 1].Should().BeSameAs(attached, "the images pair with the message by position");
        }
    }

    [Fact]
    public void A_runtime_whose_descriptor_does_not_see_images_is_refused_and_nothing_starts()
    {
        var blind = ConversationRuntime.Descriptor with { SeesImages = false };
        var starter = new DesignFromDrawingStarter(_registry, blind);

        starter.Unavailable.Should().Be(DesignFromDrawing.CannotSeeImages(blind));

        var started = starter.Start("w1", "/workspaces/w1", Drawing, null);

        started.Session.Should().BeNull();
        started.Refusal.Should().Be(DesignFromDrawing.CannotSeeImages(blind));
        _registry.Sessions.Should().BeEmpty();
        _store.Recent().Should().BeEmpty();
        _runner.LastRequest.Should().BeNull();
    }

    [Fact]
    public void The_runtime_conversations_run_on_is_read_from_the_catalog_and_sees_images()
    {
        ConversationRuntime.Tool.Should().Be(AgentTool.Claude);
        ConversationRuntime.Descriptor.SeesImages.Should().BeTrue();
        new DesignFromDrawingStarter(_registry).Unavailable.Should().BeNull();
    }

    [Theory]
    [InlineData(null, "/workspaces/w1")]
    [InlineData("w1", null)]
    [InlineData(" ", "/workspaces/w1")]
    public void No_workspace_copy_is_said_and_nothing_starts(string? workspaceId, string? copy)
    {
        var started = new DesignFromDrawingStarter(_registry).Start(workspaceId, copy, Drawing, null);

        started.Session.Should().BeNull();
        started.Refusal.Should().Contain("no workspace folder");
        _registry.Sessions.Should().BeEmpty();
    }

    [Fact]
    public void No_drawing_is_said_and_nothing_starts()
    {
        var started = new DesignFromDrawingStarter(_registry).Start("w1", "/workspaces/w1", null, "a note");

        started.Refusal.Should().Be("Choose or paste the drawing first.");
        _registry.Sessions.Should().BeEmpty();
    }

    [Fact]
    public void A_conversation_already_running_in_the_folder_is_the_registrys_own_refusal()
    {
        var starter = new DesignFromDrawingStarter(_registry);
        starter.Start("w1", "/workspaces/w1", Drawing, null).Session.Should().NotBeNull();

        var second = starter.Start("w1", "/workspaces/w1", Drawing, null);

        second.Session.Should().BeNull();
        second.Refusal.Should().Be("This workspace already has a session running. Stop it before starting another.");
    }

    [Fact]
    public void The_starter_holds_nothing_that_could_write_a_document()
    {
        // Nothing stores the image as the design: the only thing the starter can reach is the registry, so the
        // one document that results is the one the agent writes with its tree.
        var fields = typeof(DesignFromDrawingStarter)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(f => f.FieldType)
            .ToList();

        fields.Should().BeEquivalentTo([typeof(LiveSessionRegistry), typeof(Application.AgentRuntimes.AgentRuntimeDescriptor)]);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(10); // poll interval inside a wait helper (backlog 107)
        }

        condition().Should().BeTrue();
    }

    /// <summary>A runner that records the request and holds the conversation open.</summary>
    private sealed class HeldRunner : IAgentSessionRunner
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();

        public AgentSessionRequest? LastRequest { get; private set; }

        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return _events.Reader.ReadAllAsync(cancellationToken);
        }

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public bool Stop(AgentSessionId sessionId)
        {
            _events.Writer.TryComplete();
            return true;
        }

        public bool ChangeModel(AgentSessionId sessionId, string? model) => false;
    }
}
