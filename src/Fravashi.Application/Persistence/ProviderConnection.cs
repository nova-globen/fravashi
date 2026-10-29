namespace Fravashi.Application.Persistence;

/// <summary>
/// The category of an external provider a connection targets. Product-neutral (naming-conventions D5):
/// these are the three store categories onboarding collects, not vendor or product names.
/// </summary>
public enum ProviderKind
{
    /// <summary>Git source hosting (e.g. GitHub, Azure DevOps).</summary>
    Source,

    /// <summary>Documentation / wiki (e.g. Confluence, Azure DevOps Wiki, GitHub Wiki, or the local in-DB wiki).</summary>
    Wiki,

    /// <summary>Work board / tracker (e.g. Azure DevOps, Jira, or the local in-DB board).</summary>
    Board,
}

/// <summary>
/// A configured connection to an external provider (source / wiki / board), persisted in SQLite and
/// collected during onboarding. The token itself is not stored here; <see cref="CredentialRef"/> points
/// at a protected entry in the secret store (see <see cref="ISecretStore"/>).
/// </summary>
/// <param name="Id">Stable identifier for the connection (caller-assigned).</param>
/// <param name="Kind">Which store category this connection serves.</param>
/// <param name="BaseUrl">The provider base URL, when applicable (null for local providers).</param>
/// <param name="CredentialRef">Reference into the secret store for the connection's token, or null.</param>
/// <param name="Status">Free-form status set by onboarding's access test (e.g. <c>Untested</c>, <c>Verified</c>, <c>Failed</c>).</param>
/// <param name="UpdatedUtc">When the record was last written.</param>
public sealed record ProviderConnection(
    string Id,
    ProviderKind Kind,
    string? BaseUrl,
    string? CredentialRef,
    string Status,
    DateTimeOffset UpdatedUtc);
