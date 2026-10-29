using Fravashi.Application;
using Fravashi.Domain;
using Fravashi.Application.Git;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Domain.PullRequests;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.PullRequests;

namespace Fravashi.Web.Services;

/// <summary>An install-wide provider connection, in the words the setup writes it.</summary>
/// <param name="Provider">The provider's name as a person says it: "Jira", "Azure DevOps", "Confluence".</param>
/// <param name="Site">Where the connection points, "acme.atlassian.net", or null when nothing is recorded.</param>
/// <param name="Target">
/// The external project or space the connection was made for, named so nobody mistakes it for the folder being set up:
/// "Jira project CORE", "Azure DevOps project Fravashi", "Confluence space CORE", "the Azure DevOps wiki of Fravashi".
/// </param>
public sealed record SetupConnection(string Provider, string? Site, string Target);

/// <summary>The install's board and wiki connections, either of which may be absent.</summary>
public sealed record SetupConnections(SetupConnection? Board, SetupConnection? Wiki)
{
    /// <summary>
    /// Where a project's board and wiki live, in one line: "Board and wiki here in Fravashi", "Board follows Jira project
    /// CORE; wiki here in Fravashi", and so on.
    /// </summary>
    public string Describe(ProviderChoice board, ProviderChoice wiki)
    {
        var boardOut = board == ProviderChoice.Install && Board is not null;
        var wikiOut = wiki == ProviderChoice.Install && Wiki is not null;
        if (!boardOut && !wikiOut)
        {
            return $"Board and wiki here in {ProductName.Short}";
        }

        var boardText = boardOut ? $"Board follows {Board!.Target}" : $"Board here in {ProductName.Short}";
        var wikiText = wikiOut ? $"wiki publishes to {Wiki!.Target}" : $"wiki here in {ProductName.Short}";
        return $"{boardText}; {wikiText}";
    }

    /// <summary>
    /// Reads the connections onboarding (or Settings, Board and Wiki) recorded. A provider counts as connected only
    /// when one of the external providers is chosen AND its connection row exists.
    /// </summary>
    public static SetupConnections Read(IConfigurationStore settings, IProviderConnectionStore connections)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(connections);
        SetupConnection? board = null;
        var boardProvider = settings.Get("Onboarding:Board:Provider");
        if (boardProvider is OnboardingProviders.Jira or OnboardingProviders.AzureDevOps
            && connections.Get("board") is { } boardRow)
        {
            var name = boardProvider == OnboardingProviders.Jira ? "Jira" : "Azure DevOps";
            var site = Site(boardRow.BaseUrl);
            var key = Clean(settings.Get("Onboarding:Board:Project"));
            board = new SetupConnection(name, site,
                key is not null ? $"{name} project {key}" : site is not null ? $"{name} at {site}" : name);
        }

        SetupConnection? wiki = null;
        var wikiProvider = settings.Get("Onboarding:Wiki:Provider");
        if (wikiProvider is OnboardingProviders.Confluence or OnboardingProviders.AzureDevOpsWiki or OnboardingProviders.GitHubWiki
            && connections.Get("wiki") is { } wikiRow)
        {
            var site = Site(wikiRow.BaseUrl);
            var key = Clean(settings.Get("Onboarding:Draft:Wiki:SpaceKey"));
            var owner = OwnerOf(site);
            var (name, target) = wikiProvider switch
            {
                OnboardingProviders.Confluence => ("Confluence",
                    key is not null ? $"Confluence space {key}" : site is not null ? $"Confluence at {site}" : "Confluence"),
                OnboardingProviders.AzureDevOpsWiki => ("the Azure DevOps wiki",
                    owner is not null ? $"the Azure DevOps wiki of {owner}" : "the Azure DevOps wiki"),
                _ => ("the GitHub wiki", owner is not null ? $"the GitHub wiki of {owner}" : "the GitHub wiki"),
            };
            wiki = new SetupConnection(name, site, target);
        }

        return new SetupConnections(board, wiki);
    }

    /// <summary>
    /// The project (Azure DevOps, the last part of dev.azure.com/org/project) or repository (GitHub, owner/repo) a wiki's
    /// address names, or null when it names none.
    /// </summary>
    private static string? OwnerOf(string? site)
    {
        if (site is null)
        {
            return null;
        }

        var parts = site.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        return parts[0].Contains("github", StringComparison.OrdinalIgnoreCase)
            ? $"{parts[1]}/{parts[2]}"
            : Uri.UnescapeDataString(parts[^1]);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Site(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            ? (uri.Host + uri.AbsolutePath).TrimEnd('/')
            : url.Trim();
    }
}

/// <summary>Where the setup stands on reading the folder's git.</summary>
public enum SetupSourceState
{
    /// <summary>Still reading.</summary>
    Reading,

    /// <summary>A git repository; branch and remotes are known.</summary>
    Repository,

    /// <summary>The folder is not a git repository.</summary>
    NotARepository,

    /// <summary>Git could not be read; <see cref="SetupSource.Error"/> says why.</summary>
    Failed,
}

/// <summary>One remote, with the kind of host it is on and whether pull requests can be shown for it.</summary>
public sealed record SetupRemote(string Name, string Url, string HostKind, string Host, bool PullRequests, bool HasToken);

/// <summary>What the folder's git says, read only.</summary>
public sealed record SetupSource(SetupSourceState State, string? Branch, IReadOnlyList<SetupRemote> Remotes, string? Error)
{
    /// <summary>Not read yet.</summary>
    public static SetupSource Reading { get; } = new(SetupSourceState.Reading, null, [], null);

    /// <summary>The remote a line names: origin, else the first.</summary>
    public SetupRemote? Main => Remotes.FirstOrDefault(r => r.Name == "origin") ?? Remotes.FirstOrDefault();

    /// <summary>"Branch main, origin on GitHub", for the recommended setup.</summary>
    public string Summary(string folder) => State switch
    {
        SetupSourceState.Reading => $"Reading {folder}'s Git settings ...",
        SetupSourceState.NotARepository => "Not a Git repository yet; Git features turn on when it becomes one",
        SetupSourceState.Failed => "Git could not be read here; the Git page will say why",
        _ => Main is { } remote
            ? $"Branch {Branch ?? "(none yet)"}, {remote.Name} on {remote.HostKind}"
            : $"Branch {Branch ?? "(none yet)"}, no remote",
    };

    /// <summary>
    /// Reads the folder's git through the bridge, read only: is it a repository, its branch, its remotes, and what kind
    /// of host each remote is on (a <c>git_hosts</c> entry first, then the URL, as the Git page decides it).
    /// </summary>
    public static async Task<SetupSource> ReadAsync(
        IWorkspaceGit? git, string workspaceId, Func<GitHostAccess>? hosts, CancellationToken ct = default)
    {
        if (git?.For(workspaceId) is not { } services)
        {
            return new SetupSource(SetupSourceState.Failed, null, [], "Git cannot be reached from this window.");
        }

        try
        {
            if (!await Task.Run(() => services.Git.IsAvailable, ct).ConfigureAwait(false))
            {
                return services.Git.UnavailableReason is { Length: > 0 } reason
                    ? new SetupSource(SetupSourceState.Failed, null, [], reason)
                    : new SetupSource(SetupSourceState.NotARepository, null, [], null);
            }

            var branch = await services.Git.GetCurrentBranchAsync(ct).ConfigureAwait(false);
            var remotes = await services.Remote.GetRemotesAsync(ct).ConfigureAwait(false);
            var access = hosts?.Invoke();
            return new SetupSource(
                SetupSourceState.Repository,
                string.IsNullOrWhiteSpace(branch) ? null : branch.Trim(),
                [.. remotes.Select(r => Describe(r, access))],
                null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SetupSource(SetupSourceState.Failed, null, [], ex.Message);
        }
    }

    private static SetupRemote Describe(GitRemote remote, GitHostAccess? access)
    {
        var parsed = RemoteInfo.Parse(remote.FetchUrl);
        var resolved = access?.Resolve(parsed);
        var provider = resolved?.Provider ?? parsed.Provider;
        var host = parsed.Host;
        var kind = provider switch
        {
            PullRequestProvider.GitHub => "GitHub",
            PullRequestProvider.AzureDevOps => "Azure DevOps",
            PullRequestProvider.Forgejo => "Forgejo",
            _ when host.Contains("gitlab", StringComparison.OrdinalIgnoreCase) => "GitLab",
            _ when host.Length > 0 => host,
            _ => "another Git host",
        };
        return new SetupRemote(remote.Name, remote.FetchUrl, kind, host, provider != PullRequestProvider.None, resolved?.Credential is not null);
    }
}
