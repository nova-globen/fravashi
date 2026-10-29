namespace Fravashi.Application.Persistence;

/// <summary>
/// Stores provider credentials at rest, keyed by a stable reference (see
/// <see cref="ProviderConnection.CredentialRef"/>). Values are protected on write and unprotected on read
/// via <see cref="ISecretProtector"/>; callers deal only in plaintext and never see the stored form.
/// </summary>
public interface ISecretStore
{
    /// <summary>Protects <paramref name="plaintext"/> and stores it under <paramref name="reference"/>.</summary>
    void Set(string reference, string plaintext);

    /// <summary>Returns the unprotected secret for <paramref name="reference"/>, or null if none is stored.</summary>
    string? Get(string reference);

    void Remove(string reference);
}
