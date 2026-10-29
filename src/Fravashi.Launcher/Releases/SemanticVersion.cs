using System.Globalization;

namespace Fravashi.Launcher.Releases;

/// <summary>
/// A small semantic-version value for the launcher's update comparison - enough of SemVer 2.0.0 for the
/// release channel (ADR 0006): a <c>major.minor.patch</c> core with an optional pre-release suffix. A leading
/// <c>v</c> is accepted (so a git-tag string parses). Build metadata (<c>+…</c>) is ignored per SemVer.
/// A pre-release sorts <em>below</em> its release core (<c>1.0.0-rc.1 &lt; 1.0.0</c>). This is deliberately
/// modest: it is a comparison helper, not a full SemVer engine.
/// </summary>
public sealed record SemanticVersion(int Major, int Minor, int Patch, string? PreRelease)
    : IComparable<SemanticVersion>
{
    /// <summary>Parses a version string; throws <see cref="FormatException"/> on malformed input.</summary>
    public static SemanticVersion Parse(string text)
    {
        if (!TryParse(text, out var version))
        {
            throw new FormatException($"'{text}' is not a recognised version.");
        }

        return version;
    }

    /// <summary>Non-throwing parse of <c>[v]major.minor.patch[-prerelease][+build]</c>.</summary>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = new SemanticVersion(0, 0, 0, null);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.Trim();
        if (span.StartsWith('v') || span.StartsWith('V'))
        {
            span = span[1..];
        }

        // Strip build metadata (ignored for precedence per SemVer).
        var plus = span.IndexOf('+');
        if (plus >= 0)
        {
            span = span[..plus];
        }

        string? preRelease = null;
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = span[(dash + 1)..];
            span = span[..dash];
            if (preRelease.Length == 0)
            {
                return false;
            }
        }

        var parts = span.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return false;
            }

            numbers[i] = n;
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], preRelease);
        return true;
    }

    /// <summary>Precedence comparison: core numbers first, then a pre-release sorts below its release core.</summary>
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major.CompareTo(other.Major);
        if (core != 0)
        {
            return core;
        }

        core = Minor.CompareTo(other.Minor);
        if (core != 0)
        {
            return core;
        }

        core = Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }

        // Equal cores: no pre-release outranks any pre-release; otherwise ordinal on the suffix.
        return (PreRelease, other.PreRelease) switch
        {
            (null, null) => 0,
            (null, not null) => 1,
            (not null, null) => -1,
            var (a, b) => string.CompareOrdinal(a, b),
        };
    }
}
