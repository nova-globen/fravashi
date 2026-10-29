using Fravashi.Application.Verification;

namespace Fravashi.Application.Prototypes;

/// <summary>
/// A prototype: a generated derivative of a design, never a second truth (ADR 0021 decision 6, backlog 211).
/// Only <see cref="PrototypeReader"/> makes one from text, so a document that exists is one that passed every
/// rule, including that its pinned design actually exists.
/// </summary>
/// <param name="Version">The schema version the document was written in.</param>
/// <param name="Design">The design this is a prototype of, pinned to a version.</param>
/// <param name="Overlay">The direct edits kept on top of the design's rendered output, in the order they apply.</param>
public sealed record PrototypeDocument(
    int Version,
    PrototypeDesignRef Design,
    IReadOnlyList<PrototypeOperation> Overlay);

/// <summary>The design a prototype is generated from, pinned to the version it was generated at.</summary>
/// <param name="Number">The design document's number.</param>
/// <param name="Version">The version of the design the prototype was generated from.</param>
public sealed record PrototypeDesignRef(int Number, int Version);

/// <summary>
/// Where an overlay operation applies: a node's id when it has one, else its tree path (an RFC-6901 JSON
/// Pointer into the design's mockup tree, e.g. <c>/screens/0/children/2</c>). Exactly one of the two, never
/// both, never neither.
/// <para>
/// Backlog 238 (plan @52): optionally one PART of that node, the element the renderer draws for it without an anchor
/// of its own (a screen's or dialog's title, a caption, a panel's tab, a column header), named by #221's
/// <see cref="DesignPart"/> and, for a column header, the column's record key as <see cref="DesignExpectation.Column"/>
/// names it. <see cref="DesignPart.Node"/> (no <c>part</c> in the JSON) is the node's own element, as before.
/// </para>
/// </summary>
public sealed record PrototypeAnchor(string? Id, string? Path, DesignPart Part = DesignPart.Node, string? Column = null);

/// <summary>Where an inserted HTML fragment lands, relative to its anchor.</summary>
public enum PrototypePlacement
{
    /// <summary>Immediately before the anchor.</summary>
    Before,

    /// <summary>Immediately after the anchor.</summary>
    After,

    /// <summary>Inside the anchor, as its last child.</summary>
    Inside,
}

/// <summary>
/// One overlay operation. The closed set below is every kind there is; an operation outside it is the
/// "unknown operation" refusal. All but <see cref="PrototypePageCssOperation"/> carry an <see cref="Anchor"/>.
/// </summary>
public abstract record PrototypeOperation
{
    /// <summary>The stable name <see cref="PrototypeVocabulary"/> stores and reads this kind under.</summary>
    public abstract string Op { get; }
}

/// <summary>Replaces an anchor's visible text.</summary>
public sealed record PrototypeSetTextOperation(PrototypeAnchor Anchor, string Text) : PrototypeOperation
{
    /// <inheritdoc />
    public override string Op => PrototypeVocabulary.SetText;
}

/// <summary>Sets one or more inline style properties on an anchor.</summary>
public sealed record PrototypeSetCssOperation(PrototypeAnchor Anchor, IReadOnlyDictionary<string, string> Properties)
    : PrototypeOperation
{
    /// <inheritdoc />
    public override string Op => PrototypeVocabulary.SetCss;
}

/// <summary>Sets one attribute on an anchor. Never an event handler: that is refused by name.</summary>
public sealed record PrototypeSetAttributeOperation(PrototypeAnchor Anchor, string AttributeName, string Value)
    : PrototypeOperation
{
    /// <inheritdoc />
    public override string Op => PrototypeVocabulary.SetAttribute;
}

/// <summary>Adds one class to an anchor.</summary>
public sealed record PrototypeAddClassOperation(PrototypeAnchor Anchor, string Class) : PrototypeOperation
{
    /// <inheritdoc />
    public override string Op => PrototypeVocabulary.AddClass;
}

/// <summary>
/// A page-level CSS rule. The one kind with no anchor: a rule is not about one node, it is an addition to
/// the page's own stylesheet.
/// </summary>
public sealed record PrototypePageCssOperation(string Rule) : PrototypeOperation
{
    /// <inheritdoc />
    public override string Op => PrototypeVocabulary.PageCss;
}

/// <summary>Inserts a fragment of HTML before, after or inside an anchor.</summary>
public sealed record PrototypeInsertHtmlOperation(PrototypeAnchor Anchor, PrototypePlacement Placement, string Html)
    : PrototypeOperation
{
    /// <inheritdoc />
    public override string Op => PrototypeVocabulary.InsertHtml;
}

/// <summary>One thing wrong with a prototype document.</summary>
/// <param name="Path">Where, as a JSON path from the root (<c>$.overlay[2].anchor</c>).</param>
/// <param name="Message">What is wrong and what would be allowed instead.</param>
public sealed record PrototypeError(string Path, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>A document read: the document when it is valid, otherwise every error found.</summary>
public sealed record PrototypeReadResult(PrototypeDocument? Document, IReadOnlyList<PrototypeError> Errors)
{
    /// <summary>True when the document passed every rule.</summary>
    public bool IsValid => Document is not null;
}

/// <summary>A list of overlay operations read on their own (backlog 215): the operations, or every error found.</summary>
public sealed record PrototypeOperationsRead(IReadOnlyList<PrototypeOperation>? Operations, IReadOnlyList<PrototypeError> Errors)
{
    /// <summary>True when every operation passed every rule and there was at least one.</summary>
    public bool IsValid => Operations is not null;
}
