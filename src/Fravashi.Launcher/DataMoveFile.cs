namespace Fravashi.Launcher;

/// <summary>
/// What the data-folder move (<see cref="DataFolderMove"/>) has to say while it applies: why the data is still in the
/// old folder, or that both folders hold data. Written into <c>&lt;DataDir&gt;/data-move.txt</c> so <c>fravashi status</c>
/// and the studio's Settings Overview can both say it (the studio reads <c>/data/data-move.txt</c>), and removed once
/// the move has nothing to say. As with <see cref="BridgeReachFile"/>, no type crosses that boundary, so the file name
/// is a literal on both sides (the studio's is <c>DataMoveNotice.FileName</c>): change one and the other, together.
/// </summary>
public static class DataMoveFile
{
    public const string FileName = "data-move.txt";

    /// <summary>The words, or null when there are none.</summary>
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

    /// <summary>Leaves <paramref name="words"/> for status and the studio; best effort.</summary>
    public static void Write(string dataDirectory, string words)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, FileName), words);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the launcher log has the same words.
        }
    }

    /// <summary>Removes the words once they no longer apply; best effort.</summary>
    public static void Clear(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, FileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
