using System.Security.Cryptography;
using System.Text;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// RFC 7636 (PKCE) helpers for the IdP sign-in flow (plan Phase 3). The verifier never leaves
/// the server side: it lives in the auth-code state ledger until the callback exchanges it.
/// </summary>
public static class Pkce
{
    /// <summary>A fresh high-entropy code verifier (32 random bytes, base64url).</summary>
    public static string NewVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    /// <summary>The S256 code challenge for a verifier: base64url(SHA256(ascii(verifier))).</summary>
    public static string ChallengeS256(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
