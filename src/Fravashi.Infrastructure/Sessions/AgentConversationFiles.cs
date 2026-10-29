using Fravashi.Domain.AgentSessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// The agent CLI's own conversation files for the studio's sessions: where they are, whether one still exists,
/// and removing them when the conversation is deleted.
/// <para>
/// Decision (2026-09-15, through the coordinator): a conversation's CLI file is KEPT for as long as the
/// conversation exists, because <c>--resume</c> needs it; deleting the conversation removes its whole
/// <c>&lt;data&gt;/agent-sessions/{id}</c> folder, <c>claude/projects</c> included, and, when the conversation
/// lived in the CLI's default config home (it ran with nothing projected), its file there too. The CLI writes
/// that file unredacted; scrubbing it is backlog 116's remaining work (see the agent threat model).
/// </para>
/// </summary>
public sealed class AgentConversationFiles(
    string dataDirectory,
    Func<string?>? defaultConfigHome = null,
    ILogger<AgentConversationFiles>? logger = null)
{
    private readonly string _root = Path.Combine(dataDirectory, "agent-sessions");
    private readonly Func<string?> _defaultConfigHome = defaultConfigHome ?? CliDefaultConfigHome;
    private readonly ILogger _logger = logger ?? NullLogger<AgentConversationFiles>.Instance;

    /// <summary>A session's folder on the data volume: its hook files and its config home.</summary>
    public string SessionFolderFor(AgentSessionId id) => Path.Combine(_root, id.ToString());

    /// <summary>
    /// True when the CLI still has the conversation <paramref name="providerSessionId"/> under this session's
    /// config home or under the CLI's default one, which are the only two places a resume can be started with.
    /// </summary>
    public bool Exists(AgentSessionId id, string providerSessionId)
    {
        if (FileNameFor(providerSessionId) is not { } fileName)
        {
            return false;
        }

        return FindConversation(Path.Combine(SessionFolderFor(id), SessionFrameworkProjector.ConfigHomeName), fileName) is not null
            || (_defaultConfigHome() is { Length: > 0 } home && FindConversation(home, fileName) is not null);
    }

    /// <summary>
    /// Removes everything a deleted conversation left on disk: the session's whole folder, and its file in the
    /// CLI's default config home when there is one. Absent is fine; a failure is logged, never thrown, because
    /// the conversation's rows are already gone by the time this runs.
    /// </summary>
    public void Delete(AgentSessionId id, string? providerSessionId)
    {
        try
        {
            var folder = SessionFolderFor(id);
            if (Directory.Exists(folder))
            {
                // Recursive delete removes a link inside the folder as a link; it never follows one out.
                Directory.Delete(folder, recursive: true);
            }

            if (FileNameFor(providerSessionId) is { } fileName
                && _defaultConfigHome() is { Length: > 0 } home
                && FindConversation(home, fileName) is { } inDefaultHome)
            {
                File.Delete(inDefaultHome);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the agent CLI's files for deleted conversation {SessionId}.", id);
        }
    }

    /// <summary>
    /// The conversation file's name, or null for an id that is not one plain file name. The id is the CLI's,
    /// read back from a row, so a separator or a dot-dot in it must never reach a path.
    /// </summary>
    internal static string? FileNameFor(string? providerSessionId)
    {
        var id = providerSessionId?.Trim();
        return !string.IsNullOrEmpty(id) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? id + ".jsonl"
            : null;
    }

    /// <summary>The conversation file under a config home's store, one project folder deep, or null.</summary>
    internal static string? FindConversation(string configHome, string fileName)
    {
        var store = Path.Combine(configHome, SessionFrameworkProjector.ConversationStoreName);
        if (!Directory.Exists(store))
        {
            return null;
        }

        return Directory.EnumerateDirectories(store)
            .Select(project => Path.Combine(project, fileName))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The config home the studio's own CLI reads when nothing points it elsewhere: an explicit
    /// <c>CLAUDE_CONFIG_DIR</c> on the process wins, otherwise <c>$HOME/.claude</c>. Null when neither is known.
    /// </summary>
    public static string? CliDefaultConfigHome()
    {
        var explicitHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(explicitHome))
        {
            return explicitHome;
        }

        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE");
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".claude");
    }
}
