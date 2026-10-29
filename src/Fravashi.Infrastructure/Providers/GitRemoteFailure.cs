namespace Fravashi.Infrastructure.Providers;

/// <summary>What a failed git transport call against a remote actually means.</summary>
public enum GitRemoteFailureKind
{
    /// <summary>The remote (or the wiki repository behind it) does not exist, or is invisible to this credential.</summary>
    NotFound,

    /// <summary>The credential was rejected: expired, revoked, or wrong for this host.</summary>
    BadCredential,

    /// <summary>Anything else (network, proxy, a git-side error).</summary>
    Other,
}

/// <summary>
/// Classifies git's stderr for the transport calls we make against a host (backlog 76). Shared by the
/// onboarding GitHub-wiki probe and the GitHub-wiki sync source so both name the same condition instead of
/// showing raw "fatal:" text that reads like a Fravashi failure.
/// </summary>
public static class GitRemoteFailure
{
    /// <summary>
    /// GitHub returns "Repository not found" both for a missing wiki repo AND for a bad token on a private
    /// repo (it conflates the two deliberately, to avoid leaking repository existence), so the unambiguous
    /// credential phrases are tested first.
    /// </summary>
    public static GitRemoteFailureKind Classify(string? stderr)
    {
        var e = (stderr ?? string.Empty).ToLowerInvariant();
        if (e.Contains("authentication failed", StringComparison.Ordinal)
            || e.Contains("invalid username or", StringComparison.Ordinal)
            || e.Contains("could not read username", StringComparison.Ordinal)
            || e.Contains("could not read password", StringComparison.Ordinal)
            || e.Contains("password authentication", StringComparison.Ordinal)
            || e.Contains("http 401", StringComparison.Ordinal)
            || e.Contains("403 forbidden", StringComparison.Ordinal)
            || e.Contains("http 403", StringComparison.Ordinal))
        {
            return GitRemoteFailureKind.BadCredential;
        }

        if (e.Contains("repository not found", StringComparison.Ordinal)
            || e.Contains("not found", StringComparison.Ordinal)
            || e.Contains("http 404", StringComparison.Ordinal)
            || e.Contains("does not appear to be a git repository", StringComparison.Ordinal))
        {
            return GitRemoteFailureKind.NotFound;
        }

        return GitRemoteFailureKind.Other;
    }
}
