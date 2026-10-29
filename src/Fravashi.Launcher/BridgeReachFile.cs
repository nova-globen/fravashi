namespace Fravashi.Launcher;

/// <summary>
/// What the studio last found when it asked fravashi.exe's host bridge for an answer from INSIDE the container (backlog
/// 252), read so <c>fravashi status</c> can say it too. Only the studio can ask from where the container stands, so it
/// writes the words into <c>&lt;DataDir&gt;/bridge-reach.txt</c> while they apply and removes the file when the
/// bridge answers (<c>Fravashi.Infrastructure.Bridge.BridgeReachMonitor</c>); this side relays them verbatim. As
/// with <see cref="RestartReasonFile"/>, there is no shared type across that boundary, so the file name is a literal
/// on both sides: change one and the other, together.
/// </summary>
public static class BridgeReachFile
{
    public const string FileName = "bridge-reach.txt";

    /// <summary>The studio's words, or null when it left none (the bridge answers, or nothing was measured).</summary>
    public static string? Read(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, FileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
