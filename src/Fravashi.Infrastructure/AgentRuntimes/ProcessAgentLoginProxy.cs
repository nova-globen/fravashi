using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Persistence;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>
/// Starts a proxied interactive login (S13/S14) by running the CLI's own sign-in command. In a Linux container
/// the command is wrapped in <c>script -qec "&lt;cmd&gt;" /dev/null</c> to give it the pseudo-TTY these CLIs
/// demand; elsewhere it runs directly. A per-tool settings override
/// (<c>AgentRuntimes:LoginCommandOverride:{tool}</c>) lets a scripted fake CLI stand in for a real vendor login
/// during verification - the same real proxy path, a fake command.
/// </summary>
public sealed class ProcessAgentLoginProxy : IAgentLoginProxy
{
    /// <summary>The settings-key prefix for a per-tool login-command override.</summary>
    public const string LoginOverridePrefix = "AgentRuntimes:LoginCommandOverride:";

    private const string ScriptPath = "/usr/bin/script";

    private readonly IAgentLoginPipeFactory _pipes;
    private readonly IConfigurationStore _config;
    private readonly IAgentRuntimeCredentialStore? _credentials;

    /// <param name="credentials">Where a sign-in's printed long-lived token is kept (backlog 183); null keeps none.</param>
    public ProcessAgentLoginProxy(IAgentLoginPipeFactory pipes, IConfigurationStore config, IAgentRuntimeCredentialStore? credentials = null)
    {
        _pipes = pipes;
        _config = config;
        _credentials = credentials;
    }

    public IAgentLoginSession Start(AgentTool tool)
    {
        var descriptor = AgentRuntimeCatalog.For(tool);
        var inner = _config.Get(LoginOverridePrefix + tool)
            ?? $"{descriptor.Command} {string.Join(' ', descriptor.InteractiveLoginArguments)}";
        inner = inner.Trim();

        var (fileName, arguments) = BuildCommand(inner, UsePseudoTty());
        var pipe = _pipes.Create(fileName, arguments);
        return new AgentLoginSession(tool, pipe, descriptor.SubscriptionTokenEnvVar is null ? null : KeepToken(tool));
    }

    private Func<string, string?> KeepToken(AgentTool tool) => token =>
    {
        if (_credentials is null)
        {
            return "this studio has nowhere to keep it.";
        }

        try
        {
            _credentials.SaveSubscriptionToken(tool, token);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    };

    /// <summary>
    /// How the sign-in command is run. Under a pseudo-terminal it is also given 1000 columns: at the 80 a bare terminal
    /// has, the CLI wrapped its sign-in link (and would wrap the token it prints) over several lines, so the link the
    /// panel made clickable was cut off (seen 2026-10-02).
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Arguments) BuildCommand(string inner, bool pseudoTty)
    {
        if (pseudoTty)
        {
            // `script` forwards stdin to the child's PTY and streams its combined output on stdout.
            return ("script", ["-qec", $"stty cols 1000 rows 50 2>/dev/null; {inner}", "/dev/null"]);
        }

        var tokens = inner.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0
            ? (inner, Array.Empty<string>())
            : (tokens[0], tokens[1..]);
    }

    private static bool UsePseudoTty() => OperatingSystem.IsLinux() && File.Exists(ScriptPath);
}
