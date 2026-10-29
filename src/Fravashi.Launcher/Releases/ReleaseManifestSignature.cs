using System.Security.Cryptography;

namespace Fravashi.Launcher.Releases;

/// <summary>What checking the update manifest's signature found (backlog 242).</summary>
public enum ManifestSignature
{
    /// <summary>The sidecar is a signature by this product's release key over exactly the bytes received.</summary>
    Verified,

    /// <summary>No sidecar was published beside the manifest.</summary>
    Unsigned,

    /// <summary>A sidecar was published, but it is not a signature by this product's release key over these bytes.</summary>
    BadSignature,
}

/// <summary>
/// Verifies the update manifest against the release key (backlog 242, ruling 2026-10-07: ECDSA P-256, no new
/// binary). <c>release.yml</c> signs the EXACT bytes of <c>release-manifest.json</c> with SHA-256 and attaches the DER
/// signature, base64 on one line, beside it as <see cref="SidecarName"/> (<c>setup/release/sign-manifest.sh</c>). The
/// public key is embedded from <c>setup/release/manifest-signing-public.pem</c>. A sidecar rather than a field inside
/// the document, because verifying the bytes as received needs nothing of the manifest to be parsed first: nothing in
/// it is read before this says <see cref="ManifestSignature.Verified"/>.
/// </summary>
public static class ReleaseManifestSignature
{
    /// <summary>The signature's file name, beside <c>release-manifest.json</c>.</summary>
    public const string SidecarName = "release-manifest.sig";

    /// <summary>The words for a manifest that is not used because of its signature, whichever way it failed.</summary>
    public const string NotSignedWords =
        "the update channel's manifest is not signed by this product's release key, so nothing from it is used";

    private const string KeyResource = "Fravashi.Launcher.Releases.manifest-signing-public.pem";

    private static readonly Lazy<string> EmbeddedKey = new(ReadEmbeddedKey);

    /// <summary>The release key's public half as embedded in this build (PEM, SPKI).</summary>
    public static string ReleaseKeyPem => EmbeddedKey.Value;

    /// <summary>Why a refused manifest was refused, after <see cref="NotSignedWords"/>.</summary>
    public static string Why(ManifestSignature outcome) => outcome switch
    {
        ManifestSignature.Unsigned => "no signature was published beside it",
        ManifestSignature.BadSignature => "the signature beside it does not match it",
        _ => "it was verified",
    };

    /// <summary>The whole refusal sentence: <see cref="NotSignedWords"/> and why, in parentheses.</summary>
    public static string Refusal(ManifestSignature outcome) => $"{NotSignedWords} ({Why(outcome)})";

    /// <summary>
    /// Checks <paramref name="sidecar"/> (the sidecar's text, null when none was published) against
    /// <paramref name="manifest"/>'s bytes with <paramref name="publicKeyPem"/>, by default the embedded release key.
    /// </summary>
    public static ManifestSignature Verify(ReadOnlySpan<byte> manifest, string? sidecar, string? publicKeyPem = null)
    {
        if (sidecar is null)
        {
            return ManifestSignature.Unsigned;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(sidecar.Trim());
        }
        catch (FormatException)
        {
            return ManifestSignature.BadSignature;
        }

        if (signature.Length == 0)
        {
            return ManifestSignature.BadSignature;
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem ?? ReleaseKeyPem);
        try
        {
            return key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
                ? ManifestSignature.Verified
                : ManifestSignature.BadSignature;
        }
        catch (CryptographicException)
        {
            // A DER sequence that does not even parse is a bad signature, not a crash.
            return ManifestSignature.BadSignature;
        }
    }

    private static string ReadEmbeddedKey()
    {
        using var stream = typeof(ReleaseManifestSignature).Assembly.GetManifestResourceStream(KeyResource)
            ?? throw new InvalidOperationException($"The release key {KeyResource} is not embedded in this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
