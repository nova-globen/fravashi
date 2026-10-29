using System.Globalization;
using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Wiki;

/// <summary>Where a reference leads, once the studio has checked that the thing exists.</summary>
/// <param name="Url">The studio route that shows it.</param>
/// <param name="Title">What it is called, for a tooltip.</param>
public sealed record WikiReferenceTarget(string Url, string? Title = null);

/// <summary>
/// The answer to "does this exist and where does it lead", for every reference in one document (backlog 39).
/// <para>
/// It is resolved ONCE, before rendering, rather than looked up per reference while the markdown is walked.
/// Rendering is synchronous and the answers come from a database, and a renderer that blocks on a query per
/// reference is a page that stalls on a document with fifty of them.
/// </para>
/// </summary>
public sealed class WikiReferenceMap
{
    private readonly Dictionary<WikiReference, WikiReferenceTarget> _targets = [];

    /// <summary>A map that knows nothing, so every reference in a document renders as broken.</summary>
    public static WikiReferenceMap Empty { get; } = new();

    /// <summary>The studio route for each kind. One place, so a moved route moves once.</summary>
    public static string RouteFor(WikiReference reference) => reference.Kind switch
    {
        WikiReferenceKind.WorkItem =>
            "/workitems/" + reference.Number.ToString(CultureInfo.InvariantCulture),
        WikiReferenceKind.WikiItem =>
            "/wiki/" + reference.Number.ToString(CultureInfo.InvariantCulture),
        WikiReferenceKind.PullRequest =>
            "/git/pr/" + reference.Number.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(reference), reference, "Not a reference kind."),
    };

    /// <summary>Records that a reference resolves, with what it is called.</summary>
    public WikiReferenceMap Add(WikiReference reference, string? title)
    {
        _targets[reference] = new WikiReferenceTarget(RouteFor(reference), title);
        return this;
    }

    /// <summary>Where a reference leads, or null when nothing of that number exists.</summary>
    public WikiReferenceTarget? Resolve(WikiReference reference) =>
        _targets.TryGetValue(reference, out var target) ? target : null;
}
