using System.Reflection;
using Fravashi.Application.Git;

namespace Fravashi.Web.Tests.Setup;

/// <summary>
/// Git for the Source step without a repository on disk (item #373): a work tree on <see cref="Branch"/> with the given
/// remotes, or no work tree at all. Built on DispatchProxy so only the four calls the step makes need an answer; any
/// other call is a test bug and throws.
/// </summary>
internal sealed class SetupFakeGit : IWorkspaceGit
{
    public bool IsRepository { get; init; } = true;

    public string Branch { get; init; } = "main";

    public IReadOnlyList<GitRemote> Remotes { get; init; } = [new GitRemote("origin", "https://github.com/acme/shop.git")];

    public WorkspaceGitServices? For(string workspaceId)
    {
        var git = Answering<IGitService>(name => name switch
        {
            "get_IsAvailable" => IsRepository,
            "get_UnavailableReason" => null,
            "GetCurrentBranchAsync" => Task.FromResult(Branch),
            _ => throw new InvalidOperationException($"The setup asked git for {name}."),
        });
        var remote = Answering<IGitRemoteService>(name => name switch
        {
            "GetRemotesAsync" => Task.FromResult(Remotes),
            _ => throw new InvalidOperationException($"The setup asked git for {name}."),
        });
        return new WorkspaceGitServices(workspaceId, Answering<IGitCommandRunner>(_ => null), git, Answering<IGitWorkingTreeService>(_ => null), remote);
    }

    private static T Answering<T>(Func<string, object?> answer)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, Answerer>();
        ((Answerer)(object)proxy).Answer = answer;
        return proxy;
    }

    public class Answerer : DispatchProxy
    {
        public Func<string, object?> Answer { get; set; } = _ => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Answer(targetMethod?.Name ?? string.Empty);
    }
}
