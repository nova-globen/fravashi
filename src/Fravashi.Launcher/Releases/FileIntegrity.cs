using System.Security.Cryptography;

namespace Fravashi.Launcher.Releases;

/// <summary>
/// SHA-256 integrity checks over downloaded release assets (ADR 0006): a consumer verifies an asset's hash
/// against the manifest <b>before</b> it is loaded or unpacked. This is the launcher-side twin of what the
/// install scripts do in shell - kept here so the update path (T5) verifies with the same algorithm the
/// manifest was written with. The manifest's signature (<see cref="ReleaseManifestSignature"/>, backlog 242) sits
/// <em>above</em> this: the signature authenticates the manifest against the release key, then the hash authenticates
/// each asset against that verified manifest. A hash here proves nothing about a manifest that was not verified.
/// </summary>
public static class FileIntegrity
{
    /// <summary>Lower-case hex SHA-256 of a file's contents (streamed, so a multi-GB image tar is fine).</summary>
    public static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Lower-case hex SHA-256 of an in-memory buffer (e.g. a fetched manifest body).</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// True when the file's SHA-256 matches <paramref name="expected"/>. Comparison is case-insensitive and
    /// tolerant of a leading <c>sha256:</c> prefix. An empty expected hash is never a match (fail closed).
    /// </summary>
    public static bool VerifyFile(string path, string expected)
    {
        var normalized = Normalize(expected);
        return normalized.Length > 0 &&
               string.Equals(Sha256Hex(path), normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return "";
        }

        var trimmed = hex.Trim();
        return trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? trimmed["sha256:".Length..]
            : trimmed;
    }
}
