using System.Globalization;

namespace Fravashi.Application.Updates;

/// <summary>
/// A compact semantic-version comparison for the in-container tool-update check (Type A, ADR 0006): decide
/// whether a registry's latest published version is newer than the one the CLI reports. It parses a
/// <c>major.minor.patch</c> core with an optional pre-release suffix (a pre-release sorts below its release
/// core, per SemVer 2.0.0) and tolerates a leading <c>v</c> and build metadata.
/// </summary>
/// <remarks>
/// This deliberately mirrors the launcher's <c>SemanticVersion</c>: the two live in non-referencing assemblies
/// (the web app never references the launcher), so a tiny pure comparer is duplicated rather than a shared
/// project introduced for one type. It is a comparison helper, not a source of truth for data.
/// </remarks>
public sealed record SemVer(int Major, int Minor, int Patch, string? PreRelease) : IComparable<SemVer>
{
    /// <summary>Non-throwing parse of <c>[v]major.minor.patch[-prerelease][+build]</c>.</summary>
    public static bool TryParse(string? text, out SemVer version)
    {
        version = new SemVer(0, 0, 0, null);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.Trim();
        if (span.StartsWith('v') || span.StartsWith('V'))
        {
            span = span[1..];
        }

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

        version = new SemVer(numbers[0], numbers[1], numbers[2], preRelease);
        return true;
    }

    /// <summary>
    /// True when <paramref name="latest"/> is a strictly newer version than <paramref name="installed"/>.
    /// If either side is unparseable, returns <c>false</c> (fail closed to "no update" - never claim an update
    /// we cannot substantiate).
    /// </summary>
    public static bool IsNewer(string? latest, string? installed) =>
        TryParse(latest, out var l) && TryParse(installed, out var i) && l.CompareTo(i) > 0;

    /// <summary>Precedence comparison: core numbers first, then a pre-release sorts below its release core.</summary>
    public int CompareTo(SemVer? other)
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

        return (PreRelease, other.PreRelease) switch
        {
            (null, null) => 0,
            (null, not null) => 1,
            (not null, null) => -1,
            var (a, b) => string.CompareOrdinal(a, b),
        };
    }
}
