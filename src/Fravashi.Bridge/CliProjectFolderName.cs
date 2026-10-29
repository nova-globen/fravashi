using System.Text;

namespace Fravashi.Bridge;

/// <summary>
/// The folder name the Claude CLI files a working directory's conversations under, inside its
/// <c>projects</c> folder (backlog 43). Ported from the CLI's own bundle rather than inferred: every
/// UTF-16 code unit outside <c>[A-Za-z0-9]</c> becomes a hyphen (so a drive colon, every separator, a
/// dot and a SPACE all do, which is the case the archived scanner's weaker encoder got wrong), and a
/// result longer than 200 characters is cut to 200 and suffixed with a hyphen and the absolute value
/// of a 32-bit string hash of the ORIGINAL path in base 36. Case is kept as the path spells it; on
/// Windows the file system matches the folder case-insensitively, exactly as the CLI's own lookup does.
/// </summary>
public static class CliProjectFolderName
{
    /// <summary>The CLI's cut-off before it appends the hash.</summary>
    public const int MaxLength = 200;

    public static string Encode(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        var builder = new StringBuilder(workingDirectory.Length);
        foreach (var c in workingDirectory)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }

        if (builder.Length <= MaxLength)
        {
            return builder.ToString();
        }

        return string.Concat(builder.ToString(0, MaxLength), "-", ToBase36(Math.Abs((long)Hash(workingDirectory))));
    }

    /// <summary>The CLI's <c>(h &lt;&lt; 5) - h + charCode | 0</c> over UTF-16 code units.</summary>
    private static int Hash(string value)
    {
        var hash = 0;
        foreach (var c in value)
        {
            hash = unchecked((hash << 5) - hash + c);
        }

        return hash;
    }

    private static string ToBase36(long value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value == 0)
        {
            return "0";
        }

        var chars = new Stack<char>();
        while (value > 0)
        {
            chars.Push(digits[(int)(value % 36)]);
            value /= 36;
        }

        return new string([.. chars]);
    }
}
