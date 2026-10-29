using Fravashi.Application.Framework;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Framework;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// The generic typed payload serializer used by the T3 kinds. Round-trips each definition through JSON and
/// confirms an MCP secret entry keeps <b>no plaintext</b> in the stored payload (only a secret-store reference).
/// </summary>
public class FrameworkPayloadTests
{
    [Fact]
    public void Agent_round_trips_including_targets_and_model()
    {
        var agent = new AgentDefinition
        {
            Name = "code-reviewer",
            Description = "when to delegate",
            Tools = ["Read", "Grep"],
            Model = "sonnet",
            SystemPrompt = "body",
            DisplayName = "Reviewer",
            Targets = [AgentTool.Claude],
        };

        var back = FrameworkPayload<AgentDefinition>.Deserialize(FrameworkPayload<AgentDefinition>.Serialize(agent));

        back.Should().BeEquivalentTo(agent);
    }

    [Fact]
    public void Command_hook_and_plugin_round_trip()
    {
        var command = new CommandDefinition { Name = "c", Description = "d", Body = "b", AllowedTools = ["Read"] };
        FrameworkPayload<CommandDefinition>.Deserialize(FrameworkPayload<CommandDefinition>.Serialize(command))
            .Should().BeEquivalentTo(command);

        var hook = new HookDefinition { Name = "h", Event = HookEvent.PreCompact, Command = "x", TimeoutSeconds = 5 };
        FrameworkPayload<HookDefinition>.Deserialize(FrameworkPayload<HookDefinition>.Serialize(hook))
            .Should().BeEquivalentTo(hook);

        var plugin = new PluginDefinition
        {
            Name = "p",
            Version = "1.0.0",
            Author = new PluginAuthor("Ada"),
            Keywords = ["a"],
            Components = new PluginComponents { Commands = ["./c.md"] },
        };
        FrameworkPayload<PluginDefinition>.Deserialize(FrameworkPayload<PluginDefinition>.Serialize(plugin))
            .Should().BeEquivalentTo(plugin);
    }

    [Fact]
    public void An_mcp_secret_entry_stores_a_reference_not_the_plaintext()
    {
        var server = new McpServerDefinition
        {
            Name = "airtable",
            Transport = McpTransport.Stdio,
            Command = "npx",
            Env =
            [
                McpEnvEntry.Plain("BASE", "app123"),
                new McpEnvEntry("API_KEY", Value: null, IsSecret: true, SecretRef: "mcp/1/abc"),
            ],
        };

        var payload = FrameworkPayload<McpServerDefinition>.Serialize(server);

        // The secret's plaintext is never in the definition and so never in the payload; only the ref persists.
        payload.Should().Contain("mcp/1/abc");
        payload.Should().NotContain("SECRETVALUE");
        var back = FrameworkPayload<McpServerDefinition>.Deserialize(payload);
        var secret = back.Env.Single(e => e.Key == "API_KEY");
        secret.IsSecret.Should().BeTrue();
        secret.Value.Should().BeNull();
        secret.SecretRef.Should().Be("mcp/1/abc");
    }
}
