using Fravashi.Application.Git;
using Fravashi.Domain.Releases;

namespace Fravashi.Application.Releases;

/// <summary>What creating or pushing a release tag did.</summary>
/// <param name="Done">True when it happened.</param>
/// <param name="Message">What happened, or why not, in words a person reads.</param>
/// <param name="Sha">The commit the tag points at, when known.</param>
public sealed record ReleaseTagOutcome(bool Done, string Message, string? Sha = null);

/// <summary>
/// Creates and pushes a release's tag (work item #368), each as its OWN act the page calls only after a person
/// confirmed it. Creating refuses a name that is not a release version and a name that already exists, so an existing tag
/// is never moved, replaced or deleted; pushing sends that single tag ref and nothing else. Both go through the workspace's
/// git seams, which on the installed studio is the host bridge with the host's identity and credentials.
/// </summary>
public sealed class ReleaseTagger(IGitService git, IGitRemoteService remote)
{
    /// <summary>
    /// Creates the annotated tag <paramref name="version"/> on <paramref name="commit"/>, with the release's title as its
    /// message. Refuses before git runs when the version is not in the release shape or a tag of that name exists.
    /// </summary>
    public async Task<ReleaseTagOutcome> CreateAsync(string version, string commit, CancellationToken ct = default)
    {
        if (ReleaseVersion.Parse(version) is null)
        {
            return new ReleaseTagOutcome(false, ReleaseVersion.Rule);
        }

        if (string.IsNullOrWhiteSpace(commit))
        {
            return new ReleaseTagOutcome(false, "Choose the commit the tag goes on.");
        }

        var tags = await git.GetTagsAsync(ct).ConfigureAwait(false);
        if (tags.Any(t => string.Equals(t.Name, version, StringComparison.Ordinal)))
        {
            return new ReleaseTagOutcome(
                false,
                $"A tag named {version} already exists. A release never moves or replaces a tag: choose \"The tag already exists\" to use it.");
        }

        try
        {
            await git.CreateTagAsync(version, commit, Release.TitleFor(version), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return new ReleaseTagOutcome(false, "The tag was not created: " + ex.Message);
        }

        return new ReleaseTagOutcome(true, $"Created the tag {version} on {Short(commit)}. It is not pushed yet.", commit);
    }

    /// <summary>Pushes the single tag ref <c>refs/tags/VERSION</c> to origin. Refuses a tag that does not exist locally.</summary>
    public async Task<ReleaseTagOutcome> PushAsync(string version, CancellationToken ct = default)
    {
        if (ReleaseVersion.Parse(version) is null)
        {
            return new ReleaseTagOutcome(false, ReleaseVersion.Rule);
        }

        var tags = await git.GetTagsAsync(ct).ConfigureAwait(false);
        if (tags.FirstOrDefault(t => string.Equals(t.Name, version, StringComparison.Ordinal)) is not { } tag)
        {
            return new ReleaseTagOutcome(false, $"There is no tag named {version} here to push.");
        }

        var pushed = await remote.PushTagAsync(version, ct).ConfigureAwait(false);
        if (pushed.Succeeded)
        {
            return new ReleaseTagOutcome(true, $"Pushed the tag {version} to origin.", tag.Sha);
        }

        var said = pushed.Error ?? "the push failed";
        return new ReleaseTagOutcome(
            false,
            pushed.Failure switch
            {
                GitSyncFailure.Network => $"The tag was not pushed: the remote could not be reached ({said}). Trying again is safe.",
                GitSyncFailure.OutcomeUnknown => $"The push's answer was lost ({said}); check the forge before pushing again.",
                _ => $"The tag was not pushed: {said}",
            },
            tag.Sha);
    }

    private static string Short(string commit) => commit.Length > 10 ? commit[..10] : commit;
}
