using System.Security.Cryptography;
using System.Text;

namespace Fravashi.Bridge;

/// <summary>
/// The per-install bridge access token: minted by fravashi.exe, persisted beside launcher.json, handed
/// to the studio container as <c>FRAVASHI_BRIDGE_TOKEN</c>, and required on every bridge request.
/// Same trust model as the window token (ADR 0017): a local coupling secret between processes the
/// launcher itself started, not an internet credential; the bridge additionally binds only to
/// loopback and the WSL-facing interface. Verification is constant-time.
/// </summary>
public static class BridgeAccessToken
{
    public const string FileName = "bridge.token";
    public const string UrlEnvironmentVariable = "FRAVASHI_BRIDGE_URL";
    public const string TokenEnvironmentVariable = "FRAVASHI_BRIDGE_TOKEN";

    public static string GetOrCreate(string configDirectory)
    {
        var path = Path.Combine(configDirectory, FileName);
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length > 0)
                {
                    return existing;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall through to minting; worst case bridge and container get a fresh pair.
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        try
        {
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(path, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unpersistable: still usable for this run (bridge and container both get this value).
        }

        return token;
    }

    public static string? TryRead(string configDirectory)
    {
        try
        {
            var path = Path.Combine(configDirectory, FileName);
            if (File.Exists(path))
            {
                var token = File.ReadAllText(path).Trim();
                return token.Length > 0 ? token : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>Constant-time comparison so request auth leaks nothing through timing.</summary>
    public static bool Verify(string? presented, string expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
    }
}
