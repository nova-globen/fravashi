namespace Fravashi.Launcher;

/// <summary>
/// Compares two spellings of one image id. Docker and wslc print <c>sha256:&lt;hex&gt;</c> and podman prints the bare
/// hex, so the integrity pin written under one engine never equalled the same image's id under another, and switching
/// engines failed the check closed on an untouched image (seen 2026-10-04, when the dev host moved from podman to wslc).
/// Only that prefix is ignored: the hex must still match exactly, so this never accepts a different image.
/// </summary>
public static class ImageIds
{
    private const string Prefix = "sha256:";

    /// <summary>True when both are present and name the same image.</summary>
    public static bool Same(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(Bare(left), Bare(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string Bare(string id)
    {
        var trimmed = id.Trim();
        return trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? trimmed[Prefix.Length..] : trimmed;
    }
}
