namespace Fravashi.Application.Persistence;

/// <summary>
/// A git hosting server the user stored an API credential for (plan 2c, decision 8). One entry
/// serves every workspace whose origin remote points at that host.
/// </summary>
/// <param name="Host">Normalized: lowercase host name, no scheme, no port, no path.</param>
/// <param name="Provider">An <c>OnboardingProviders</c> name: "GitHub", "AzureDevOps", or "Forgejo".</param>
/// <param name="CredentialRef">Secret-store reference, by convention <c>githost:{host}:token</c>.</param>
public sealed record GitHostRecord(string Host, string Provider, string CredentialRef, DateTimeOffset UpdatedUtc);

/// <summary>The per-host credential registry backing the PR panel and remote-git auth.</summary>
public interface IGitHostStore
{
    IReadOnlyList<GitHostRecord> GetAll();

    /// <summary>Case-insensitive lookup by host name; null when the host has no entry.</summary>
    GitHostRecord? Get(string host);

    void Upsert(GitHostRecord record);

    void Remove(string host);
}
