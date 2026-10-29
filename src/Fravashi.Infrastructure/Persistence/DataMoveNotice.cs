namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// What the launcher's data-folder move (the rename to Fravashi, <c>Fravashi.Launcher.DataFolderMove</c>) left for the
/// studio to say: why the data is still in the former folder, or that both folders hold an install. The
/// launcher writes it into the data directory, mounted here at <c>/data</c>, and removes it once it no longer applies;
/// Settings > General > Overview shows it. As with <c>bridge-reach.txt</c>, no type crosses that boundary, so the file
/// name is a literal on both sides (the launcher's is <c>DataMoveFile.FileName</c>): change one and the other, together.
/// </summary>
public static class DataMoveNotice
{
    public const string FileName = "data-move.txt";

    /// <summary>The launcher's words, or null when it left none.</summary>
    public static string? Read(DataDirectory dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        try
        {
            var path = System.IO.Path.Combine(dataDirectory.Path, FileName);
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
