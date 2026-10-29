namespace Fravashi.Application.Verification;

/// <summary>What a built page showed on one screen, phase by phase and viewport by viewport (backlog 222, plan @46).</summary>
/// <param name="Screen">The design screen's index the page was checked against.</param>
/// <param name="Phases">One entry per phase and viewport that was attempted, in any order.</param>
public sealed record DesignObservation(int Screen, IReadOnlyList<ObservedPhase> Phases);

/// <summary>One phase of a page at one viewport.</summary>
/// <param name="Phase">The phase's name, as <see cref="DesignPhase.Name"/> spells it.</param>
/// <param name="Viewport">The viewport's name, or null for a design that names none.</param>
/// <param name="Width">The viewport's width in CSS pixels, or null.</param>
/// <param name="Nodes">The aria snapshot's top-level nodes, or null when the phase was not reached.</param>
/// <param name="NotReached">Why the driver could not reach the phase (its opener was not found), or null when it was.</param>
public sealed record ObservedPhase(
    string Phase, string? Viewport, int? Width, IReadOnlyList<ObservedNode>? Nodes, string? NotReached);

/// <summary>
/// One element of a Playwright aria snapshot: what the snapshot prints for it, plus the two probes the snapshot does not
/// carry (its box and, for text a person reads, its contrast).
/// </summary>
/// <param name="Role">The ARIA role as Playwright prints it (<c>text</c> for a bare text item).</param>
/// <param name="Name">The accessible name, or null when none was printed.</param>
/// <param name="Text">The inline text after the colon (<c>- generic: Name</c>), or null.</param>
/// <param name="Attributes">The bracketed attributes in order of appearance, a bare one with a null value.</param>
/// <param name="Children">The nested nodes, in order.</param>
public sealed record ObservedNode(
    string Role,
    string? Name,
    string? Text,
    IReadOnlyDictionary<string, string?> Attributes,
    IReadOnlyList<ObservedNode> Children)
{
    /// <summary>The snapshot's element reference (<c>[ref=e5]</c>), or null when it printed none.</summary>
    public string? Ref => Attributes.TryGetValue("ref", out var value) ? value : null;

    /// <summary>The element's box in CSS pixels, relative to the viewport, or null when it was not measured.</summary>
    public ObservedBox? Box { get; init; }

    /// <summary>The WCAG 2 contrast of its text against its effective background, or null when it was not measured.</summary>
    public double? Contrast { get; init; }
}

/// <summary>A box in CSS pixels.</summary>
public sealed record ObservedBox(double X, double Y, double Width, double Height)
{
    /// <summary>Its right edge.</summary>
    public double Right => X + Width;

    /// <summary>Its bottom edge.</summary>
    public double Bottom => Y + Height;

    /// <summary>The smallest box holding both.</summary>
    public ObservedBox Union(ObservedBox other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new ObservedBox(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }
}
