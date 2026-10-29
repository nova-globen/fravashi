using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 214 (plan @39): the studio's side of the page-to-studio message, which is a trust boundary. bunit fakes
/// the interop, so it cannot see what <c>prototypeEdit.js</c> lets through; these tests run the real file under Node
/// (which the CI runner and the container carry) and hand its <c>accept</c> the messages a page, or anything else in
/// the window, could send. Only a message from the frame's own window, in the one shape, gives an anchor.
/// </summary>
public sealed class PrototypeEditScriptTests
{
    private static string WebProject()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fravashi.sln")))
        {
            root = root.Parent;
        }

        Assert.True(root is not null, "the test must be able to locate the repository root (Fravashi.sln)");
        return Path.Combine(root!.FullName, "src", "Fravashi.Web");
    }

    private static string ScriptPath => Path.Combine(WebProject(), "wwwroot", "prototypeEdit.js");

    /// <summary>Runs <paramref name="driver"/> after the real script, under Node, and returns what it printed as JSON.</summary>
    private static JsonElement Run(string driver)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "driver.js");
            File.WriteAllText(file, $$"""
                const fs = require("fs");
                const vm = require("vm");
                const listeners = [];
                globalThis.window = globalThis;
                globalThis.addEventListener = (type, handler) => listeners.push({ type, handler });
                globalThis.removeEventListener = (type, handler) => {
                  const at = listeners.findIndex(l => l.type === type && l.handler === handler);
                  if (at >= 0) { listeners.splice(at, 1); }
                };
                vm.runInThisContext(fs.readFileSync({{JsonSerializer.Serialize(ScriptPath)}}, "utf8"));
                const edit = window.fravashiPrototypeEdit;
                (async () => {
                {{driver}}
                })().catch(e => { console.error(e); process.exit(2); });
                """);

            var start = new ProcessStartInfo("node", [file])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(30_000), "node did not finish within 30 seconds");
            Assert.True(process.ExitCode == 0, $"node failed: {error.Result}");
            return JsonDocument.Parse(output.Result).RootElement.Clone();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [NodeFact]
    public void Only_a_message_from_the_frame_in_the_one_shape_gives_an_anchor()
    {
        var answers = Run("""
            const frameWindow = {};
            const frame = { contentWindow: frameWindow };
            const from = (data, source = frameWindow) => edit.accept({ source, data }, frame);
            const good = () => ({ mk: "select", anchor: "next" });
            const withSymbol = good(); withSymbol[Symbol("x")] = 1;
            const inherited = Object.create({ anchor: "next" }); inherited.mk = "select";
            const bare = Object.create(null); bare.mk = "select"; bare.anchor = "next";
            class Shaped { constructor() { this.mk = "select"; this.anchor = "next"; } }
            console.log(JSON.stringify({
              good: from(good()),
              longest: from({ mk: "select", anchor: "a".repeat(1024) }) !== null,
              otherWindow: from(good(), {}),
              noSource: from(good(), null),
              frameGone: edit.accept({ source: null, data: good() }, { contentWindow: null }),
              noFrame: edit.accept({ source: frameWindow, data: good() }, null),
              extraKey: from({ mk: "select", anchor: "next", go: "javascript:alert(1)" }),
              missingAnchor: from({ mk: "select", other: "next" }),
              wrongMk: from({ mk: "navigate", anchor: "next" }),
              anchorNumber: from({ mk: "select", anchor: 5 }),
              anchorObject: from({ mk: "select", anchor: { toString: "next" } }),
              anchorEmpty: from({ mk: "select", anchor: "" }),
              anchorTooLong: from({ mk: "select", anchor: "a".repeat(1025) }),
              stringPayload: from('{"mk":"select","anchor":"next"}'),
              nullPayload: from(null),
              arrayPayload: from(["select", "next"]),
              symbolKey: from(withSymbol),
              inheritedAnchor: from(inherited),
              nullPrototype: from(bare),
              classInstance: from(new Shaped()),
            }));
            """);

        var good = answers.GetProperty("good");
        Assert.Equal("next", good.GetProperty("anchor").GetString());
        Assert.Equal(JsonValueKind.Null, good.GetProperty("part").ValueKind);
        Assert.Equal(JsonValueKind.Null, good.GetProperty("column").ValueKind);
        Assert.True(answers.GetProperty("longest").GetBoolean());
        foreach (var refused in answers.EnumerateObject().Where(p => p.Name is not ("good" or "longest")))
        {
            Assert.True(refused.Value.ValueKind == JsonValueKind.Null, $"{refused.Name} should be ignored, and gave {refused.Value}");
        }
    }

    [NodeFact]
    public void Attach_forwards_only_what_accept_lets_through_and_detach_removes_the_listener()
    {
        var answers = Run("""
            const frameWindow = {};
            const frame = { contentWindow: frameWindow };
            const calls = [];
            const dotnet = { invokeMethodAsync: (name, ...args) => { calls.push([name, ...args]); return Promise.resolve(); } };
            const handle = edit.attach(frame, dotnet);
            const registered = listeners.filter(l => l.type === "message").length;
            const deliver = (event) => listeners.filter(l => l.type === "message").forEach(l => l.handler(event));
            deliver({ source: {}, data: { mk: "select", anchor: "elsewhere" } });
            deliver({ source: frameWindow, data: { mk: "select", anchor: "next", extra: 1 } });
            deliver({ source: frameWindow, data: { mk: "select", anchor: "next" } });
            edit.detach(handle);
            const left = listeners.length;
            deliver({ source: frameWindow, data: { mk: "select", anchor: "after" } });
            edit.detach(handle);
            console.log(JSON.stringify({ registered, calls, left }));
            """);

        Assert.Equal(1, answers.GetProperty("registered").GetInt32());
        var call = Assert.Single(answers.GetProperty("calls").EnumerateArray().ToList());
        Assert.Equal(["SelectFromPage", "next", null, null], call.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Null ? null : e.GetString()));
        Assert.Equal(0, answers.GetProperty("left").GetInt32());
    }

    [NodeFact]
    public void A_part_is_accepted_only_in_its_own_shape()
    {
        // Backlog 238 (plan @52): a click on a title, a caption or a tab adds its part; a column header adds its column too.
        var answers = Run("""
            const frameWindow = {};
            const frame = { contentWindow: frameWindow };
            const from = (data) => edit.accept({ source: frameWindow, data }, frame);
            const calls = [];
            const dotnet = { invokeMethodAsync: (name, ...args) => { calls.push([name, ...args]); return Promise.resolve(); } };
            const handle = edit.attach(frame, dotnet);
            listeners.filter(l => l.type === "message").forEach(l => l.handler({ source: frameWindow, data: { mk: "select", anchor: "people", part: "columnHeader", column: "email" } }));
            edit.detach(handle);
            console.log(JSON.stringify({
              accepted: {
                title: from({ mk: "select", anchor: "home", part: "title" }),
                caption: from({ mk: "select", anchor: "people", part: "caption" }),
                tab: from({ mk: "select", anchor: "one", part: "tab" }),
                header: from({ mk: "select", anchor: "people", part: "columnHeader", column: "email" }),
              },
              refused: {
                unknownPart: from({ mk: "select", anchor: "home", part: "heading" }),
                nodePart: from({ mk: "select", anchor: "home", part: "node" }),
                partNumber: from({ mk: "select", anchor: "home", part: 1 }),
                partNull: from({ mk: "select", anchor: "home", part: null }),
                columnOnTitle: from({ mk: "select", anchor: "home", part: "title", column: "email" }),
                headerWithoutColumn: from({ mk: "select", anchor: "people", part: "columnHeader" }),
                headerEmptyColumn: from({ mk: "select", anchor: "people", part: "columnHeader", column: "" }),
                headerLongColumn: from({ mk: "select", anchor: "people", part: "columnHeader", column: "c".repeat(1025) }),
                headerColumnObject: from({ mk: "select", anchor: "people", part: "columnHeader", column: {} }),
                columnWithoutPart: from({ mk: "select", anchor: "people", column: "email" }),
                extraKey: from({ mk: "select", anchor: "home", part: "title", go: "x" }),
                fiveKeys: from({ mk: "select", anchor: "people", part: "columnHeader", column: "email", go: "x" }),
              },
              calls,
            }));
            """);

        var accepted = answers.GetProperty("accepted");
        Assert.Equal(("home", "title"), (accepted.GetProperty("title").GetProperty("anchor").GetString(), accepted.GetProperty("title").GetProperty("part").GetString()));
        Assert.Equal("caption", accepted.GetProperty("caption").GetProperty("part").GetString());
        Assert.Equal("tab", accepted.GetProperty("tab").GetProperty("part").GetString());
        Assert.Equal(JsonValueKind.Null, accepted.GetProperty("tab").GetProperty("column").ValueKind);
        Assert.Equal("email", accepted.GetProperty("header").GetProperty("column").GetString());
        foreach (var refused in answers.GetProperty("refused").EnumerateObject())
        {
            Assert.True(refused.Value.ValueKind == JsonValueKind.Null, $"{refused.Name} should be ignored, and gave {refused.Value}");
        }

        var call = Assert.Single(answers.GetProperty("calls").EnumerateArray().ToList());
        Assert.Equal(["SelectFromPage", "people", "columnHeader", "email"], call.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void The_listener_script_is_shipped_and_loaded_by_the_page()
    {
        // bunit fakes the interop, so nothing else notices a missing script: in a real window Edit would throw
        // "fravashiPrototypeEdit is not defined".
        Assert.True(File.Exists(ScriptPath), $"the studio should ship the listener at {ScriptPath}");
        var app = File.ReadAllText(Path.Combine(WebProject(), "Components", "App.razor"));
        Assert.Contains("<script src=\"prototypeEdit.js\"></script>", app, StringComparison.Ordinal);
    }
}

/// <summary>
/// A fact that runs only where a <c>node</c> is on PATH, skipped with its reason otherwise. Set in the constructor
/// because <c>Assert.Skip</c> needs xunit v3. It never searches anywhere but PATH.
/// </summary>
public sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe" } : ["node"];
        var found = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => names.Any(name => File.Exists(Path.Combine(dir, name))));
        if (!found)
        {
            Skip = "No node on PATH: the listener script's boundary is run under Node, which the CI runner and the container carry.";
        }
    }
}
