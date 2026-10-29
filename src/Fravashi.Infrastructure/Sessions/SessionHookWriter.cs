using System.Text.Json;
using System.Text.Json.Nodes;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>Where a session's hook files live on the data volume.</summary>
/// <param name="ToolServerConfigPath">
/// The MCP config that points the CLI at the studio's tool server (backlog 84), or null when this writer was
/// built without a tool-server URL.
/// </param>
public sealed record SessionHookFiles(
    string Directory,
    string SettingsPath,
    string ScriptPath,
    string? ToolServerConfigPath = null);

/// <summary>
/// Writes the per-session settings file and approval script the CLI is started with (plan 2e Task 3).
/// The script is what the CLI runs before every tool call: it POSTs the pending call to the studio,
/// BLOCKS until the user answers, and prints the studio's decision. If anything at all goes wrong
/// (studio down, network refused, empty answer) it prints DENY, so a broken approval channel can only
/// ever stop the agent, never wave it through.
/// <para>
/// Shapes verified against claude 2.1.221 in the container on 2026-08-04: a "*" matcher fires for
/// every tool (Read included), and a per-hook <c>timeout</c> in seconds is honored well past the 60s
/// default (a 90s stall completed under timeout 600). The matcher and the decision document were driven
/// again against the pinned 2.1.222 on 2026-08-05
/// (<c>ClaudeCliContractTests.A_real_tool_call_is_gated_by_the_pre_tool_use_hook</c>: a real Bash call was
/// stopped by this script's fail-closed refusal, and the CLI handed its reason back as the tool result).
/// The long-<c>timeout</c> observation is the one part of the paragraph above that has not been repeated
/// since 2.1.221.
/// </para>
/// </summary>
/// <param name="dataDirectory">The data volume; session folders go under <c>agent-sessions/</c>.</param>
/// <param name="approvalUrl">Where the hook script posts pending tool calls.</param>
/// <param name="toolServerUrl">
/// Where the studio's tool server answers (<see cref="Tools.StudioToolServer.Route"/>). When given, every session also
/// gets an MCP config naming that server with the session's secret, which the runner passes as <c>--mcp-config</c>.
/// </param>
public sealed class SessionHookWriter(string dataDirectory, string approvalUrl, string? toolServerUrl = null)
{
    /// <summary>
    /// How long the CLI lets the hook block, in seconds. Deliberately ABOVE the broker's own timeout
    /// so the studio is what decides an unanswered call, not the CLI killing the hook underneath it.
    /// </summary>
    public static int HookTimeoutSeconds => (int)AgentApprovalBroker.DefaultTimeout.TotalSeconds + 600;

    private static readonly UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string _root = Path.Combine(dataDirectory, "agent-sessions");
    private readonly string _approvalUrl = approvalUrl;
    private readonly string? _toolServerUrl = string.IsNullOrWhiteSpace(toolServerUrl) ? null : toolServerUrl.Trim();

    /// <summary>Writes (or rewrites) a session's hook files and returns where they landed.</summary>
    public SessionHookFiles Write(AgentSessionId sessionId, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var directory = Path.Combine(_root, sessionId.ToString());
        Directory.CreateDirectory(directory);
        Protect(directory);

        var files = new SessionHookFiles(
            directory,
            Path.Combine(directory, "settings.json"),
            Path.Combine(directory, "approve.sh"),
            _toolServerUrl is null ? null : Path.Combine(directory, "tools.mcp.json"));

        File.WriteAllText(files.ScriptPath, Script(secret));
        Protect(files.ScriptPath);
        File.WriteAllText(files.SettingsPath, Settings(files.ScriptPath));

        if (files.ToolServerConfigPath is { } toolConfig)
        {
            // It carries the session secret, exactly like the script, so it gets the same owner-only mode.
            File.WriteAllText(toolConfig, ToolServerConfig(_toolServerUrl!, secret));
            Protect(toolConfig);
        }

        return files;
    }

    /// <summary>
    /// Tears a session's folder down when its process ends, taking its secret off disk, EXCEPT the agent
    /// CLI's own conversation store (<see cref="SessionFrameworkProjector.ConversationStorePath"/>). Absent
    /// is fine, and a folder left holding nothing else is removed entirely.
    /// <para>
    /// This used to delete the whole folder, which was right while the folder held only the hook files.
    /// Since backlog 69 the CLI's config home lives in it too (<c>claude/</c>, its <c>CLAUDE_CONFIG_DIR</c>), and
    /// the CLI keeps the conversation it would <c>--resume</c> under <c>claude/projects</c>. Deleting it made
    /// every ended conversation impossible to resume (owner report 2026-09-15: "No conversation found with
    /// session ID"). The conversation file stays; the CLI writing its own transcript onto the persistent
    /// volume is a known issue tracked separately (backlog 116), and until that lands resuming needs it.
    /// Everything else goes as before: the approval script and settings (the secret), the projected
    /// framework files and the seeded credential copy, which the next start re-projects anyway.
    /// </para>
    /// </summary>
    public void Remove(AgentSessionId sessionId)
    {
        var directory = Path.Combine(_root, sessionId.ToString());
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var keep = Path.GetFullPath(Path.Combine(directory, SessionFrameworkProjector.ConversationStorePath));
            RemoveAllBut(directory, keep);

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A lingering folder is not worth failing a session teardown over.
        }
    }

    /// <summary>
    /// Deletes everything under <paramref name="directory"/> that is not <paramref name="keep"/> or on the
    /// way to it. A link is deleted as a link, never followed: a folder inside the session area is not a
    /// reason to delete what it points at.
    /// </summary>
    private static void RemoveAllBut(string directory, string keep)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var full = Path.GetFullPath(entry);
            var info = new FileInfo(full);
            var isRealDirectory = Directory.Exists(full) && info.LinkTarget is null;

            if (string.Equals(full, keep, StringComparison.Ordinal))
            {
                continue;
            }

            if (isRealDirectory && keep.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                RemoveAllBut(full, keep);
                if (!Directory.EnumerateFileSystemEntries(full).Any())
                {
                    Directory.Delete(full);
                }

                continue;
            }

            if (isRealDirectory)
            {
                Directory.Delete(full, recursive: true);
            }
            else if (Directory.Exists(full))
            {
                // A directory link: removing it removes the link, not its target.
                Directory.Delete(full);
            }
            else
            {
                File.Delete(full);
            }
        }
    }

    private static string Settings(string scriptPath)
    {
        // Built as nodes rather than serialized from an anonymous type: no reflection, so this keeps
        // working under any trimming/AOT publish the app grows later.
        var settings = new JsonObject
        {
            // Backlog 174: what the signed-in ACCOUNT carries is not the agent's to use. Signed in with a person's
            // subscription, the pinned CLI loaded their claude.ai connectors CONNECTED (Gmail, Drive, Calendar) and
            // the plugins and skills synced from claude.ai into every session. From --settings each switch holds for
            // this invocation only and moves nothing on disk (read from the pinned binary's own descriptions).
            ["disableClaudeAiConnectors"] = true,
            ["syncClaudeAiPlugins"] = false,
            ["syncClaudeAiSkills"] = false,

            // Backlog 179: what a commit says about who wrote it is the repository's convention, not the vendor's
            // default. The pinned CLI's own guidance added a model trailer to an agent's commit although this
            // repository forbids one; an empty text hides it ("Empty string hides attribution"), and a project that
            // wants attribution can ask for it in its own instructions.
            ["attribution"] = new JsonObject
            {
                ["commit"] = string.Empty,
                ["pr"] = string.Empty,
            },
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray(
                    new JsonObject
                    {
                        // Catch-all: the read-only allowlist is reviewable C# (decision 5),
                        // not a regex buried in a settings file.
                        ["matcher"] = "*",
                        ["hooks"] = new JsonArray(
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = HookCommand(scriptPath),
                                ["timeout"] = HookTimeoutSeconds,
                            }),
                    }),
            },
        };

        return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// The MCP config naming the studio's tool server (backlog 84): Streamable HTTP, authenticated by the same
    /// per-session secret header the hook script sends. Shape measured against claude 2.1.222 on 2026-09-15: the
    /// CLI reported the server <c>connected</c> and sent the header on every request, including its GET probe.
    /// </summary>
    public static string ToolServerConfig(string url, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [Tools.StudioToolServer.ServerName] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = url,
                    ["headers"] = new JsonObject { [Tools.StudioToolServer.SecretHeader] = secret },
                },
            },
        };

        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
    /// The refusal the hook COMMAND gives when the script ends without answering (backlog 115). No single
    /// quote in it: it is written inside a single-quoted shell word.
    /// </summary>
    public const string CrashedHookRefusal =
        "Fravashi: the approval hook ended without answering, so this tool call is refused.";

    /// <summary>
    /// The command the CLI runs before every tool call: the session's script, and a refusal the SHELL gives
    /// when the script ends without a clean exit (backlog 115).
    /// <para>
    /// The script alone is not enough, and that was measured rather than assumed. Against the pinned 2.1.222
    /// on 2026-09-15 a PreToolUse hook that exits 1 with no output, is killed, times out, or exits 0 with no
    /// decision document is NOT a refusal: the CLI records the hook as failed and hands the call to its
    /// ordinary permission system. Under <c>--permission-mode bypassPermissions</c> every one of those let
    /// the command run. The studio's sessions run in the default mode, where that permission system happens
    /// to refuse the same call in headless mode, which is why the 2026-08-13 incident's "PreToolUse:Bash
    /// failed (exit 1)" stopped anything at all: a second, vendor-owned barrier, not the studio's guarantee.
    /// Exit code 2 IS blocking in both modes (same run), so any non-zero end of the script (a crash, a kill
    /// by signal, a missing script file, a failed write of the answer) becomes exit 2 with a stated reason.
    /// A clean exit 0 still hands the CLI exactly what the script printed.
    /// </para>
    /// <para>
    /// What this cannot cover, and the threat model says so: the CLI killing the hook at its own timeout (the
    /// hook's output is then ignored; the studio's timeouts are ordered so the script answers first), and
    /// the shell that runs this command being killed itself.
    /// </para>
    /// </summary>
    internal static string HookCommand(string scriptPath) =>
        $"sh '{scriptPath}' || {{ echo '{CrashedHookRefusal}' >&2; exit 2; }}";

    /// <summary>
    /// The hook script itself. Written with LF endings and no interpolation of the decision document:
    /// the refusal it prints comes from <see cref="AgentApprovalEndpoint.Render"/>, so the studio and
    /// the fallback speak exactly the same protocol.
    /// </summary>
    private string Script(string secret)
    {
        var deny = AgentApprovalEndpoint.Render(
            ToolApprovalDecision.Deny("The studio could not be reached, so this tool call was refused."));

        string[] lines =
        [
            "#!/bin/sh",
            "# Fravashi session approval hook (plan 2e). The CLI runs this before every tool call and",
            "# waits for what it prints; it asks the studio, which asks the user.",
            "#",
            "# It FAILS CLOSED: every failure path prints a refusal, and the only approval this script",
            "# can ever emit is one the studio itself answered with.",
            "DENY='" + deny + "'",
            string.Empty,
            "RESPONSE=$(curl --silent --show-error --max-time " + (HookTimeoutSeconds - 300) + " \\",
            "  --request POST \\",
            "  --header 'Content-Type: application/json' \\",
            "  --header '" + AgentApprovalEndpoint.SecretHeader + ": " + secret + "' \\",
            "  --data-binary @- \\",
            "  '" + _approvalUrl + "' 2>/dev/null)",
            string.Empty,
            "if [ $? -ne 0 ] || [ -z \"$RESPONSE\" ]; then",
            "  printf '%s\\n' \"$DENY\"",
            "  exit 0",
            "fi",
            string.Empty,
            "# Only a real decision document is forwarded. Anything else (a proxy error page, an",
            "# HTML login redirect, a truncated body) is refused: verified in the container, where a",
            "# 502 page was otherwise passed through to the CLI verbatim.",
            "case \"$RESPONSE\" in",
            "  *'\"hookSpecificOutput\"'*'\"permissionDecision\"'*) printf '%s\\n' \"$RESPONSE\" ;;",
            "  *) printf '%s\\n' \"$DENY\" ;;",
            "esac",
            string.Empty,
        ];

        // LF, always: a CRLF shell script does not run in the container.
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The session secret lives in this folder, so on Linux (the container) it is owner-only. Windows
    /// dev hosts have no equivalent mode and are left to their inherited ACLs.
    /// </summary>
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
