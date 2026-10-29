using System.Text.Json;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Framework;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Domain.Tests;

/// <summary>
/// The T3 artifact contracts: validation rules and the canonical serialization for agents, commands, hooks,
/// MCP servers and plugins. Frontmatter kinds (agents, commands) follow the quoted-value/unquoted-key contract
/// (ADR 0005 / D03-6); the JSON kinds (hooks, MCP, plugins) emit valid, stable JSON. MCP secret values never
/// serialize as plaintext - only an env-expansion placeholder.
/// </summary>
public class FrameworkArtifactContractTests
{
    // ── Agents (subagent contract) ───────────────────────────────────────────────────────────────────────────

    private static AgentDefinition ValidAgent() => new()
    {
        Name = "code-reviewer",
        Description = "Use to review code changes for correctness and style before a commit.",
        SystemPrompt = "You are a meticulous reviewer.",
    };

    [Fact]
    public void A_minimal_valid_agent_passes() => AgentValidation.IsValid(ValidAgent()).Should().BeTrue();

    [Fact]
    public void Agent_requires_name_description_and_system_prompt()
    {
        var agent = new AgentDefinition { Name = "Bad Name", Description = "", SystemPrompt = "  " };
        var fields = AgentValidation.Validate(agent).Select(e => e.Field).ToHashSet();
        fields.Should().Contain(["name", "description", "system_prompt"]);
    }

    [Fact]
    public void Agent_brand_colour_is_validated_only_when_present()
    {
        AgentValidation.Validate(ValidAgent() with { BrandColor = "" }).Should().NotContain(e => e.Field == "brand_color");
        AgentValidation.Validate(ValidAgent() with { BrandColor = "nope" }).Should().Contain(e => e.Field == "brand_color");
    }

    [Fact]
    public void Agent_markdown_quotes_values_leaves_keys_unquoted_and_conditionally_emits_tools_and_model()
    {
        var agent = ValidAgent() with
        {
            Tools = ["Read", "Grep"],
            Model = "sonnet",
        };
        var md = AgentDocument.ToAgentMarkdown(agent);

        md.Should().StartWith("---\n");
        md.Should().Contain("name: \"code-reviewer\"");        // value quoted, key unquoted
        md.Should().Contain("description: \"");
        md.Should().Contain("tools: \"Read, Grep\"");           // comma-joined scalar
        md.Should().Contain("model: \"sonnet\"");
        md.Should().Contain("You are a meticulous reviewer.");  // body after frontmatter

        // Omitted when empty.
        var bare = AgentDocument.ToAgentMarkdown(ValidAgent());
        bare.Should().NotContain("tools:");
        bare.Should().NotContain("model:");
    }

    // ── Commands (slash command) ─────────────────────────────────────────────────────────────────────────────

    private static CommandDefinition ValidCommand() => new()
    {
        Name = "review-pr",
        Description = "Review the open pull request.",
        Body = "Review PR $ARGUMENTS.",
    };

    [Fact]
    public void Command_requires_name_description_and_body()
    {
        var fields = CommandValidation.Validate(new CommandDefinition()).Select(e => e.Field).ToHashSet();
        fields.Should().Contain(["name", "description", "body"]);
    }

    [Fact]
    public void Command_markdown_quotes_values_and_conditionally_emits_optional_frontmatter()
    {
        var command = ValidCommand() with
        {
            ArgumentHint = "[pr-number]",
            AllowedTools = ["Read", "Bash(git *)"],
            Model = "opus",
        };
        var md = CommandDocument.ToCommandMarkdown(command);

        md.Should().Contain("description: \"Review the open pull request.\"");
        md.Should().Contain("argument-hint: \"[pr-number]\"");
        md.Should().Contain("allowed-tools:\n  - \"Read\"\n  - \"Bash(git *)\"");  // block list, each item quoted
        md.Should().Contain("model: \"opus\"");
        md.Should().Contain("Review PR $ARGUMENTS.");

        var bare = CommandDocument.ToCommandMarkdown(ValidCommand());
        bare.Should().NotContain("argument-hint:");
        bare.Should().NotContain("allowed-tools:");
        bare.Should().NotContain("model:");
    }

    // ── Hooks ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hook_requires_a_command_and_a_positive_timeout()
    {
        var bad = new HookDefinition { Name = "h", Command = "", TimeoutSeconds = 0 };
        var fields = HookValidation.Validate(bad).Select(e => e.Field).ToHashSet();
        fields.Should().Contain(["command", "timeout"]);
    }

    [Fact]
    public void Hook_settings_json_carries_event_matcher_and_command_handler()
    {
        var hook = new HookDefinition
        {
            Name = "format-on-edit",
            Event = HookEvent.PostToolUse,
            Matcher = "Edit|Write",
            Command = "prettier --write .",
            TimeoutSeconds = 30,
        };
        var json = HookDocument.ToSettingsJson(hook);

        using var doc = JsonDocument.Parse(json); // valid JSON
        var group = doc.RootElement.GetProperty("hooks").GetProperty("PostToolUse")[0];
        group.GetProperty("matcher").GetString().Should().Be("Edit|Write");
        var handler = group.GetProperty("hooks")[0];
        handler.GetProperty("type").GetString().Should().Be("command");
        handler.GetProperty("command").GetString().Should().Be("prettier --write .");
        handler.GetProperty("timeout").GetInt32().Should().Be(30);
    }

    [Fact]
    public void Hook_matcher_and_timeout_are_omitted_when_unset()
    {
        var hook = new HookDefinition { Name = "on-stop", Event = HookEvent.Stop, Command = "echo done" };
        var json = HookDocument.ToSettingsJson(hook);
        var group = JsonDocument.Parse(json).RootElement.GetProperty("hooks").GetProperty("Stop")[0];
        group.TryGetProperty("matcher", out _).Should().BeFalse();
        group.GetProperty("hooks")[0].TryGetProperty("timeout", out _).Should().BeFalse();
    }

    // ── MCP servers (secret handling) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Mcp_stdio_requires_a_command_and_http_requires_a_url()
    {
        McpServerValidation.Validate(new McpServerDefinition { Name = "s", Transport = McpTransport.Stdio })
            .Should().Contain(e => e.Field == "command");
        McpServerValidation.Validate(new McpServerDefinition { Name = "s", Transport = McpTransport.Http })
            .Should().Contain(e => e.Field == "url");
    }

    [Fact]
    public void Mcp_json_renders_a_secret_env_value_as_a_placeholder_never_plaintext()
    {
        var server = new McpServerDefinition
        {
            Name = "airtable",
            Transport = McpTransport.Stdio,
            Command = "npx",
            Args = ["-y", "airtable-mcp"],
            Env =
            [
                McpEnvEntry.Plain("AIRTABLE_BASE", "app123"),
                new McpEnvEntry("AIRTABLE_API_KEY", null, IsSecret: true, SecretRef: "mcp/x/secret"),
            ],
        };
        var json = McpServerDocument.ToMcpJson(server);

        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("mcpServers").GetProperty("airtable");
        entry.GetProperty("type").GetString().Should().Be("stdio");
        var env = entry.GetProperty("env");
        env.GetProperty("AIRTABLE_BASE").GetString().Should().Be("app123");
        env.GetProperty("AIRTABLE_API_KEY").GetString().Should().Be("${AIRTABLE_API_KEY}"); // placeholder
        json.Should().NotContain("mcp/x/secret");  // the secret ref never leaks into the config
    }

    [Fact]
    public void Mcp_http_renders_url_and_header_placeholders()
    {
        var server = new McpServerDefinition
        {
            Name = "remote",
            Transport = McpTransport.Http,
            Url = "https://mcp.example.com/mcp",
            Headers = [new McpEnvEntry("Authorization", null, IsSecret: true, SecretRef: "mcp/y/tok")],
        };
        var json = McpServerDocument.ToMcpJson(server);
        var entry = JsonDocument.Parse(json).RootElement.GetProperty("mcpServers").GetProperty("remote");
        entry.GetProperty("type").GetString().Should().Be("http");
        entry.GetProperty("url").GetString().Should().Be("https://mcp.example.com/mcp");
        entry.GetProperty("headers").GetProperty("Authorization").GetString().Should().Be("${Authorization}");
    }

    // ── Plugins ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("2.1.0-beta.1", true)]
    [InlineData("1.0", false)]
    [InlineData("v1.0.0", false)]
    public void Plugin_version_when_present_must_be_semver(string version, bool valid)
    {
        var plugin = new PluginDefinition { Name = "p", Version = version };
        PluginValidation.Validate(plugin).Any(e => e.Field == "version").Should().Be(!valid);
    }

    [Fact]
    public void Plugin_json_emits_author_object_keywords_array_and_folds_single_component_to_a_string()
    {
        var plugin = new PluginDefinition
        {
            Name = "deploy-tools",
            Version = "1.2.0",
            Description = "Deployment helpers.",
            Author = new PluginAuthor("Ada", "ada@example.com"),
            License = "MIT",
            Keywords = ["deployment", "ci-cd"],
            Components = new PluginComponents
            {
                Commands = ["./commands/deploy.md"],                 // one → string
                Agents = ["./agents/a.md", "./agents/b.md"],         // many → array
            },
        };
        var json = PluginDocument.ToPluginJson(plugin);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("name").GetString().Should().Be("deploy-tools");
        root.GetProperty("version").GetString().Should().Be("1.2.0");
        root.GetProperty("author").GetProperty("name").GetString().Should().Be("Ada");
        root.GetProperty("author").GetProperty("email").GetString().Should().Be("ada@example.com");
        root.GetProperty("keywords").GetArrayLength().Should().Be(2);
        root.GetProperty("commands").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("agents").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public void Plugin_json_omits_unset_optional_fields()
    {
        var json = PluginDocument.ToPluginJson(new PluginDefinition { Name = "bare" });
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("name").GetString().Should().Be("bare");
        doc.RootElement.TryGetProperty("version", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("author", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("keywords", out _).Should().BeFalse();
    }

    // A cross-kind sanity check the targets metadata carries the runtime enum on agents.
    [Fact]
    public void Agent_targets_are_recorded_as_metadata()
    {
        var agent = ValidAgent() with { Targets = [AgentTool.Claude] };
        agent.Targets.Should().Equal(AgentTool.Claude);
    }
}
