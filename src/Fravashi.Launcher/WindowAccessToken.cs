namespace Fravashi.Launcher;

/// <summary>
/// The per-install window access token (ADR 0017): the studio serves ONLY the studio window shell, and
/// this token is how the launcher couples the two: it goes to the studio container as the
/// <c>FRAVASHI_WINDOW_TOKEN</c> env var and to the studio window as its second argument (which brands it
/// into the WebView2 user agent). Minted once and persisted beside <c>launcher.json</c> so the tray
/// and later launcher runs open windows the already-running container accepts. A local coupling
/// marker for the single-user studio, not a secret (the real perimeter is the localhost binding);
/// losing the file just mints a new token on the next container (re)create.
/// </summary>
public static class WindowAccessToken
{
    public const string FileName = "window.token";

    public const string EnvironmentVariable = "FRAVASHI_WINDOW_TOKEN";

    /// <summary>Read the persisted token, minting and saving one when absent (or unreadable).</summary>
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
            // Fall through to minting; worst case the window and container get a fresh pair.
        }

        var token = Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(path, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unpersistable: still usable for this run (window and container both get this value).
        }

        return token;
    }

    /// <summary>Read the persisted token without minting (the tray path: the container already runs).</summary>
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
            // No token readable.
        }

        return null;
    }
}
