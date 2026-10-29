using System.Security.Cryptography;

namespace Fravashi.AppRunner;

/// <summary>
/// The ONE named field a verification run gets (backlog 226, the ruling of 2026-10-07, ADR 0030 decision 8,
/// plan @69). When the verifier starts a run as a verification run, the app runner mints a window token for THAT run
/// and gives the application container <see cref="Name"/> carrying it, plus the studio's own
/// <see cref="WindowTokenName"/> set to the same value, so a studio built from the repository requires that token at
/// its window-only gate and honours the flag only because the two match. Nothing else ever sets either name: a spec
/// that names one is refused by name (<see cref="SpecReader"/>), and no operation but a verification run's start
/// passes them. The live studio's token is never read, here or anywhere on this path.
/// </summary>
public static class VerificationRunField
{
    /// <summary>The flag. Its value is the run's minted window token.</summary>
    public const string Name = "FRAVASHI_VERIFICATION_RUN";

    /// <summary>The studio's window token variable, set for the run to the same minted value.</summary>
    public const string WindowTokenName = "FRAVASHI_WINDOW_TOKEN";

    /// <summary>Both names the field occupies; a spec may set neither.</summary>
    public static readonly IReadOnlyList<string> Names = [Name, WindowTokenName];

    /// <summary>Why a spec that names either is refused.</summary>
    public const string Refusal =
        "it is the app runner's verification-run field (backlog 226); only a verification run's start sets it, never a spec";

    /// <summary>A fresh token for one run: 32 random bytes as 64 lower-case hex digits.</summary>
    public static RunWindowToken Mint() => new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
}

/// <summary>
/// One verification run's minted window token. A class rather than a string so that a record holding it, logged or
/// interpolated, prints <see cref="ToString"/> and never the value.
/// </summary>
public sealed class RunWindowToken
{
    internal RunWindowToken(string value) => Value = value;

    /// <summary>The token itself. Handed to the run's environment and to the verifier that started it, nowhere else.</summary>
    public string Value { get; }

    /// <summary>What the application container is given: the flag and the window token, both this value.</summary>
    public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [VerificationRunField.Name] = Value,
        [VerificationRunField.WindowTokenName] = Value,
    };

    public override string ToString() => "(a verification run's window token, withheld)";
}
