using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Projects;
using Fravashi.Application.Prototypes;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Wiki;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions.Tools;

/// <summary>
/// Backlog 215's acceptance through the real tool server over the real wiki (plan @40): an agent creates a prototype
/// of a design, adjusts it, and the adjustment renders, with the project resolved PER CALL (the session here is in a
/// second project, whose numbers collide with the default project's on purpose); prototype_adjust asks and its
/// approval card names each operation in words; a stale version and a guarded fragment are refused, writing nothing.
/// </summary>
public sealed class PrototypeToolsTests : IDisposable
{
    private const string Version = "2025-11-25";

    private const string Tree =
        """{"mockup": 1, "title": "Sign in", "screens": [{"type": "window", "id": "home", "text": "Home", "children": [{"type": "button", "id": "save", "text": "Save"}, {"type": "text", "text": "Plain"}]}]}""";

    private const string NewerTree =
        """{"mockup": 1, "title": "Sign in", "screens": [{"type": "window", "id": "home", "text": "Home", "children": [{"type": "button", "id": "save", "text": "Save now"}]}]}""";

    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "fravashi-prototype-tools-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteTestContext _store = new();
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly AgentSessionId _session = AgentSessionId.New();
    private readonly SqliteWikiLibraries _wikis;
    private readonly LocalTrackerResolver _trackers;
    private readonly ProjectId _project;
    private readonly string _secret;

    private int _key;

    public PrototypeToolsTests()
    {
        _wikis = new SqliteWikiLibraries(_store.Factory, TimeProvider.System);
        _trackers = new LocalTrackerResolver(_store.Factory, TimeProvider.System);
        _project = new SqliteProjectStore(_store.Factory, TimeProvider.System).Create("Second").Id;
        _secret = _broker.RegisterSession(_session, "/workspaces/a");
    }

    private IWikiLibrary Wiki => _wikis.For(_project);

    public void Dispose()
    {
        _broker.Dispose();
        _store.Dispose();
        try
        {
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    // ---- the acceptance: create, adjust, and the adjustment renders --------------------------------------------

    [Fact]
    public async Task An_agent_creates_a_prototype_adjusts_it_and_the_adjustment_renders_in_the_sessions_own_project()
    {
        // The default project holds a design with the same number, so a tool that fell back to it would be caught.
        await DesignAsync(_wikis.For(ProjectId.Default), "Somebody else's design", Tree);
        var design = await DesignAsync(Wiki, "Sign in", Tree);
        var server = Server();

        var created = Payload(await CallAsync(server, PrototypeRenderTool.ToolName, $$"""{"design":{{design.Number}}}"""));

        created.GetProperty("changed").GetString().Should().Be("created");
        var item = created.GetProperty("item");
        var number = item.GetProperty("number").GetInt32();
        item.GetProperty("design").GetProperty("version").GetInt64().Should().Be(design.Version);
        item.GetProperty("renders").GetBoolean().Should().BeTrue();
        item.GetProperty("anchors").EnumerateArray().Select(a => a.GetProperty("anchor").GetRawText())
            .Should().Contain("""{"id":"save"}""").And.Contain("""{"path":"/screens/0/children/1"}""");
        (await Wiki.GetByNumberAsync(number))!.Type.Should().Be(WikiItemType.Prototype);
        (await _wikis.For(ProjectId.Default).ListAsync(new WikiItemQuery { Types = [WikiItemType.Prototype] }))
            .Should().BeEmpty("the call acted on the session's project, not the default one");

        var adjusted = Payload(await CallAsync(
            server,
            PrototypeAdjustTool.ToolName,
            $$"""
            {
              "number": {{number}},
              "version": {{item.GetProperty("version").GetInt64()}},
              "operations": [
                { "op": "setText", "anchor": { "id": "save" }, "text": "Book the room" },
                { "op": "setCss", "anchor": { "path": "/screens/0/children/1" }, "properties": { "color": "red" } }
              ]
            }
            """));

        adjusted.GetProperty("changed").GetString().Should().Be("adjusted");
        adjusted.GetProperty("item").GetProperty("applied").EnumerateArray().Select(a => a.GetString())
            .Should().Equal("Sets the text of \"save\" to \"Book the room\".", "Sets color: red on the node at /screens/0/children/1.");

        // Read back through the store and rendered the way the page renders it.
        var stored = (await Wiki.GetByNumberAsync(number))!;
        var body = await PrototypeBody.ReadAsync(stored.Body, Wiki);
        body.IsValid.Should().BeTrue(string.Join("\n", body.Errors));
        body.Document!.Overlay.Should().HaveCount(2);
        var pinned = await PrototypePinnedDesign.ReadAsync(Wiki, body.Document);
        var render = PrototypeRenderer.Render(pinned.Tree!, body.Document.Overlay);
        render.Unplaced.Should().BeEmpty();
        render.Html.Should().Contain(">Book the room</button>");
        render.Html.Should().Contain("style=\"color: red\"");

        // And prototype_render on the existing prototype, already current, writes nothing and shows the new text.
        var again = Payload(await CallAsync(
            server, PrototypeRenderTool.ToolName, $$"""{"number":{{number}},"version":{{stored.Version}}}"""));
        again.GetProperty("changed").GetString().Should().Be("unchanged");
        again.GetProperty("item").GetProperty("anchors").EnumerateArray()
            .Single(a => a.GetProperty("anchor").GetRawText() == """{"id":"save"}""")
            .GetProperty("text").GetString().Should().Be("Book the room");
        (await Wiki.GetByNumberAsync(number))!.Version.Should().Be(stored.Version);
    }

    [Fact]
    public async Task An_adjustment_is_the_same_overlay_a_persons_direct_edit_writes()
    {
        var design = await DesignAsync(Wiki, "Sign in", Tree);
        var server = Server();
        var number = Payload(await CallAsync(server, PrototypeRenderTool.ToolName, $$"""{"design":{{design.Number}}}"""))
            .GetProperty("item").GetProperty("number").GetInt32();
        var opened = (await Wiki.GetByNumberAsync(number))!;

        // What the page's panel would have written for the same two edits.
        var expected = PrototypeEditSession.Open((await PrototypeBody.ReadAsync(opened.Body, Wiki)).Document!);
        expected.Apply(PrototypeEdits.SetText(new PrototypeAnchor("save", null), "Go").Operation!);
        expected.Apply(PrototypeEdits.SetStyle(new PrototypeAnchor("save", null), "font-weight", "bold").Operation!);

        Payload(await CallAsync(
            server,
            PrototypeAdjustTool.ToolName,
            Adjust(
                number,
                opened.Version,
                """{"op":"setText","anchor":{"id":"save"},"text":"Go"},{"op":"setCss","anchor":{"id":"save"},"properties":{"font-weight":"bold"}}""")));

        var stored = (await Wiki.GetByNumberAsync(number))!;
        PrototypeBody.FindBlocks(stored.Body).Single().Json.Should().Be(PrototypeWriter.Write(expected.Document));
        stored.Body[..stored.Body.IndexOf("```", StringComparison.Ordinal)]
            .Should().Be(opened.Body[..opened.Body.IndexOf("```", StringComparison.Ordinal)], "the prose is untouched");
    }

    // ---- what is refused ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_adjustment_against_a_stale_version_is_refused_and_writes_nothing()
    {
        var (number, version) = await PrototypeAsync();
        var server = Server();
        Payload(await CallAsync(server, PrototypeAdjustTool.ToolName, Adjust(number, version, """{"op":"setText","anchor":{"id":"save"},"text":"First"}""")));
        var after = (await Wiki.GetByNumberAsync(number))!;

        var stale = await CallAsync(server, PrototypeAdjustTool.ToolName, Adjust(number, version, """{"op":"setText","anchor":{"id":"save"},"text":"Second"}"""));

        stale.IsError.Should().BeTrue();
        Code(stale).Should().Be(WikiCodes.Conflict);
        stale.Text.Should().Contain($"is at version {after.Version} and you read version {version}");
        (await Wiki.GetByNumberAsync(number))!.Body.Should().Be(after.Body);
    }

    [Theory]
    [InlineData("""{"op":"insertHtml","anchor":{"id":"save"},"placement":"after","html":"<img src=x onerror=alert(1)>"}""", "$.operations[1].html")]
    [InlineData("""{"op":"insertHtml","anchor":{"id":"save"},"placement":"inside","html":"<iframe src=\"https://example.com\"></iframe>"}""", "$.operations[1].html")]
    [InlineData("""{"op":"setAttribute","anchor":{"id":"save"},"name":"onclick","value":"x"}""", "$.operations[1].name")]
    public async Task An_operation_the_guard_refuses_is_refused_by_the_tool_with_the_readers_message_and_nothing_is_written(
        string refused, string path)
    {
        var (number, version) = await PrototypeAsync();
        var before = (await Wiki.GetByNumberAsync(number))!.Body;
        var list = $$"""[{"op":"setText","anchor":{"id":"save"},"text":"fine"},{{refused}}]""";
        var expected = PrototypeReader.ReadOperations(list).Errors.Should().ContainSingle().Subject;

        var result = await CallAsync(Server(), PrototypeAdjustTool.ToolName, $$"""{"number":{{number}},"version":{{version}},"operations":{{list}}}""");

        result.IsError.Should().BeTrue();
        Code(result).Should().Be("operations_invalid");
        using var json = JsonDocument.Parse(result.Text);
        var error = json.RootElement.GetProperty("errors").EnumerateArray().Should().ContainSingle().Subject;
        error.GetProperty("path").GetString().Should().Be(path).And.Be(expected.Path);
        error.GetProperty("message").GetString().Should().Be(expected.Message);
        var stored = (await Wiki.GetByNumberAsync(number))!;
        stored.Body.Should().Be(before, "not even the operation before the refused one is written");
        stored.Version.Should().Be(version);
    }

    [Fact]
    public async Task An_operation_whose_anchor_names_no_node_is_refused_and_nothing_is_written()
    {
        var (number, version) = await PrototypeAsync();

        var result = await CallAsync(Server(), PrototypeAdjustTool.ToolName, Adjust(number, version, """{"op":"setText","anchor":{"id":"nowhere"},"text":"x"}"""));

        result.IsError.Should().BeTrue();
        Code(result).Should().Be("anchor_not_found");
        result.Text.Should().Contain("$.operations[0].anchor").And.Contain("prototype_render lists every anchor");
        (await Wiki.GetByNumberAsync(number))!.Version.Should().Be(version);
    }

    // ---- a node's parts (backlog 238, plan @52) -----------------------------------------------------------------

    [Fact]
    public async Task An_agent_sees_the_heading_as_a_part_and_makes_only_the_heading_bigger()
    {
        var design = await DesignAsync(Wiki, "Sign in", Tree);
        var server = Server();
        var created = Payload(await CallAsync(server, PrototypeRenderTool.ToolName, $$"""{"design":{{design.Number}}}""")).GetProperty("item");
        created.GetProperty("anchors").EnumerateArray().Select(a => a.GetProperty("anchor").GetRawText())
            .Should().Contain("""{"id":"home","part":"title"}""", "prototype_render lists the parts an agent can address");

        var adjusted = Payload(await CallAsync(
            server,
            PrototypeAdjustTool.ToolName,
            Adjust(created.GetProperty("number").GetInt32(), created.GetProperty("version").GetInt64(),
                """{"op":"setCss","anchor":{"id":"home","part":"title"},"properties":{"font-size":"1.6rem"}}""")));

        adjusted.GetProperty("item").GetProperty("applied").EnumerateArray().Select(a => a.GetString())
            .Should().Equal("Sets font-size: 1.6rem on the title of \"home\".");
        var stored = (await Wiki.GetByNumberAsync(created.GetProperty("number").GetInt32()))!;
        var body = await PrototypeBody.ReadAsync(stored.Body, Wiki);
        var render = PrototypeRenderer.Render((await PrototypePinnedDesign.ReadAsync(Wiki, body.Document!)).Tree!, body.Document!.Overlay);
        render.Unplaced.Should().BeEmpty();
        render.Html.Should().Contain("<h1 data-mk-part-of=\"home\" data-mk-part=\"title\" style=\"font-size: 1.6rem\">Home</h1>")
            .And.Contain("<section data-mk-anchor=\"home\" class=\"mk-screen\" aria-label=\"Home\" data-mk-screen=\"screen\">");
    }

    [Fact]
    public async Task A_part_the_node_does_not_have_is_refused_with_its_reason_and_nothing_is_written()
    {
        var (number, version) = await PrototypeAsync();

        var result = await CallAsync(Server(), PrototypeAdjustTool.ToolName, Adjust(number, version,
            """{"op":"setCss","anchor":{"id":"save","part":"title"},"properties":{"color":"red"}}"""));

        Code(result).Should().Be("part_not_found");
        result.Text.Should().Contain("$.operations[0].anchor.part")
            .And.Contain("The button \\u0022save\\u0022 has no \\u0022title\\u0022 part");
        (await Wiki.GetByNumberAsync(number))!.Version.Should().Be(version);
    }

    [Fact]
    public async Task Prototype_check_names_a_part_its_node_does_not_have_in_the_pinned_design()
    {
        var design = await DesignAsync(Wiki, "Sign in", Tree);
        var check = new StudioToolServer(
            _broker, [new PrototypeCheckTool(_wikis, _trackers)], new FileToolCallLedger(_dataDirectory), "9.8.7",
            StudioToolServer.DefaultMaxResultBytes, grants: new DefaultStudioToolGrants(StudioToolGrant.Full), projects: new SessionsIn(_project));
        string Document(string anchor) => JsonSerializer.Serialize(
            $$"""{"prototype": 1, "design": {"number": {{design.Number}}, "version": {{design.Version}}}, "overlay": [{"op": "setText", "anchor": {{anchor}}, "text": "x"}]}""");

        var refused = Payload(await CallAsync(check, PrototypeCheckTool.ToolName, $$"""{"tree":{{Document("""{"id": "home", "part": "caption"}""")}}}"""));
        var fine = Payload(await CallAsync(check, PrototypeCheckTool.ToolName, $$"""{"tree":{{Document("""{"id": "home", "part": "title"}""")}}}"""));

        refused.GetProperty("valid").GetBoolean().Should().BeFalse();
        var error = refused.GetProperty("errors").EnumerateArray().Should().ContainSingle().Subject;
        error.GetProperty("path").GetString().Should().Be("$.overlay[0].anchor.part");
        error.GetProperty("message").GetString().Should().Be("The screen \"home\" has no \"caption\" part; its part is \"title\".");
        fine.GetProperty("valid").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task No_operations_a_missing_version_and_a_document_that_is_not_a_prototype_are_refused()
    {
        var (number, version) = await PrototypeAsync();
        var design = (await Wiki.ListAsync(new WikiItemQuery { Types = [WikiItemType.Design] })).Single();
        var server = Server();

        Code(await CallAsync(server, PrototypeAdjustTool.ToolName, $$"""{"number":{{number}},"version":{{version}},"operations":[]}"""))
            .Should().Be("operations_invalid");
        Code(await CallAsync(server, PrototypeAdjustTool.ToolName, $$"""{"number":{{number}},"operations":[]}"""))
            .Should().Be("version_required");
        Code(await CallAsync(server, PrototypeAdjustTool.ToolName, Adjust(design.Number, design.Version, """{"op":"pageCss","rule":"p{}"}""")))
            .Should().Be("not_a_prototype");
        Code(await CallAsync(server, PrototypeRenderTool.ToolName, $$"""{"design":{{number}}}""")).Should().Be("not_a_design");
        Code(await CallAsync(server, PrototypeRenderTool.ToolName, """{}""")).Should().Be("design_required");
    }

    [Fact]
    public async Task A_design_whose_tree_does_not_read_gets_no_prototype()
    {
        var broken = await DesignAsync(Wiki, "Broken", """{"mockup": 1, "screens": [{"type": "window", "id": "home", "colour": "red"}]}""");

        var result = await CallAsync(Server(), PrototypeRenderTool.ToolName, $$"""{"design":{{broken.Number}}}""");

        Code(result).Should().Be("design_invalid");
        (await Wiki.ListAsync(new WikiItemQuery { Types = [WikiItemType.Prototype] })).Should().BeEmpty();
    }

    // ---- refreshing onto a newer design ------------------------------------------------------------------------

    [Fact]
    public async Task Refreshing_moves_the_pin_to_the_designs_current_version_and_keeps_the_overlay()
    {
        var (number, version) = await PrototypeAsync();
        var server = Server();
        Payload(await CallAsync(server, PrototypeAdjustTool.ToolName, Adjust(number, version, """{"op":"pageCss","rule":".x { color: red; }"}""")));
        var adjusted = (await Wiki.GetByNumberAsync(number))!;
        var design = (await Wiki.ListAsync(new WikiItemQuery { Types = [WikiItemType.Design] })).Single();
        var newer = (await Wiki.UpdateAsync(design.Id, new WikiItemEdit { Body = DesignBody(NewerTree) }, design.Version)).Value;

        var stale = await CallAsync(server, PrototypeRenderTool.ToolName, $$"""{"number":{{number}},"version":{{version}}}""");
        Code(stale).Should().Be(WikiCodes.Conflict);

        var mismatch = await CallAsync(server, PrototypeRenderTool.ToolName, $$"""{"number":{{number}},"version":{{adjusted.Version}},"design":{{number}}}""");
        Code(mismatch).Should().Be("design_mismatch");

        var refreshed = Payload(await CallAsync(server, PrototypeRenderTool.ToolName, $$"""{"number":{{number}},"version":{{adjusted.Version}}}"""));

        refreshed.GetProperty("changed").GetString().Should().Be("refreshed");
        refreshed.GetProperty("item").GetProperty("design").GetProperty("version").GetInt64().Should().Be(newer.Version);
        var stored = await PrototypeBody.ReadAsync((await Wiki.GetByNumberAsync(number))!.Body, Wiki);
        stored.Document!.Design.Version.Should().Be((int)newer.Version);
        stored.Document.Overlay.Should().ContainSingle().Which.Should().Be(new PrototypePageCssOperation(".x { color: red; }"));
        refreshed.GetProperty("item").GetProperty("anchors").EnumerateArray()
            .Single(a => a.GetProperty("anchor").GetRawText() == """{"id":"save"}""")
            .GetProperty("text").GetString().Should().Be("Save now", "the page renders from the newer design");
    }

    // ---- asking, and the approval card -------------------------------------------------------------------------

    [Fact]
    public void Creating_a_prototype_runs_unattended_and_naming_an_existing_one_asks()
    {
        var render = StudioToolServer.CliToolName(PrototypeRenderTool.ToolName);
        var adjust = StudioToolServer.CliToolName(PrototypeAdjustTool.ToolName);

        AgentGraphTools.RunsUnattended(render, new Dictionary<string, string> { ["design"] = "3" }).Should().NotBeNull();
        AgentGraphTools.RunsUnattended(render, new Dictionary<string, string> { ["design"] = "3", ["number"] = "4", ["version"] = "2" }).Should().BeNull();
        AgentGraphTools.RunsUnattended(render, new Dictionary<string, string> { ["number"] = "null" }).Should().BeNull();
        AgentGraphTools.RunsUnattended(adjust, new Dictionary<string, string> { ["operations"] = "[]" }).Should().BeNull();
        AgentToolSafety.IsReadOnly(render).Should().BeFalse();
        AgentToolSafety.IsReadOnly(adjust).Should().BeFalse();
    }

    [Fact]
    public async Task A_prototype_adjustment_asks_and_its_card_names_each_operation_in_words()
    {
        var broker = new AgentApprovalBroker();
        broker.RegisterSession(_session, "/workspaces/a");
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;
        const string operations = """
            [
              { "op": "setText", "anchor": { "id": "save" }, "text": "Book the room" },
              { "op": "setCss", "anchor": { "path": "/screens/0/children/1" }, "properties": { "color": "red" } },
              { "op": "insertHtml", "anchor": { "id": "save" }, "placement": "after", "html": "<em>Nothing is booked yet.</em>" }
            ]
            """;

        var pending = broker.RequestAsync(
            _session,
            StudioToolServer.CliToolName(PrototypeAdjustTool.ToolName),
            "toolu_1",
            AgentToolInput.Create(
                new Dictionary<string, string> { ["number"] = "4", ["version"] = "3", ["operations"] = operations },
                []));

        seen.Should().NotBeNull("an adjustment always asks");
        var effects = seen!.Effects!;
        effects.Select(e => e.Field).Should().Equal("number", "operations[0]", "operations[1]", "operations[2]");
        effects[0].Notice.Should().Contain("prototype @4 against version 3").And.Contain("appending 3 operations");
        effects[1].Notice.Should().Be("1. Sets the text of \"save\" to \"Book the room\".");
        effects[2].Notice.Should().Be("2. Sets color: red on the node at /screens/0/children/1.");
        effects[3].Notice.Should().Be("3. Inserts HTML after \"save\". The fragment: <em>Nothing is booked yet.</em>");
        effects.Should().OnlyContain(e => e.Weight == AgentEffectWeight.Note);

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
        broker.Dispose();
    }

    [Fact]
    public async Task Operations_the_studio_cannot_read_are_said_loudly_on_the_card()
    {
        var broker = new AgentApprovalBroker();
        broker.RegisterSession(_session, "/workspaces/a");
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(
            _session,
            StudioToolServer.CliToolName(PrototypeAdjustTool.ToolName),
            "toolu_1",
            AgentToolInput.Create(
                new Dictionary<string, string>
                {
                    ["number"] = "4",
                    ["version"] = "3",
                    ["operations"] = """[{"op":"insertHtml","anchor":{"id":"save"},"placement":"after","html":"<script>x</script>"}]""",
                },
                []));

        var effect = seen!.Effects!.Should().ContainSingle(e => e.Field == "operations").Subject;
        effect.Weight.Should().Be(AgentEffectWeight.Alarm);
        effect.Notice.Should().Contain("cannot read these operations").And.Contain("writes nothing");

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
        broker.Dispose();
    }

    [Fact]
    public void The_descriptions_send_structural_changes_to_the_design()
    {
        foreach (var tool in Tools())
        {
            tool.Description.Should().Contain("belongs in the DESIGN", tool.Name);
            using var schema = JsonDocument.Parse(tool.InputSchemaJson);
            schema.RootElement.GetProperty("type").GetString().Should().Be("object");
        }

        PrototypeAdjustTool.Effects("Bash", new Dictionary<string, string> { ["operations"] = "[]" }).Should().BeEmpty();
    }

    [Fact]
    public async Task A_read_only_session_cannot_render_or_adjust()
    {
        var listed = await ListAsync(Server(StudioToolGrant.ReadOnly));

        listed.Should().NotContain(PrototypeRenderTool.ToolName).And.NotContain(PrototypeAdjustTool.ToolName);
        (await ListAsync(Server())).Should().Contain(PrototypeRenderTool.ToolName).And.Contain(PrototypeAdjustTool.ToolName);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static string DesignBody(string tree) => $"The sign-in screen.\n\n```mockup\n{tree}\n```\n";

    private static async Task<WikiItem> DesignAsync(IWikiLibrary wiki, string title, string tree)
    {
        var created = await wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Design, Title = title, Body = DesignBody(tree) });
        created.IsValid.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));
        return created.Value;
    }

    private async Task<(int Number, long Version)> PrototypeAsync()
    {
        var design = await DesignAsync(Wiki, "Sign in", Tree);
        var item = Payload(await CallAsync(Server(), PrototypeRenderTool.ToolName, $$"""{"design":{{design.Number}}}""")).GetProperty("item");
        return (item.GetProperty("number").GetInt32(), item.GetProperty("version").GetInt64());
    }

    private static string Adjust(int number, long version, string operation) =>
        $$"""{"number":{{number}},"version":{{version}},"operations":[{{operation}}]}""";

    private static string? Code((string Text, bool IsError) result)
    {
        result.IsError.Should().BeTrue(result.Text);
        using var json = JsonDocument.Parse(result.Text);
        return json.RootElement.GetProperty("refused").GetString();
    }

    private IStudioTool[] Tools() =>
    [
        new PrototypeRenderTool(_wikis, _trackers),
        new PrototypeAdjustTool(_wikis, _trackers),
    ];

    private StudioToolServer Server(StudioToolGrant? grant = null) =>
        new(
            _broker,
            Tools(),
            new FileToolCallLedger(_dataDirectory),
            "9.8.7",
            StudioToolServer.DefaultMaxResultBytes,
            grants: new DefaultStudioToolGrants(grant ?? StudioToolGrant.Full),
            projects: new SessionsIn(_project));

    /// <summary>Every session is in the one named project: the tools must ask the server for it at each call.</summary>
    private sealed class SessionsIn(ProjectId project) : ISessionProjects
    {
        public ProjectId? For(AgentSessionId session) => project;
    }

    private async Task<IReadOnlyList<string>> ListAsync(StudioToolServer server)
    {
        var response = await server.HandleAsync(new StudioToolHttpRequest(
            "POST", _secret, Version, null, """{"method":"tools/list","jsonrpc":"2.0","id":1}"""));

        using var json = JsonDocument.Parse(response.Json!);
        return
        [
            .. json.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()!),
        ];
    }

    private async Task<(string Text, bool IsError)> CallAsync(StudioToolServer server, string tool, string arguments)
    {
        var operationKey = "toolu_" + Interlocked.Increment(ref _key).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var body =
            $"{{\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{arguments},\"_meta\":{{\"claudecode/toolUseId\":\"{operationKey}\"}}}},\"jsonrpc\":\"2.0\",\"id\":2}}";

        var response = await server.HandleAsync(new StudioToolHttpRequest("POST", _secret, Version, null, body));
        response.StatusCode.Should().Be(200);

        using var json = JsonDocument.Parse(response.Json!);
        if (json.RootElement.TryGetProperty("error", out var error))
        {
            return (error.GetProperty("message").GetString()!, true);
        }

        var result = json.RootElement.GetProperty("result");
        return (
            result.GetProperty("content")[0].GetProperty("text").GetString()!,
            result.GetProperty("isError").GetBoolean());
    }

    private static JsonElement Payload((string Text, bool IsError) result)
    {
        result.IsError.Should().BeFalse(result.Text);
        return JsonDocument.Parse(result.Text).RootElement.Clone();
    }
}
