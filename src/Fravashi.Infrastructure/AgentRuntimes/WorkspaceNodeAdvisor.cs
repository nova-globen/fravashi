using Fravashi.Application.AgentRuntimes;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>What a workspace asked for against what the layout has, in one read.</summary>
/// <param name="Request">The version the workspace asked for and the file that asked; null when nothing did.</param>
/// <param name="ProvisionedVersion">The exact provisioned version that would serve the request, or null when none.</param>
/// <param name="ProvisionedBin">The bin folder that version's PATH prefix is, or null.</param>
public sealed record WorkspaceNodeStatus(
    ProjectNodeRequest? Request,
    string? ProvisionedVersion,
    string? ProvisionedBin)
{
    /// <summary>True when a version was asked for and nothing provisioned answers it: the banner case.</summary>
    public bool IsMissing => Request is not null && ProvisionedBin is null;
}

/// <summary>
/// Backlog 10: answers the banner's two questions for a workspace copy: what did it ask for, and what will
/// the agent actually get. The second half is the image Node when nothing provisioned matches, read ONCE
/// per process by running <c>node --version</c> through the agent command runner (the same restricted
/// environment an agent child gets, so the answer is the one the agent would see), and never read at all
/// while every workspace's request is satisfied.
/// </summary>
public sealed class WorkspaceNodeAdvisor
{
    private readonly NodePathProvisioner _layout;
    private readonly IAgentCommandRunner _runner;
    private readonly Lazy<Task<string?>> _imageVersion;

    public WorkspaceNodeAdvisor(NodePathProvisioner layout, IAgentCommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(runner);
        _layout = layout;
        _runner = runner;
        _imageVersion = new Lazy<Task<string?>>(ReadImageVersionAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Reads the workspace copy's request and the layout's answer to it. Cheap: three file probes and a directory listing.</summary>
    public WorkspaceNodeStatus Inspect(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return new WorkspaceNodeStatus(null, null, null);
        }

        var request = ProjectNodeResolver.ResolveRequest(workingDirectory);
        if (request is null)
        {
            return new WorkspaceNodeStatus(null, null, null);
        }

        return new WorkspaceNodeStatus(
            request,
            _layout.ProvisionedVersionFor(request.Version),
            _layout.PathPrefixFor(request.Version));
    }

    /// <summary>
    /// The Node an agent gets when nothing provisioned matches: whatever <c>node</c> on the agent's PATH
    /// reports, without a leading <c>v</c>. Null when there is no runnable node at all, which the banner
    /// says in those words rather than inventing a number.
    /// </summary>
    public Task<string?> ImageNodeVersionAsync() => _imageVersion.Value;

    private async Task<string?> ReadImageVersionAsync()
    {
        try
        {
            var result = await _runner.RunAsync(new AgentCommand("node", ["--version"], TimeSpan.FromSeconds(15)));
            return result.Success
                ? ProcessAgentRuntimeService.ParseVersion(result.StandardOutput)?.TrimStart('v', 'V')
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
        {
            return null;
        }
    }
}
