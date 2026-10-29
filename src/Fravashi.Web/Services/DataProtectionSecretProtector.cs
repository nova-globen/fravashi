using Fravashi.Application.Persistence;
using Microsoft.AspNetCore.DataProtection;

namespace Fravashi.Web.Services;

/// <summary>
/// PROVISIONAL <see cref="ISecretProtector"/> backed by ASP.NET Core Data Protection. The key ring is
/// persisted to the data volume (see Program.cs), which works inside a Linux container where DPAPI / an OS
/// keychain is unavailable. The keys therefore sit beside the database on the same volume - a deliberate
/// same-volume compromise, decided as such; the durable
/// credentials-at-rest decision is deferred to its own ADR (ADR 0003). Replace the implementation behind
/// <see cref="ISecretProtector"/> to strengthen it without touching callers.
/// </summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;

    /// <summary>The protector purpose of the secret store.</summary>
    public const string Purpose = "Fravashi.Credentials.v1";

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
