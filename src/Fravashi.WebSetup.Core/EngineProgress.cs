using System.Globalization;
using System.Text.RegularExpressions;
using Fravashi.Domain;

namespace Fravashi.WebSetup;

/// <summary>Where an install has got to, as far as the engine's own narration says.</summary>
public enum EngineStage
{
    Starting,
    LookingUp,
    Downloading,
    Verifying,
    Unpacking,
    Installing,
    Installed,
}

/// <summary>
/// Reads the engine's output into progress. The engine already narrates every step with <c>Write-Host</c>
/// (which reaches stdout when stdout is a pipe), so nothing about the engine changes for the wizard: the lines
/// matched here are the ones <c>setup/webinstall/webinstall.ps1</c> prints, and EngineProgressTests holds each
/// one against the script's own text so a reworded line is a red test rather than a stuck progress page.
/// curl draws its progress bar on stderr as <c>####   42.1%</c> fragments separated by carriage returns;
/// those become <see cref="Percent"/>.
/// </summary>
public sealed partial class EngineProgress
{
    /// <summary>
    /// How the engine begins its reason when the update channel answered 404 for the release manifest: nothing is
    /// published in that folder (yet).
    /// </summary>
    public const string NotPublishedMarker = $"No {ProductName.Short} release is published at ";

    public const string LookingUpLine = "Looking up the release ...";
    public const string ReleasePrefix = "Release: ";
    public const string UnpackingLine = "Unpacking the launcher bundle ...";
    public const string DecompressingPrefix = "Decompressing ";
    public const string HandOffLine = "Handing off to the release's install-windows.ps1 ...";
    public const string VerifiedPrefix = "verified sha256 ";
    public const string ReassembledPrefix = "reassembled ";
    public const string FailedPrefix = "FAILED: ";

    public EngineStage Stage { get; private set; } = EngineStage.Starting;

    /// <summary>What the current stage is working on: the file being downloaded, for instance.</summary>
    public string? Item { get; private set; }

    /// <summary>curl's percentage for the current file, or null when no transfer is drawing one.</summary>
    public double? Percent { get; private set; }

    /// <summary>The release the engine resolved (<c>latest</c> becomes a real tag here).</summary>
    public string? ResolvedTag { get; private set; }

    /// <summary>Whether the update channel has no release in the folder asked for (its manifest answered 404).</summary>
    public bool NotPublished { get; private set; }

    /// <summary>
    /// The last non-empty line the engine printed on stdout. When the engine fails, this is its reason: both
    /// <c>Die</c> and the <c>trap</c> print their message last, and without <c>-Pause</c> nothing follows it.
    /// </summary>
    public string? LastLine { get; private set; }

    /// <summary>The last non-empty stderr line that was not a progress bar, for a failure stdout never explained.</summary>
    public string? LastErrorLine { get; private set; }

    /// <summary>Raised after any line or fragment changed something a window would show.</summary>
    public event Action? Changed;

    /// <summary>Takes one stdout line and returns it, ready for the window and the log.</summary>
    public string OnOutputLine(string line)
    {
        var text = line.Trim();
        if (text.Length == 0)
        {
            return line;
        }

        LastLine = text;

        if (text.StartsWith(NotPublishedMarker, StringComparison.Ordinal))
        {
            NotPublished = true;
        }

        if (text == LookingUpLine)
        {
            Move(EngineStage.LookingUp, null);
        }
        else if (text.StartsWith(ReleasePrefix, StringComparison.Ordinal))
        {
            var rest = text[ReleasePrefix.Length..];
            var space = rest.IndexOf(' ', StringComparison.Ordinal);
            ResolvedTag = space < 0 ? rest : rest[..space];
        }
        else if (DownloadingLine().Match(text) is { Success: true } download)
        {
            Move(EngineStage.Downloading, download.Groups["what"].Value);
        }
        else if (text.StartsWith(ReassembledPrefix, StringComparison.Ordinal))
        {
            // Slices joined: still the download stage (the image comes after the launcher), now naming the whole file.
            Item = text;
            Percent = null;
        }
        else if (text.StartsWith(VerifiedPrefix, StringComparison.Ordinal))
        {
            Move(EngineStage.Verifying, text);
        }
        else if (text == UnpackingLine || text.StartsWith(DecompressingPrefix, StringComparison.Ordinal))
        {
            Move(EngineStage.Unpacking, text.TrimEnd('.', ' '));
        }
        else if (text == HandOffLine)
        {
            Move(EngineStage.Installing, null);
        }
        else if (InstalledLine().IsMatch(text))
        {
            Move(EngineStage.Installed, null);
        }

        Changed?.Invoke();
        return line;
    }

    /// <summary>
    /// Takes one stderr fragment (split on carriage returns and line feeds). A curl progress bar updates
    /// <see cref="Percent"/> and returns null (it is not worth a log line per redraw); anything else is
    /// returned, to be shown and logged like a line.
    /// </summary>
    public string? OnErrorFragment(string fragment)
    {
        var text = fragment.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var percent = CurlPercent(text);
        if (percent is not null)
        {
            Percent = percent;
            Changed?.Invoke();
            return null;
        }

        LastErrorLine = text;
        Changed?.Invoke();
        return text;
    }

    /// <summary>
    /// curl's <c>--progress-bar</c> percentage from one redraw, or null when the fragment is not one. A bar is
    /// only '#', spaces and a trailing percentage; curl prints it with one decimal and a dot.
    /// </summary>
    public static double? CurlPercent(string fragment)
    {
        var match = CurlBar().Match(fragment.Trim());
        if (!match.Success)
        {
            return null;
        }

        var value = double.Parse(match.Groups["pct"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        return value is >= 0 and <= 100 ? value : null;
    }

    private void Move(EngineStage stage, string? item)
    {
        Stage = stage;
        Item = item;
        Percent = null;
    }

    // "Downloading <name> ..." from the update channel, which is public.
    [GeneratedRegex(@"^Downloading (?<what>.+?) \.\.\.$", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadingLine();

    // The engine says the full name since the rename to Fravashi; an engine from before it said the former short name
    // (FormerNames; the cleanup release drops it).
    [GeneratedRegex(@"^(?:Nova Globen Fravashi|" + FormerNames.Product + @") \S+ is installed\.$", RegexOptions.CultureInvariant)]
    private static partial Regex InstalledLine();

    [GeneratedRegex(@"^#*\s*(?<pct>\d{1,3}(?:[.,]\d+)?)%$", RegexOptions.CultureInvariant)]
    private static partial Regex CurlBar();
}
