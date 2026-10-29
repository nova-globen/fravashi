using System.Globalization;
using System.Text.RegularExpressions;

namespace Fravashi.Domain.Releases;

/// <summary>
/// A release's version, held to ONE strict shape (work item #368): an optional <c>v</c>, then
/// <c>MAJOR.MINOR.PATCH</c> with no leading zeros, then optionally <c>-</c> and a pre-release label of letters, digits,
/// dots and hyphens. That shape is also the only tag name the studio will ever create for a release, so a version a
/// person typed can never carry a colon, a caret, a space or anything else that gives a git command line extra meaning:
/// it is refused here, before anything reaches git.
/// </summary>
public sealed partial record ReleaseVersion
{
    /// <summary>The rule in words, for a refusal.</summary>
    public const string Rule =
        "A release version is MAJOR.MINOR.PATCH with an optional leading 'v' and an optional '-label' of letters, digits, "
        + "dots and hyphens, such as v0.9.97 or 1.4.0-rc.1. Numbers have no leading zeros.";

    /// <summary>The longest text accepted, so a pasted paragraph is refused rather than matched.</summary>
    public const int MaxLength = 64;

    private ReleaseVersion(string text, bool prefixed, int major, int minor, int patch, string? label)
    {
        Text = text;
        Prefixed = prefixed;
        Major = major;
        Minor = minor;
        Patch = patch;
        Label = label;
    }

    /// <summary>The version exactly as accepted, which is also its tag name.</summary>
    public string Text { get; }

    /// <summary>True when the version starts with <c>v</c>.</summary>
    public bool Prefixed { get; }

    /// <summary>The major number.</summary>
    public int Major { get; }

    /// <summary>The minor number.</summary>
    public int Minor { get; }

    /// <summary>The patch number.</summary>
    public int Patch { get; }

    /// <summary>The pre-release label after the hyphen, or null.</summary>
    public string? Label { get; }

    /// <summary>The version, or null when the text is not in the one accepted shape.</summary>
    public static ReleaseVersion? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return null;
        }

        var match = Shape().Match(text);
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return null;
        }

        var label = match.Groups["label"].Success ? match.Groups["label"].Value : null;
        if (label is not null && (label.Contains("..", StringComparison.Ordinal) || label.EndsWith('.') || label.EndsWith(".lock", StringComparison.Ordinal)))
        {
            // Inside the character class but still something git refuses or reads specially in a ref name.
            return null;
        }

        return new ReleaseVersion(text, match.Groups["v"].Success, major, minor, patch, label);
    }

    /// <summary>True when <paramref name="text"/> is a release version in the accepted shape.</summary>
    public static bool IsValid(string? text) => Parse(text) is not null;

    /// <summary>The next patch release, keeping the <c>v</c> and dropping any label.</summary>
    public ReleaseVersion NextPatch() => Make(Major, Minor, Patch + 1);

    /// <summary>The next minor release.</summary>
    public ReleaseVersion NextMinor() => Make(Major, Minor + 1, 0);

    /// <summary>The next major release.</summary>
    public ReleaseVersion NextMajor() => Make(Major + 1, 0, 0);

    /// <summary>
    /// The newest release version among <paramref name="names"/> (tag names, say), ordered by number with a labelled
    /// version below the same numbers without one; names in any other shape are ignored. Null when none qualifies.
    /// </summary>
    public static ReleaseVersion? Latest(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names
            .Select(Parse)
            .Where(v => v is not null)
            .Select(v => v!)
            .OrderByDescending(v => v.Major)
            .ThenByDescending(v => v.Minor)
            .ThenByDescending(v => v.Patch)
            .ThenBy(v => v.Label is null ? 0 : 1)
            .ThenByDescending(v => v.Label, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public override string ToString() => Text;

    private ReleaseVersion Make(int major, int minor, int patch)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{(Prefixed ? "v" : string.Empty)}{major}.{minor}.{patch}");
        return new ReleaseVersion(text, Prefixed, major, minor, patch, null);
    }

    [GeneratedRegex(
        @"^(?<v>v)?(?<major>0|[1-9][0-9]{0,8})\.(?<minor>0|[1-9][0-9]{0,8})\.(?<patch>0|[1-9][0-9]{0,8})(?:-(?<label>[0-9A-Za-z][0-9A-Za-z.-]*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}
