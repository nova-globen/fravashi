using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Framework;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Framework;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 69: the stored Settings > Framework artifacts must become real files the CLI reads, under the session
/// config area (never the workspace copy), rebuilt fresh from the current rows so a removed row leaves no
/// projection. The kinds that cannot take effect yet (MCP servers need the unbuilt graph server; plugins need a
/// loader layout the CLI does not expose here) are projected for VISIBILITY and recorded as deferred, not
/// silently dropped.
/// </summary>
public class SessionFrameworkProjectorTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "fravashi-framework-" + Guid.NewGuid().ToString("N"));

    private readonly InMemoryFrameworkStore _store = new();

    private SessionFrameworkProjector Projector() => new(_dataDirectory, _store);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
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

    [Fact]
    public void A_stored_skill_lands_as_an_invocable_skill_md_under_the_config_home()
    {
        _store.Put(FrameworkArtifactKind.Skill, "haiku-writer", FrameworkPayload<SkillDefinition>.Serialize(
            new SkillDefinition
            {
                Name = "haiku-writer",
                Description = "Use when the user asks for a haiku about anything.",
                Content = "# Haiku writer\n\nWrite a three-line haiku.",
            }));

        var projection = Projector().Project(AgentSessionId.New());

        var path = Path.Combine(projection.ConfigDirectory, "skills", "haiku-writer", "SKILL.md");
        File.Exists(path).Should().BeTrue();
        projection.Written.Should().Contain(path);
        var content = File.ReadAllText(path);
        content.Should().StartWith("---\n");
        content.Should().Contain("name: \"haiku-writer\"");
        content.Should().Contain("Use when the user asks for a haiku");
        content.Should().Contain("Write a three-line haiku.");
    }

    [Fact]
    public void A_stored_subagent_lands_as_agent_markdown_with_its_frontmatter()
    {
        _store.Put(FrameworkArtifactKind.Agent, "reviewer", FrameworkPayload<AgentDefinition>.Serialize(
            new AgentDefinition
            {
                Name = "reviewer",
                Description = "Use to review a diff for correctness.",
                SystemPrompt = "You are a meticulous code reviewer.",
                Model = "haiku",
            }));

        var projection = Projector().Project(AgentSessionId.New());

        var path = Path.Combine(projection.ConfigDirectory, "agents", "reviewer.md");
        File.Exists(path).Should().BeTrue();
        var content = File.ReadAllText(path);
        content.Should().Contain("name: \"reviewer\"");
        content.Should().Contain("description:");
        content.Should().Contain("model: \"haiku\"");
        content.Should().Contain("You are a meticulous code reviewer.");
    }

    [Fact]
    public void A_stored_command_lands_as_a_slash_command_file()
    {
        _store.Put(FrameworkArtifactKind.Command, "greet", FrameworkPayload<CommandDefinition>.Serialize(
            new CommandDefinition
            {
                Name = "greet",
                Description = "Greet someone.",
                Body = "Say hello to $ARGUMENTS.",
            }));

        var projection = Projector().Project(AgentSessionId.New());

        var path = Path.Combine(projection.ConfigDirectory, "commands", "greet.md");
        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Contain("Say hello to $ARGUMENTS.");
    }

    [Fact]
    public void A_stored_hook_merges_into_the_config_home_settings_under_its_event()
    {
        _store.Put(FrameworkArtifactKind.Hook, "guard-bash", FrameworkPayload<HookDefinition>.Serialize(
            new HookDefinition
            {
                Name = "guard-bash",
                Event = HookEvent.PreToolUse,
                Matcher = "Bash",
                Command = "echo blocked",
                TimeoutSeconds = 5,
            }));

        var projection = Projector().Project(AgentSessionId.New());

        var settingsPath = Path.Combine(projection.ConfigDirectory, "settings.json");
        File.Exists(settingsPath).Should().BeTrue();
        projection.Written.Should().Contain(settingsPath);

        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var group = document.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0];
        group.GetProperty("matcher").GetString().Should().Be("Bash");
        var handler = group.GetProperty("hooks")[0];
        handler.GetProperty("type").GetString().Should().Be("command");
        handler.GetProperty("command").GetString().Should().Be("echo blocked");
        handler.GetProperty("timeout").GetInt32().Should().Be(5);
    }

    [Fact]
    public void Multiple_hooks_on_one_event_are_merged_into_one_event_array()
    {
        _store.Put(FrameworkArtifactKind.Hook, "a", FrameworkPayload<HookDefinition>.Serialize(
            new HookDefinition { Name = "a", Event = HookEvent.PreToolUse, Command = "echo a" }));
        _store.Put(FrameworkArtifactKind.Hook, "b", FrameworkPayload<HookDefinition>.Serialize(
            new HookDefinition { Name = "b", Event = HookEvent.PreToolUse, Command = "echo b" }));

        var projection = Projector().Project(AgentSessionId.New());

        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(projection.ConfigDirectory, "settings.json")));
        document.RootElement.GetProperty("hooks").GetProperty("PreToolUse").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void A_removed_row_is_absent_from_the_next_sessions_projection()
    {
        var artifactId = _store.Put(FrameworkArtifactKind.Skill, "temporary", FrameworkPayload<SkillDefinition>.Serialize(
            new SkillDefinition { Name = "temporary", Description = "A skill.", Content = "Body." }));

        var first = Projector().Project(AgentSessionId.New());
        File.Exists(Path.Combine(first.ConfigDirectory, "skills", "temporary", "SKILL.md")).Should().BeTrue();

        _store.Remove(artifactId);

        var second = Projector().Project(AgentSessionId.New());
        Directory.Exists(Path.Combine(second.ConfigDirectory, "skills", "temporary")).Should().BeFalse(
            "a deleted row simply is not written, because the projection is rebuilt fresh from the current rows");
    }

    [Fact]
    public void Re_projecting_the_same_session_reflects_the_current_rows_and_drops_stale_files()
    {
        var session = AgentSessionId.New();
        var artifactId = _store.Put(FrameworkArtifactKind.Skill, "gone", FrameworkPayload<SkillDefinition>.Serialize(
            new SkillDefinition { Name = "gone", Description = "A skill.", Content = "Body." }));

        var projector = Projector();
        projector.Project(session);
        var stale = Path.Combine(projector.ConfigDirectoryFor(session), "skills", "gone", "SKILL.md");
        File.Exists(stale).Should().BeTrue();

        _store.Remove(artifactId);
        projector.Project(session);

        File.Exists(stale).Should().BeFalse("the config home is wiped and rebuilt, so nothing stale survives");
    }

    [Fact]
    public void An_mcp_server_is_projected_for_visibility_but_deferred_with_a_reason()
    {
        _store.Put(FrameworkArtifactKind.McpServer, "graph", FrameworkPayload<McpServerDefinition>.Serialize(
            new McpServerDefinition
            {
                Name = "graph",
                Transport = McpTransport.Stdio,
                Command = "node",
                Args = ["server.js"],
            }));

        var projection = Projector().Project(AgentSessionId.New());

        // Recorded as deferred, not silently dropped.
        var deferred = projection.Deferred.Should().ContainSingle().Subject;
        deferred.Kind.Should().Be(FrameworkArtifactKind.McpServer);
        deferred.Name.Should().Be("graph");
        deferred.Reason.Should().NotBeNullOrWhiteSpace();

        // Visible and diffable...
        File.Exists(deferred.Path).Should().BeTrue();
        File.ReadAllText(deferred.Path).Should().Contain("mcpServers");

        // ...but under projected-not-active, so the CLI never scans it as live config, and it is NOT a written
        // (behaviour-changing) file.
        deferred.Path.Should().Contain("projected-not-active");
        projection.Written.Should().NotContain(deferred.Path);
        File.Exists(Path.Combine(projection.ConfigDirectory, ".mcp.json")).Should().BeFalse();
        File.Exists(Path.Combine(projection.ConfigDirectory, "projected-not-active", "README.md")).Should().BeTrue();
    }

    [Fact]
    public void A_plugin_is_projected_for_visibility_but_deferred_with_a_reason()
    {
        _store.Put(FrameworkArtifactKind.Plugin, "toolbox", FrameworkPayload<PluginDefinition>.Serialize(
            new PluginDefinition { Name = "toolbox", Version = "1.0.0" }));

        var projection = Projector().Project(AgentSessionId.New());

        var deferred = projection.Deferred.Should().ContainSingle().Subject;
        deferred.Kind.Should().Be(FrameworkArtifactKind.Plugin);
        File.Exists(deferred.Path).Should().BeTrue();
        deferred.Path.Should().Contain("projected-not-active");
        File.ReadAllText(deferred.Path).Should().Contain("toolbox");
    }

    [Fact]
    public void An_empty_framework_projects_nothing_and_asks_for_no_config_home()
    {
        var projection = Projector().Project(AgentSessionId.New());

        projection.Written.Should().BeEmpty();
        projection.Deferred.Should().BeEmpty();
        projection.HasContent.Should().BeFalse(
            "with nothing to project the child keeps the default config home, so behaviour is unchanged");
    }

    [Fact]
    public void The_projection_stays_inside_the_session_config_area_never_the_workspace_copy()
    {
        _store.Put(FrameworkArtifactKind.Skill, "s", FrameworkPayload<SkillDefinition>.Serialize(
            new SkillDefinition { Name = "s", Description = "A skill.", Content = "Body." }));

        var session = AgentSessionId.New();
        var projection = Projector().Project(session);

        var expectedRoot = Path.Combine(_dataDirectory, "agent-sessions", session.ToString(), "claude");
        Path.TrimEndingDirectorySeparator(projection.ConfigDirectory)
            .Should().Be(Path.TrimEndingDirectorySeparator(expectedRoot));
        foreach (var file in projection.Written)
        {
            file.Should().StartWith(expectedRoot);
        }
    }

    [Fact]
    public void A_row_whose_name_carries_a_path_separator_cannot_escape_the_config_home()
    {
        _store.Put(FrameworkArtifactKind.Skill, "../../escape", FrameworkPayload<SkillDefinition>.Serialize(
            new SkillDefinition { Name = "../../escape", Description = "A skill.", Content = "Body." }));

        var session = AgentSessionId.New();
        var projection = Projector().Project(session);

        var configHome = projection.ConfigDirectory;
        projection.Written.Should().ContainSingle();
        Path.GetFullPath(projection.Written[0]).Should().StartWith(Path.GetFullPath(configHome),
            "a name is data, so a separator or a .. in it must not write outside the config home");
    }

    // ---- the CLI's conversations under the config home (owner report 2026-09-15) --------------

    [Fact]
    public void The_cli_keys_a_working_directory_the_way_it_was_measured_to()
    {
        // Backlog 155: measured as -workspaces-<id> in the installed product's session homes.
        SessionFrameworkProjector.ProjectKeyFor("/workspaces/58cb481484fe2efe").Should().Be("-workspaces-58cb481484fe2efe");
        SessionFrameworkProjector.ProjectKeyFor(@"C:\code\app").Should().Be("C--code-app");
    }

    [Fact]
    public void Memory_is_shared_by_every_session_of_a_workspace_and_a_sessions_own_notes_are_kept()
    {
        // Backlog 155: two sessions, two config homes, one memory. The first session already wrote a note into its
        // own folder before the memory was shared; linking keeps it and the second session reads it.
        StoreASkill();
        var projector = new SessionFrameworkProjector(_dataDirectory, _store, sourceConfigHome: () => null);
        const string copy = "/workspaces/w1";
        var key = SessionFrameworkProjector.ProjectKeyFor(copy);

        var first = projector.Project(AgentSessionId.New()).ConfigDirectory;
        var ownFolder = Path.Combine(first, "projects", key, "memory");
        Directory.CreateDirectory(ownFolder);
        File.WriteAllText(Path.Combine(ownFolder, "note.md"), "remembered in the first conversation\n");

        var linked = projector.LinkMemory(first, copy);
        if (!linked)
        {
            // A host that refuses a symbolic link (an unprivileged Windows account) keeps the session's own folder;
            // the sharing is proved where links are allowed, which the container is.
            File.Exists(Path.Combine(ownFolder, "note.md")).Should().BeTrue("nothing is lost when the link is refused");
            return;
        }

        var shared = projector.MemoryDirectoryFor(copy);
        File.ReadAllText(Path.Combine(shared, "note.md")).Should().Contain("first conversation");
        new DirectoryInfo(ownFolder).LinkTarget.Should().NotBeNull("the session's folder is now the link");

        var second = projector.Project(AgentSessionId.New()).ConfigDirectory;
        projector.LinkMemory(second, copy).Should().BeTrue();
        File.ReadAllText(Path.Combine(second, "projects", key, "memory", "note.md")).Should().Contain("first conversation");

        // And writing through the second session's link lands in the shared folder for the next one.
        File.WriteAllText(Path.Combine(second, "projects", key, "memory", "more.md"), "second\n");
        File.Exists(Path.Combine(shared, "more.md")).Should().BeTrue();

        // Linking again is idempotent.
        projector.LinkMemory(second, copy).Should().BeTrue();
    }

    private void StoreASkill() =>
        _store.Put(FrameworkArtifactKind.Skill, "s", FrameworkPayload<SkillDefinition>.Serialize(
            new SkillDefinition { Name = "s", Description = "A skill.", Content = "Body." }));

    private static string PutConversation(string configHome, string providerId)
    {
        var file = Path.Combine(configHome, "projects", "-workspaces-w1", providerId + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{}\n");
        return file;
    }

    [Fact]
    public void Re_projecting_a_session_keeps_the_conversations_the_cli_wrote_there()
    {
        // A resume re-projects the SAME session's config home, and the wipe used to take the conversation.
        StoreASkill();
        var session = AgentSessionId.New();
        var projection = Projector().Project(session);
        var conversation = PutConversation(projection.ConfigDirectory, "p1");

        Projector().Project(session);

        File.Exists(conversation).Should().BeTrue();
        File.Exists(Path.Combine(projection.ConfigDirectory, "skills", "s", "SKILL.md")).Should().BeTrue();
    }

    [Fact]
    public void A_new_conversation_gets_the_session_home_only_when_something_was_projected()
    {
        var session = AgentSessionId.New();
        var empty = Projector().Project(session);
        Projector().ConfigHomeFor(session, empty, null).Should().BeNull();

        StoreASkill();
        var full = Projector().Project(session);
        Projector().ConfigHomeFor(session, full, null).Should().Be(full.ConfigDirectory);
    }

    [Fact]
    public void A_resume_is_started_with_the_home_that_holds_its_conversation_even_with_nothing_projected()
    {
        var session = AgentSessionId.New();
        var projection = Projector().Project(session);
        PutConversation(projection.ConfigDirectory, "p1");

        Projector().ConfigHomeFor(session, projection, "p1").Should().Be(projection.ConfigDirectory);
    }

    [Fact]
    public void A_resume_of_a_conversation_the_default_home_holds_copies_it_in_when_skills_are_projected()
    {
        var defaultHome = Path.Combine(_dataDirectory, "default-home");
        var original = PutConversation(defaultHome, "p1");
        StoreASkill();
        var projector = new SessionFrameworkProjector(_dataDirectory, _store, sourceConfigHome: () => defaultHome);
        var session = AgentSessionId.New();
        var projection = projector.Project(session);

        projector.ConfigHomeFor(session, projection, "p1").Should().Be(projection.ConfigDirectory);

        File.Exists(Path.Combine(projection.ConfigDirectory, "projects", "-workspaces-w1", "p1.jsonl")).Should().BeTrue();
        File.Exists(original).Should().BeTrue("a copy, never a move: the default home is the CLI's own");
    }

    [Fact]
    public void A_resume_with_nothing_projected_and_no_conversation_here_keeps_the_clis_default_home()
    {
        var session = AgentSessionId.New();
        var projection = new SessionFrameworkProjector(_dataDirectory, _store, sourceConfigHome: () => null).Project(session);

        Projector().ConfigHomeFor(session, projection, "p1").Should().BeNull();
    }
}
