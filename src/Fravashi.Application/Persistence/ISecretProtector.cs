namespace Fravashi.Application.Persistence;

/// <summary>
/// Protects secret values (provider tokens) at rest. This is a seam, deliberately narrow: the current
/// implementation is PROVISIONAL - ASP.NET Core Data Protection with keys persisted beside the database
/// on the data volume (there is no OS keychain / DPAPI inside a Linux container). A stronger scheme
/// (passphrase-derived key, external KMS) replaces the implementation behind this interface without
/// touching callers. See <c>.agent/memory/decision-log.md</c> and the credentials-at-rest ADR-worthy
/// question in ADR 0003.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/>, returning an opaque, storable string.</summary>
    string Protect(string plaintext);

    /// <summary>Reverses <see cref="Protect"/>. Throws if the value was not produced by a compatible protector.</summary>
    string Unprotect(string protectedValue);
}
