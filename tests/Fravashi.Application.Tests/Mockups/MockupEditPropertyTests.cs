using System.Text.Json.Nodes;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>
/// #195's property: for random valid trees and random sequences of edits, undoing every one of them gives back
/// the original tree as <see cref="MockupWriter"/> bytes, step by step, and redoing them gives the edited tree.
/// The edits are drawn from every operation, aimed at real and missing nodes alike, so refusals are part of
/// the sequence too (a refused edit must leave nothing on the stacks). Seeded, so a failure reproduces.
/// </summary>
public class MockupEditPropertyTests
{
    private const int Trees = 60;
    private const int GrowEdits = 40;
    private const int Edits = 40;

    private static readonly string[] Seeds =
    [
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", "team-directory.attempt-3.json")),
        """{"mockup": 1, "screens": [{"type": "window", "id": "main"}]}""",
        """
        {"mockup": 1, "viewports": [{"name": "wide", "width": 1280}],
         "data": {"rows": [{"a": 1, "b": "x"}]},
         "screens": [{"type": "window", "id": "main", "layout": "row", "children": [
           {"type": "table", "data": "rows", "columns": ["a", "b"]},
           {"type": "dialog", "id": "box", "children": [{"type": "button", "text": "Close", "on": {"click": {"close": "box"}}}]}]}]}
        """,
    ];

    [Fact]
    public void Undoing_every_edit_restores_the_original_bytes_and_redoing_them_restores_the_edited_ones()
    {
        var random = new Random(195);
        var tested = 0;

        for (var round = 0; round < Trees; round++)
        {
            var original = RandomValidTree(random);
            MockupEditSession.TryOpen(original, out var session, out _).Should().BeTrue();
            var opened = session!.Json;

            var states = new List<string> { opened };
            for (var i = 0; i < Edits; i++)
            {
                var edit = RandomEdit(random, session);
                var before = session.Json;
                var depth = states.Count;
                if (session.Apply(edit).Applied)
                {
                    states.Add(session.Json);
                }
                else
                {
                    session.Json.Should().Be(before, $"a refused {edit} changes nothing");
                    states.Should().HaveCount(depth);
                }
            }

            var edited = session.Json;
            for (var step = states.Count - 1; step > 0; step--)
            {
                session.Undo().Should().BeTrue();
                session.Json.Should().Be(states[step - 1], $"round {round}: undo back to step {step - 1}");
            }

            session.CanUndo.Should().BeFalse();
            session.IsDirty.Should().BeFalse();
            session.Json.Should().Be(opened);

            for (var step = 1; step < states.Count; step++)
            {
                session.Redo().Should().BeTrue();
                session.Json.Should().Be(states[step], $"round {round}: redo to step {step}");
            }

            session.CanRedo.Should().BeFalse();
            session.Json.Should().Be(edited);
            tested += states.Count - 1;
        }

        tested.Should().BeGreaterThan(Trees * 5, "the sequences have to apply edits, not only be refused");
    }

    /// <summary>A valid tree: a seed grown by random edits until it is valid again, written canonically.</summary>
    private static string RandomValidTree(Random random)
    {
        while (true)
        {
            MockupEditSession.TryOpen(Seeds[random.Next(Seeds.Length)], out var session, out _);
            for (var i = 0; i < GrowEdits; i++)
            {
                session!.Apply(RandomEdit(random, session));
            }

            if (session!.Document is { } document)
            {
                return MockupWriter.Write(document);
            }
        }
    }

    private static MockupEdit RandomEdit(Random random, MockupEditSession session)
    {
        // #198: a group of edits is one edit too, so it is undone and redone in one step like any other. Its members
        // are drawn against the same tree, so a later one may name what an earlier one moved: refusals are in the mix.
        if (random.Next(8) == 0)
        {
            return MockupEdit.Together([.. Enumerable.Range(0, random.Next(2, 4)).Select(_ => RandomEdit(random, session))]);
        }

        var tree = (JsonObject)JsonNode.Parse(session.Json)!;
        var nodes = Paths(tree);
        var ids = nodes.Select(p => p.Node["id"]?.GetValue<string>()).OfType<string>().ToList();
        var screens = nodes.Where(p => p.Path.Length == 1).Select(p => p.Node["id"]?.GetValue<string>()).OfType<string>().ToList();

        MockupTarget AnyNode()
        {
            if (nodes.Count == 0 || random.Next(10) == 0)
            {
                return random.Next(2) == 0 ? MockupTarget.ById("missing") : MockupTarget.At(random.Next(5), random.Next(5));
            }

            var pick = nodes[random.Next(nodes.Count)];
            return pick.Node["id"] is { } id && random.Next(2) == 0
                ? MockupTarget.ById(id.GetValue<string>())
                : MockupTarget.At(pick.Path);
        }

        MockupTarget AnyParent() => random.Next(6) == 0 ? MockupTarget.Screens : AnyNode();

        string Pick(params string[] options) => options[random.Next(options.Length)];

        var n = random.Next(1_000_000);
        switch (random.Next(13))
        {
            case 0:
            case 1:
                var palette = new[]
                {
                    $$"""{"type": "button", "text": "B{{n}}"}""",
                    """{"type": "text", "text": "Words"}""",
                    """{"type": "container", "layout": "row"}""",
                    """{"type": "panel", "text": "Tab"}""",
                    $$"""{"type": "window", "id": "w{{n}}"}""",
                    """{"type": "field", "text": "Name"}""",
                    $$"""{"type": "dialog", "id": "d{{n}}"}""",
                    $$"""{"type": "button", "id": "b{{n}}", "text": "Go", "on": {"click": {"go": "{{(screens.Count > 0 ? screens[0] : "main")}}" """ + "}}}",
                    """{"type": "field"}""",
                };
                return MockupEdit.Insert(AnyParent(), random.Next(4), (JsonObject)JsonNode.Parse(palette[random.Next(palette.Length)])!);

            case 2:
                return MockupEdit.Remove(AnyNode());

            case 3:
            case 4:
                return MockupEdit.Move(AnyNode(), AnyParent(), random.Next(4));

            case 5:
            case 6:
                var name = Pick("text", "layout", "cols", "placeholder", "required", "input", "options", "data", "show", "state");
                JsonNode? value = name switch
                {
                    "text" or "placeholder" => random.Next(3) == 0 ? null : JsonValue.Create($"T{n}"),
                    "layout" => random.Next(3) == 0 ? null : JsonValue.Create(Pick("row", "column", "grid", "spiral")),
                    "cols" => random.Next(3) == 0 ? null : JsonValue.Create(random.Next(0, 14)),
                    "required" => JsonValue.Create(random.Next(2) == 0),
                    "input" => JsonValue.Create(Pick("text", "select", "checkbox")),
                    "options" => random.Next(2) == 0 ? null : new JsonArray("a", "b"),
                    "data" => random.Next(2) == 0 ? null : JsonValue.Create(Pick("rows", "people", "nothing")),
                    "show" => random.Next(2) == 0 ? null : JsonValue.Create(Pick("a", "name")),
                    _ => random.Next(2) == 0 ? null : JsonValue.Create(Pick("loading", "hidden")),
                };
                return MockupEdit.SetProperty(AnyNode(), name, value);

            case 7:
            case 8:
                var map = (MockupEntryMap)random.Next(4);
                return map switch
                {
                    MockupEntryMap.States => MockupEdit.SetEntry(AnyNode(), map, Pick("loading", "hidden", "disabled", "bogus"),
                        random.Next(3) == 0 ? null : JsonNode.Parse(Pick("{}", """{"text": "Busy"}"""))),
                    MockupEntryMap.At => MockupEdit.SetEntry(AnyNode(), map, Pick("wide", "narrow", "tablet"),
                        random.Next(3) == 0 ? null : JsonNode.Parse("""{"text": "Small"}""")),
                    MockupEntryMap.On => MockupEdit.SetEntry(AnyNode(), map, Pick("click", "submit"),
                        random.Next(3) == 0 || ids.Count == 0 ? null : JsonNode.Parse(
                            $$"""{"{{Pick("go", "open", "close")}}": "{{ids[random.Next(ids.Count)]}}"}""")),
                    _ => MockupEdit.SetEntry(AnyNode(), map, Pick("label", "heading", "decorative"),
                        random.Next(3) == 0 ? null : JsonValue.Create(random.Next(0, 8))),
                };

            case 9:
                return MockupEdit.RenameId(AnyNode(), random.Next(4) == 0 ? null : $"r{n}");

            case 10:
                return MockupEdit.SetTitle(random.Next(3) == 0 ? null : $"Title {n}");

            case 11:
                return random.Next(2) == 0
                    ? MockupEdit.InsertViewport(random.Next(3), Pick("wide", "narrow", "tablet"), random.Next(100, 2000))
                    : MockupEdit.RemoveViewport(Pick("wide", "narrow", "tablet"));

            default:
                return MockupEdit.SetDataSet(Pick("rows", "people", "teams"), random.Next(3) == 0
                    ? null
                    : (JsonArray)JsonNode.Parse(Pick("""[{"a": 1, "b": "y"}]""", """[{"name": "Ada"}]""", "[]"))!);
        }
    }

    private static List<(int[] Path, JsonObject Node)> Paths(JsonObject tree)
    {
        var found = new List<(int[] Path, JsonObject Node)>();

        void Walk(JsonArray? list, int[] parent)
        {
            for (var i = 0; i < (list?.Count ?? 0); i++)
            {
                if (list![i] is JsonObject node)
                {
                    int[] path = [.. parent, i];
                    found.Add((path, node));
                    Walk(node["children"] as JsonArray, path);
                }
            }
        }

        Walk(tree["screens"] as JsonArray, []);
        return found;
    }
}
