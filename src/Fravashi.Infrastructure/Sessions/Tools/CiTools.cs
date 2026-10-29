using System.Globalization;
using System.Text.Json.Nodes;
using Fravashi.Application.Git;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Infrastructure.Ci;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// The CI half of an agent's release act (backlog 153): what the git host ran for the workspace's commits, and
/// what a run said. The repository is the workspace's origin remote and the token is the one Settings >
/// Environment > Git hosts holds for that host; both stay in the studio, and the agent gets the answer.
/// </summary>
public abstract class CiToolBase(SessionGit git, ForgeRuns forge) : GitToolBase(git)
{
    /// <summary>The reader, for the concrete tools.</summary>
    protected ForgeRuns Forge { get; } = forge ?? throw new ArgumentNullException(nameof(forge));

    /// <summary>The repository behind the workspace's origin, or a refusal that says what is missing.</summary>
    protected async Task<(ForgeRepository? Repository, StudioToolResult? Refusal)> RepositoryAsync(
        WorkspaceGitServices git, CancellationToken cancellationToken)
    {
        var origin = await git.Git.GetRemoteUrlAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(origin))
        {
            return (null, Rejected("no_remote", "origin", "This repository has no origin remote, so there is no git host to ask about runs."));
        }

        if (Forge.Repository(origin) is not { } repository)
        {
            return (null, Rejected(
                "no_provider",
                "origin",
                "The origin's host is not a git host the studio knows. Add it, with a token, under Settings > Connections > Git hosts."));
        }

        return (repository, null);
    }

    /// <summary>
    /// The network, not the forge (backlog 233): said in words with slice A's kind, and safe to repeat because reading runs
    /// and logs changes nothing. A status the forge answered with is never this; it stays <c>provider_failed</c>.
    /// </summary>
    protected static StudioToolResult Unreachable(ForgeUnreachableException failure, ForgeRepository repository, string field)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(repository);
        return NetworkToolAnswer.Failure(
            failure.Failure,
            failure.Host,
            field,
            NetworkToolAnswer.ReadFailed(failure.Host, failure.Failure, repository.ProviderName),
            retrySafe: true);
    }

    /// <summary>One run as the agent sees it.</summary>
    protected static JsonObject Describe(ForgeRun run) => new()
    {
        ["id"] = run.Id,
        ["number"] = run.Number,
        ["workflow"] = run.Workflow,
        ["job"] = run.Job,
        ["status"] = run.Status,
        ["sha"] = run.HeadSha,
        ["branch"] = run.Branch,
        ["title"] = run.Title,
        ["url"] = run.Url,
        ["started"] = run.StartedUtc?.ToString("O", CultureInfo.InvariantCulture),
        ["updated"] = run.UpdatedUtc?.ToString("O", CultureInfo.InvariantCulture),
    };
}

/// <summary>The newest CI runs for the workspace's repository, optionally for one commit or one workflow.</summary>
public sealed class CiRunsTool(SessionGit git, ForgeRuns forge) : CiToolBase(git, forge)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "ci_runs";

    /// <summary>How many runs an answer carries when the caller does not say.</summary>
    public const int DefaultMax = 10;

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "The newest CI runs the git host ran for this repository (GitHub Actions, Azure Pipelines or Forgejo Actions), "
        + "newest first: at most `max` (default 10, up to 50), only for commit `sha` (a sha, or a reference such as HEAD) "
        + "when given, and only for a `workflow` whose name contains the text when given. On Forgejo each row is one JOB "
        + "of a run. Use the row's `id` with ci_run_log. When the git host cannot be reached the answer is "
        + "network_unreachable with retry_safe true; an error the host answered with is provider_failed.";

    /// <inheritdoc />
    public override string InputSchemaJson =>
        """
        {"type":"object","properties":{
          "max":{"type":"integer","description":"How many runs at most. Default 10, up to 50."},
          "sha":{"type":"string","description":"Only runs for this commit: a full or short sha, or a reference such as HEAD or main."},
          "workflow":{"type":"string","description":"Only runs whose workflow or pipeline name contains this text."}
        },"additionalProperties":false}
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.ReadOnly;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, WorkspaceGitServices git, CancellationToken cancellationToken)
    {
        var (repository, refusal) = await RepositoryAsync(git, cancellationToken).ConfigureAwait(false);
        if (repository is null)
        {
            return refusal!;
        }

        var sha = Reference(call, "sha", out refusal);
        if (refusal is not null)
        {
            return refusal;
        }

        if (sha is not null && !IsHex(sha))
        {
            // A reference (HEAD, main, a tag) becomes the commit it names, asked of the host repository.
            var resolved = await git.Runner.RunAsync(["rev-parse", "--verify", sha + "^{commit}"], null, cancellationToken)
                .ConfigureAwait(false);
            if (resolved.ExitCode != 0)
            {
                return GitSaid("unknown_reference", "sha", resolved);
            }

            sha = resolved.StdOut.Trim();
        }

        var workflow = Text(call, "workflow");
        var max = Math.Clamp(Number(call, "max") ?? DefaultMax, 1, 50);

        IReadOnlyList<ForgeRun> runs;
        try
        {
            runs = await Forge.ListAsync(repository, workflow is null ? max : 50, sha, cancellationToken).ConfigureAwait(false);
        }
        catch (ForgeUnreachableException ex)
        {
            return Unreachable(ex, repository, "origin");
        }
        catch (HttpRequestException ex)
        {
            return Rejected("provider_failed", "origin", ex.Message);
        }

        if (workflow is not null)
        {
            runs = runs.Where(r => r.Workflow.Contains(workflow, StringComparison.OrdinalIgnoreCase)).Take(max).ToList();
        }

        return Retrieved(new JsonObject
        {
            ["provider"] = repository.ProviderName,
            ["repository"] = repository.Subject,
            ["sha"] = sha,
            ["count"] = runs.Count,
            ["runs"] = new JsonArray([.. runs.Select(Describe)]),
        });
    }

    private static bool IsHex(string text) =>
        text.Length >= 7 && text.All(c => char.IsAsciiHexDigit(c));
}

/// <summary>What one CI run said: its log, from the tail by default because that is where a failure is.</summary>
public sealed class CiRunLogTool(SessionGit git, ForgeRuns forge) : CiToolBase(git, forge)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "ci_run_log";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "The log of one CI run, by the `id` ci_runs showed. `job` picks a job within it: its name, or its position "
        + "(GitHub and Forgejo), or a log id (Azure DevOps); absent means the most useful default (the failed job, the "
        + "last one, or job 0). At most 40000 characters, from the end unless `tail` is false. When the git host cannot be "
        + "reached the answer is network_unreachable with retry_safe true.";

    /// <inheritdoc />
    public override string InputSchemaJson =>
        """
        {"type":"object","properties":{
          "run":{"type":"string","description":"The run's id as ci_runs showed it."},
          "job":{"type":"string","description":"A job name or position, or an Azure DevOps log id."},
          "tail":{"type":"boolean","description":"Take the END of a long log (default true) rather than its start."}
        },"required":["run"],"additionalProperties":false}
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.ReadOnly;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, WorkspaceGitServices git, CancellationToken cancellationToken)
    {
        var (repository, refusal) = await RepositoryAsync(git, cancellationToken).ConfigureAwait(false);
        if (repository is null)
        {
            return refusal!;
        }

        var run = Text(call, "run");
        if (run is null || run[0] == '-' || run.Any(char.IsWhiteSpace))
        {
            return Rejected("missing_run", "run", "Say which run, by the id ci_runs showed.");
        }

        var job = Text(call, "job");
        if (job is not null && (job[0] == '-' || job.Contains('/')))
        {
            return Rejected("bad_job", "job", "A job is a name, a position, or a log id.");
        }

        ForgeRunLog? log;
        try
        {
            log = await Forge.LogAsync(repository, run, job, TextCap, Flag(call, "tail") ?? true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ForgeUnreachableException ex)
        {
            return Unreachable(ex, repository, "run");
        }
        catch (HttpRequestException ex)
        {
            return Rejected("provider_failed", "run", ex.Message);
        }

        if (log is null)
        {
            return Rejected(
                "no_log",
                "run",
                $"{repository.ProviderName} has no log to give for run {run}"
                + (job is null ? string.Empty : $" job {job}")
                + ". On Forgejo a log is readable only while the repository is public or the token is accepted by its web pages; check the job position with ci_runs.");
        }

        return Retrieved(new JsonObject
        {
            ["provider"] = repository.ProviderName,
            ["run"] = run,
            ["job"] = job,
            ["source"] = log.Source,
            ["truncated"] = log.Truncated,
            ["text"] = log.Text,
        });
    }
}

/// <summary>Creates an annotated tag on the host repository; pushing it is git_push's `tag`.</summary>
public sealed class GitTagTool(SessionGit git) : GitToolBase(git)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "git_tag";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Creates an annotated tag `name` with `message` on the current commit of the workspace repository. It does not "
        + "push: pushing a tag is git_push with `tag`, and a release here means pushing the branch first and the tag after.";

    /// <inheritdoc />
    public override string InputSchemaJson =>
        """
        {"type":"object","properties":{
          "name":{"type":"string","description":"The tag, such as v1.2.3."},
          "message":{"type":"string","description":"The annotation."}
        },"required":["name","message"],"additionalProperties":false}
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, WorkspaceGitServices git, CancellationToken cancellationToken)
    {
        var name = TagName(call, "name", out var refused);
        if (refused is not null)
        {
            return refused;
        }

        if (name is null)
        {
            return Rejected("missing_name", "name", "Say what the tag is called.");
        }

        var message = Text(call, "message");
        if (message is null)
        {
            return Rejected("missing_message", "message", "An annotated tag needs a message.");
        }

        var created = await git.Runner.RunAsync(["tag", "-a", name, "-m", message], null, cancellationToken).ConfigureAwait(false);
        if (created.ExitCode != 0)
        {
            return GitSaid("tag_failed", "name", created);
        }

        var sha = await git.Runner.RunAsync(["rev-parse", "--verify", name + "^{commit}"], null, cancellationToken).ConfigureAwait(false);
        return Changed("tagged", new JsonObject
        {
            ["tag"] = name,
            ["sha"] = sha.ExitCode == 0 ? sha.StdOut.Trim() : null,
        });
    }
}
