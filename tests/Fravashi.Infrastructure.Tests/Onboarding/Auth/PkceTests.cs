using Fravashi.Infrastructure.Onboarding.Auth;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding.Auth;

/// <summary>Plan Phase 3 Task 1: RFC 7636 code-verifier/challenge helpers.</summary>
public class PkceTests
{
    [Fact]
    public void ChallengeS256_MatchesTheRfc7636AppendixBVector()
    {
        // RFC 7636 appendix B: this verifier must yield exactly this S256 challenge.
        Pkce.ChallengeS256("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")
            .Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }

    [Fact]
    public void NewVerifier_IsBase64UrlShaped_AndUnique()
    {
        var first = Pkce.NewVerifier();
        var second = Pkce.NewVerifier();

        first.Should().NotBe(second);
        first.Length.Should().BeGreaterThanOrEqualTo(43); // 32 bytes -> 43 base64url chars
        first.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }
}
