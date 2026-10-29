namespace Fravashi.Web.Components.WorkItems;

/// <summary>
/// One wiki document related to a work item, as the related-items sidebar shows it (requested 2026-10-22,
/// related items sidebar): either the item points at it (an agent-layer reference such as its plan or its
/// approved design) or it points at the item (a typed link or an @-style reference in its body, read through
/// the wiki's own backlinks).
/// </summary>
/// <param name="Number">The document's number, which is what <c>@N</c> names and <c>/wiki/N</c> opens.</param>
/// <param name="Title">The document's title, or the reference's label when the document is gone.</param>
/// <param name="Relation">How it is related, in a few words ("plan", "approved design", "report, linked").</param>
/// <param name="IsPlan">True for the item's plan, which is listed first.</param>
/// <param name="Exists">False when the item names a document this project no longer has.</param>
public sealed record WorkItemWikiLink(int Number, string Title, string Relation, bool IsPlan, bool Exists = true);
