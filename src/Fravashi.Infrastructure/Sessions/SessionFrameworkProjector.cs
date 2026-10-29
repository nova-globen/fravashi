using System.Text.Json;
using System.Text.Json.Nodes;
using Fravashi.Application.Framework;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>One artifact kind that was projected only for visibility, not wired into the run, and why.</summary>
/// <param name="Kind">The artifact kind that could not take effect this version.</param>
/// <param name="Name">The artifact's name.</param>
/// <param name="Path">Where the visible-but-inert projection was written.</param>
/// <param name="Reason">Why it is not consumed by the CLI yet.</param>
public sealed record DeferredArtifact(FrameworkArtifactKind Kind, string Name, string Path, string Reason);

/// <summary>
/// What a session's framework projection produced: the CLAUDE_CONFIG_DIR the CLI is pointed at, the files that
/// actually change agent behaviour, and the kinds projected for visibility only.
/// </summary>
/// <param name="ConfigDirectory">The per-session config home exported as <c>CLAUDE_CONFIG_DIR</c>.</param>
/// <param name="Written">Absolute paths of the files the CLI genuinely reads (skills, agents, commands, hooks settings).</param>
/// <param name="Deferred">Kinds written for a user to see and diff, but not yet consumed (MCP servers, plugins).</param>
public sealed record SessionFrameworkProjection(
    string ConfigDirectory,
    IReadOnlyList<string> Written,
    IReadOnlyList<DeferredArtifact> Deferred)
{
    /// <summary>True when anything at all was projected, which is exactly when the child needs CLAUDE_CONFIG_DIR set.</summary>
    public bool HasContent => Written.Count > 0 || Deferred.Count > 0;
}

/// <summary>
/// Projects the stored Settings › Framework artifacts (backlog 69) into the per-session config home the agent
/// CLI reads, alongside <see cref="SessionHookWriter"/>'s approval hook. This is what closes ADR 0005's deferred
/// file-sync: the artifact rows were write-only until something wrote them where the CLI looks.
/// <para>
/// The projection is re-built FRESH from the current rows every time a session starts, into a directory that is
/// wiped first, so a deleted row simply is not written and there is no lockfile to drift. It is VISIBLE and
/// DIFFABLE: real files under <c>&lt;data&gt;/agent-sessions/{id}/claude</c> that a user or a test can read.
/// </para>
/// <para>
/// Containment (plan 2d decision 2): these files go in the session CONFIG area on the data volume, never into the
/// workspace copy. The CLI is pointed at them with <c>CLAUDE_CONFIG_DIR</c> (already on the environment allowlist,
/// <see cref="AgentRuntimes.AgentProcessEnvironment"/>), so the copy the agent works in is untouched and git truth
/// stays host-side.
/// </para>
/// <para>
/// Per-kind targets under the config home (the Claude Code user-scope layout):
/// <list type="bullet">
///   <item><b>Skill</b> → <c>skills/&lt;name&gt;/SKILL.md</c> (frontmatter + body; invocable).</item>
///   <item><b>Agent</b> (subagent) → <c>agents/&lt;name&gt;.md</c> (frontmatter + system prompt; delegable).</item>
///   <item><b>Command</b> → <c>commands/&lt;name&gt;.md</c> (invoked as <c>/name</c>).</item>
///   <item><b>Hook</b> → merged into <c>settings.json</c> under <c>hooks</c> (additive to the approval hook,
///     which lives in the separate <c>--settings</c> file).</item>
///   <item><b>McpServer</b> → <c>projected-not-active/mcp/&lt;name&gt;.json</c>, DEFERRED: the studio's graph MCP
///     server (ADR 0019) is not built, and auto-launching a user's arbitrary stdio/http server inside a contained
///     agent is a separate safety decision. Projected for visibility, not wired with <c>--mcp-config</c>.</item>
///   <item><b>Plugin</b> → <c>projected-not-active/plugins/&lt;name&gt;/plugin.json</c>, DEFERRED: the manifest's
///     component paths are metadata-only (<see cref="PluginComponents"/>), and the CLI's plugin loader needs a
///     marketplace/installed-plugin layout this projection does not synthesize. Projected for visibility only.</item>
/// </list>
/// The two deferred kinds go under <c>projected-not-active/</c> precisely so the CLI never scans them as live
/// config; they are recorded on <see cref="SessionFrameworkProjection.Deferred"/> rather than silently dropped.
/// </para>
/// </summary>
public sealed class SessionFrameworkProjector(
    string dataDirectory,
    IFrameworkArtifactStore store,
    ILogger<SessionFrameworkProjector>? logger = null,
    Func<string?>? sourceConfigHome = null,
    Func<bool>? seedSignInFile = null)
{
    /// <summary>The config home's folder name inside a session's folder on the data volume.</summary>
    public const string ConfigHomeName = "claude";

    /// <summary>
    /// Where the agent CLI keeps its conversations inside a config home (<c>projects/&lt;cwd&gt;/&lt;id&gt;.jsonl</c>).
    /// It is the CLI's state, not a projection, so neither a re-projection nor a session ending removes it:
    /// <c>--resume</c> looks for the conversation there and nowhere else (owner report 2026-09-15).
    /// </summary>
    public const string ConversationStoreName = "projects";

    /// <summary>The conversation store's path relative to a session's folder.</summary>
    public static string ConversationStorePath { get; } = Path.Combine(ConfigHomeName, ConversationStoreName);

    private readonly Func<string?> _sourceConfigHome = sourceConfigHome ?? AgentConversationFiles.CliDefaultConfigHome;

    /// <summary>
    /// Backlog 183: false once the runtime is signed in with a long-lived token the studio injects into every child.
    /// Then no conversation is handed a copy of the CLI's own refreshable sign-in, which is what went stale.
    /// </summary>
    private readonly Func<bool> _seedSignInFile = seedSignInFile ?? (() => true);

    private const string DeferredReasonMcp =
        "MCP servers are projected for visibility only: the studio's graph MCP server (ADR 0019) is not built, "
        + "and auto-launching an arbitrary server inside a contained agent is a separate safety decision. This "
        + "config is not passed to the CLI.";

    private const string DeferredReasonPlugin =
        "Plugins are projected for visibility only: the manifest's component paths are metadata-only and the CLI's "
        + "plugin loader needs an installed-plugin/marketplace layout this projection does not synthesize.";

    private static readonly UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly string _root = Path.Combine(dataDirectory, "agent-sessions");

    // Backlog 156: where a workspace's agent MEMORY lives, shared by every session that runs in that workspace. The
    // CLI writes its memory under <config home>/projects/<cwd key>/memory, and a config home is per session here, so
    // without this a note an agent kept in one conversation was gone from the next (measured in the installed
    // product on 2026-09-26: one of twenty-two session homes held a memory folder nothing else could see).
    private readonly string _memoryRoot = Path.Combine(dataDirectory, "agent-memory");

    /// <summary>The folder name the agent CLI keys a working directory's state by: every character that is not a
    /// letter or a digit becomes a hyphen (<c>/workspaces/abc</c> is <c>-workspaces-abc</c>, measured).</summary>
    public static string ProjectKeyFor(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        return string.Create(workingDirectory.Length, workingDirectory, static (span, path) =>
        {
            for (var i = 0; i < path.Length; i++)
            {
                span[i] = char.IsAsciiLetterOrDigit(path[i]) ? path[i] : '-';
            }
        });
    }

    /// <summary>Where the memory of the workspace whose copy is <paramref name="workingDirectory"/> is kept.</summary>
    public string MemoryDirectoryFor(string workingDirectory) =>
        Path.Combine(_memoryRoot, ProjectKeyFor(workingDirectory));

    /// <summary>
    /// Where a session's config home links that shared memory in: <c>&lt;configDirectory&gt;/projects/&lt;key&gt;/memory</c>,
    /// the CLI's own layout for a working directory's state. This is the ONE place that spells out that path (backlog
    /// 202): <see cref="LinkMemory"/> below builds the link here, and the approval broker resolves this exact string
    /// (<c>AgentReadScope</c>) to decide whether a read of a session's own memory may run unattended, so the two can
    /// never drift into naming two different folders.
    /// </summary>
    public static string MemoryLinkPathFor(string configDirectory, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        return Path.Combine(configDirectory, ConversationStoreName, ProjectKeyFor(workingDirectory), "memory");
    }

    /// <summary>
    /// Where the CLI saves a long tool result for the conversation <paramref name="providerSessionId"/> is
    /// running as, in <paramref name="workingDirectory"/>: <c>&lt;configDirectory&gt;/projects/&lt;key&gt;
    /// /&lt;providerSessionId&gt;/tool-results</c>, measured against the real CLI (backlog 338: a builder's own
    /// saved test log reached this exact folder twice in one turn). This is the ONE place that spells out that
    /// path, the same reason <see cref="MemoryLinkPathFor"/> is: <c>AgentReadScope</c> resolves this exact
    /// string to decide whether a read of the agent's own saved output may run unattended, so the two can never
    /// drift into naming two different folders. <paramref name="providerSessionId"/> is never the agent's own
    /// text: it is the CLI's own conversation id, either passed to <c>--resume</c> or confirmed back by the
    /// CLI's own <c>SessionRegistered</c> announcement.
    /// </summary>
    public static string ToolResultsDirectoryFor(string configDirectory, string workingDirectory, string providerSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerSessionId);
        return Path.Combine(
            configDirectory, ConversationStoreName, ProjectKeyFor(workingDirectory), providerSessionId, "tool-results");
    }

    /// <summary>
    /// Makes the session config home's memory folder for <paramref name="workingDirectory"/> a link to the workspace's
    /// shared memory, so what an agent remembers in one conversation is there in the next. A real memory folder the
    /// session home already holds (a session that ran before this existed) is moved into the shared one first,
    /// never deleted. True when the link is in place; false, with the reason logged, when the platform refused a
    /// link, in which case the session keeps its own folder and loses nothing but the sharing.
    /// </summary>
    public bool LinkMemory(string configDirectory, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var shared = MemoryDirectoryFor(workingDirectory);
        var link = MemoryLinkPathFor(configDirectory, workingDirectory);
        var projectDirectory = Path.GetDirectoryName(link)!;
        try
        {
            Directory.CreateDirectory(shared);
            Directory.CreateDirectory(projectDirectory);

            var existing = new DirectoryInfo(link);
            if (existing.Exists && existing.LinkTarget is not null)
            {
                return true;
            }

            // The link is made BESIDE the folder first, so a platform that refuses links refuses before anything of
            // the session's has moved; only then is the session's own folder folded in and replaced.
            var staged = link + ".shared";
            if (Directory.Exists(staged))
            {
                Directory.Delete(staged);
            }

            Directory.CreateSymbolicLink(staged, shared);

            if (existing.Exists)
            {
                // Never lose what a session wrote before the memory was shared: copy it in, the newer of two winning.
                foreach (var file in existing.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(shared, Path.GetRelativePath(link, file.FullName));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (!File.Exists(destination) || File.GetLastWriteTimeUtc(destination) < file.LastWriteTimeUtc)
                    {
                        file.CopyTo(destination, overwrite: true);
                    }
                }

                existing.Delete(recursive: true);
            }

            Directory.Move(staged, link);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "The workspace memory could not be linked into the session config home at {Link}.", link);
            return false;
        }
    }
    private readonly IFrameworkArtifactStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger _logger = logger ?? NullLogger<SessionFrameworkProjector>.Instance;

    /// <summary>Where a session's projected config home lives (its <c>CLAUDE_CONFIG_DIR</c>).</summary>
    public string ConfigDirectoryFor(AgentSessionId sessionId) =>
        Path.Combine(_root, sessionId.ToString(), ConfigHomeName);

    /// <summary>
    /// The config home a session's CLI child must be started with, or null for the CLI's own default. A new
    /// conversation gets the session's config home exactly when something was projected (backlog 69), so an
    /// empty framework leaves the run byte-identical to one with no projector.
    /// <para>
    /// A RESUMED conversation must be started where its conversation file is, because <c>--resume</c> looks
    /// under its config home and nowhere else. So when this session's config home already holds the
    /// conversation, that home is used even if the framework is empty now; and when the framework has
    /// content but the conversation was held by the CLI's default home (it began with nothing projected),
    /// the file is COPIED into this session's home so the skills and the conversation both reach the child.
    /// A copy, never a move: the default home belongs to the CLI outside Fravashi too.
    /// </para>
    /// </summary>
    public string? ConfigHomeFor(AgentSessionId sessionId, SessionFrameworkProjection projection, string? resumeProviderSessionId)
    {
        ArgumentNullException.ThrowIfNull(projection);

        if (string.IsNullOrWhiteSpace(resumeProviderSessionId))
        {
            return projection.HasContent ? projection.ConfigDirectory : null;
        }

        if (AgentConversationFiles.FileNameFor(resumeProviderSessionId) is not { } fileName)
        {
            return projection.HasContent ? projection.ConfigDirectory : null;
        }

        if (AgentConversationFiles.FindConversation(projection.ConfigDirectory, fileName) is not null)
        {
            return projection.ConfigDirectory;
        }

        if (!projection.HasContent)
        {
            return null;
        }

        AdoptConversation(projection.ConfigDirectory, fileName);
        return projection.ConfigDirectory;
    }

    private void AdoptConversation(string configDirectory, string fileName)
    {
        try
        {
            if (_sourceConfigHome() is not { Length: > 0 } source
                || AgentConversationFiles.FindConversation(source, fileName) is not { } found)
            {
                return;
            }

            var relative = Path.GetRelativePath(source, found);
            var destination = Path.Combine(configDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(found, destination, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The registry refuses to offer a resume whose file is in neither home, so this is a copy that failed.
            _logger.LogWarning(ex, "Could not bring a conversation into the session config home to resume it.");
        }
    }

    /// <summary>
    /// Wipes and rebuilds the session's config home from the CURRENT artifact rows. A row that was removed
    /// since the last session is simply not written this time. Returns what landed where. The CLI's
    /// conversation store is the one thing the wipe leaves, because it is not a projection
    /// (<see cref="ConversationStoreName"/>).
    /// </summary>
    public SessionFrameworkProjection Project(AgentSessionId sessionId)
    {
        var configDirectory = ConfigDirectoryFor(sessionId);
        ResetDirectory(configDirectory);

        var written = new List<string>();
        var deferred = new List<DeferredArtifact>();

        ProjectSkills(configDirectory, written);
        ProjectAgents(configDirectory, written);
        ProjectCommands(configDirectory, written);
        ProjectHooks(configDirectory, written);
        ProjectDeferred(configDirectory, deferred);

        // Best-effort: subscription login stores its token under the config home, so relocating CLAUDE_CONFIG_DIR
        // would hide it. Copy ONLY the credential file (never the history-bearing .claude.json, which would leak
        // host project paths into a contained agent) so an env-authed run and a token-authed run both work.
        SeedCredentials(configDirectory);

        return new SessionFrameworkProjection(configDirectory, written, deferred);
    }

    private void ProjectSkills(string configDirectory, List<string> written)
    {
        foreach (var artifact in _store.List(FrameworkArtifactKind.Skill))
        {
            Project(artifact, () => SkillDocument.ToSkillMarkdown(FrameworkPayload<SkillDefinition>.From(artifact)),
                Path.Combine(configDirectory, "skills", Segment(artifact.Name), "SKILL.md"), written);
        }
    }

    private void ProjectAgents(string configDirectory, List<string> written)
    {
        foreach (var artifact in _store.List(FrameworkArtifactKind.Agent))
        {
            Project(artifact, () => AgentDocument.ToAgentMarkdown(FrameworkPayload<AgentDefinition>.From(artifact)),
                Path.Combine(configDirectory, "agents", Segment(artifact.Name) + ".md"), written);
        }
    }

    private void ProjectCommands(string configDirectory, List<string> written)
    {
        foreach (var artifact in _store.List(FrameworkArtifactKind.Command))
        {
            Project(artifact, () => CommandDocument.ToCommandMarkdown(FrameworkPayload<CommandDefinition>.From(artifact)),
                Path.Combine(configDirectory, "commands", Segment(artifact.Name) + ".md"), written);
        }
    }

    /// <summary>
    /// Merges every stored hook into one <c>settings.json</c> under the config home, grouped by event. The CLI
    /// merges hooks across settings scopes, so these are additive to the fail-closed approval hook the
    /// <see cref="SessionHookWriter"/> writes into the separate <c>--settings</c> file: that one is never touched.
    /// </summary>
    private void ProjectHooks(string configDirectory, List<string> written)
    {
        var byEvent = new Dictionary<HookEvent, JsonArray>();

        foreach (var artifact in _store.List(FrameworkArtifactKind.Hook))
        {
            HookDefinition hook;
            try
            {
                hook = FrameworkPayload<HookDefinition>.From(artifact);
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                _logger.LogWarning(ex, "Skipping hook artifact {Name}: its payload did not parse.", artifact.Name);
                continue;
            }

            var handler = new JsonObject { ["type"] = "command", ["command"] = hook.Command };
            if (hook.TimeoutSeconds is { } timeout)
            {
                handler["timeout"] = timeout;
            }

            var group = new JsonObject();
            if (!string.IsNullOrWhiteSpace(hook.Matcher))
            {
                group["matcher"] = hook.Matcher;
            }

            group["hooks"] = new JsonArray(handler);

            if (!byEvent.TryGetValue(hook.Event, out var groups))
            {
                groups = [];
                byEvent[hook.Event] = groups;
            }

            groups.Add(group);
        }

        if (byEvent.Count == 0)
        {
            return;
        }

        var hooks = new JsonObject();
        foreach (var (evt, groups) in byEvent)
        {
            hooks[evt.ToString()] = groups;
        }

        var settingsPath = Path.Combine(configDirectory, "settings.json");
        WriteFile(settingsPath, new JsonObject { ["hooks"] = hooks }.ToJsonString(Indented));
        written.Add(settingsPath);
    }

    private void ProjectDeferred(string configDirectory, List<DeferredArtifact> deferred)
    {
        foreach (var artifact in _store.List(FrameworkArtifactKind.McpServer))
        {
            var path = Path.Combine(configDirectory, "projected-not-active", "mcp", Segment(artifact.Name) + ".json");
            if (TryWriteDeferred(artifact, () => McpServerDocument.ToMcpJson(FrameworkPayload<McpServerDefinition>.From(artifact)), path))
            {
                deferred.Add(new DeferredArtifact(artifact.Kind, artifact.Name, path, DeferredReasonMcp));
            }
        }

        foreach (var artifact in _store.List(FrameworkArtifactKind.Plugin))
        {
            var path = Path.Combine(configDirectory, "projected-not-active", "plugins", Segment(artifact.Name), "plugin.json");
            if (TryWriteDeferred(artifact, () => PluginDocument.ToPluginJson(FrameworkPayload<PluginDefinition>.From(artifact)), path))
            {
                deferred.Add(new DeferredArtifact(artifact.Kind, artifact.Name, path, DeferredReasonPlugin));
            }
        }

        WriteDeferredReadme(configDirectory, deferred);
    }

    private void WriteDeferredReadme(string configDirectory, List<DeferredArtifact> deferred)
    {
        if (deferred.Count == 0)
        {
            return;
        }

        var lines = new List<string>
        {
            "# Projected, not active",
            string.Empty,
            $"These artifacts are written here so you can see and diff exactly what {ProductName.Short} stored, but they do",
            "NOT change agent behaviour this version. Nothing under this folder is passed to or scanned by the CLI.",
            string.Empty,
        };
        lines.AddRange(deferred.Select(d => $"- {d.Kind} `{d.Name}`: {d.Reason}"));

        var readme = Path.Combine(configDirectory, "projected-not-active", "README.md");
        WriteFile(readme, string.Join('\n', lines) + "\n");
    }

    private void Project(FrameworkArtifact artifact, Func<string> render, string path, List<string> written)
    {
        string content;
        try
        {
            content = render();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            _logger.LogWarning(ex, "Skipping {Kind} artifact {Name}: its payload did not parse.", artifact.Kind, artifact.Name);
            return;
        }

        WriteFile(path, content);
        written.Add(path);
    }

    private bool TryWriteDeferred(FrameworkArtifact artifact, Func<string> render, string path)
    {
        try
        {
            WriteFile(path, render());
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            _logger.LogWarning(ex, "Skipping {Kind} artifact {Name}: its payload did not parse.", artifact.Kind, artifact.Name);
            return false;
        }
    }

    private void SeedCredentials(string configDirectory)
    {
        if (!_seedSignInFile())
        {
            return;
        }

        try
        {
            var source = _sourceConfigHome();
            if (source is null)
            {
                return;
            }

            var credentials = Path.Combine(source, ".credentials.json");
            if (!File.Exists(credentials))
            {
                return;
            }

            var destination = Path.Combine(configDirectory, ".credentials.json");
            File.Copy(credentials, destination, overwrite: true);
            Protect(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An env-authed run needs no seed, so failing to copy a token is never fatal to starting a session.
            _logger.LogDebug(ex, "Could not seed the agent CLI credential into the session config home.");
        }
    }

    private static void ResetDirectory(string configDirectory)
    {
        if (Directory.Exists(configDirectory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(configDirectory))
            {
                if (string.Equals(Path.GetFileName(entry), ConversationStoreName, StringComparison.Ordinal)
                    && Directory.Exists(entry))
                {
                    continue;
                }

                if (Directory.Exists(entry) && new DirectoryInfo(entry).LinkTarget is null)
                {
                    Directory.Delete(entry, recursive: true);
                }
                else if (Directory.Exists(entry))
                {
                    Directory.Delete(entry);
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }

        Directory.CreateDirectory(configDirectory);
        Protect(configDirectory);
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // LF, always: a SKILL.md or a hook command copied out of the container should not carry CRLF, and the
        // document serializers already normalise to LF, so this only guards the join.
        File.WriteAllText(path, content.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>
    /// A single safe path segment from an artifact name. Names are kebab-validated on the way in, but a row is
    /// data, so a value carrying a separator or a <c>..</c> is refused rather than allowed to escape the config
    /// home. It falls back to a stable hash so two odd names never collide onto one file.
    /// </summary>
    private static string Segment(string name)
    {
        if (!string.IsNullOrWhiteSpace(name)
            && name.IndexOfAny(['/', '\\']) < 0
            && name != "."
            && name != ".."
            && !name.Contains("..", StringComparison.Ordinal))
        {
            return name;
        }

        return "artifact-" + Math.Abs(string.GetHashCode(name, StringComparison.Ordinal))
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Protect(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, OwnerOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort: a mount that refuses chmod must not stop a session from starting.
        }
    }
}
