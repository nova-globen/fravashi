using System.Text.Json;

namespace Fravashi.Application.Mockups;

/// <summary>
/// A mockup: the canonical declarative UI-tree of ADR 0021, version <see cref="MockupVocabulary.Version"/>.
/// Every editing surface (the editor of #51, an agent, the HTML renderer of #53) is a projection over this,
/// and the verifier (#56) reads it as the approved design. Only <see cref="MockupReader"/> makes one from
/// text, so a document that exists is one that passed every rule.
/// </summary>
/// <param name="Version">The schema version the document was written in.</param>
/// <param name="Title">What the mockup is of.</param>
/// <param name="Viewports">The widths the design is specified at; a node's <see cref="MockupNode.At"/> names them.</param>
/// <param name="Data">Named sample data sets, in the order they were written.</param>
/// <param name="Screens">The top-level windows; <c>go</c> navigates between them.</param>
public sealed record MockupDocument(
    int Version,
    string? Title,
    IReadOnlyList<MockupViewport> Viewports,
    IReadOnlyList<MockupDataSet> Data,
    IReadOnlyList<MockupNode> Screens);

/// <summary>A width the design is specified at (#78: responsive variants).</summary>
public sealed record MockupViewport(string Name, int Width);

/// <summary>
/// A named set of sample records a list, table or tree shows (#78: sample data). Each record is a JSON
/// object, kept as it was written.
/// </summary>
public sealed record MockupDataSet(string Name, IReadOnlyList<JsonElement> Records);

/// <summary>A table column: the record key it shows and its heading.</summary>
public sealed record MockupColumn(string Key, string? Text);

/// <summary>What a node means to assistive technology (#78: accessibility intent).</summary>
/// <param name="Label">The accessible name, where the visible text is not one.</param>
/// <param name="Heading">The heading level, 1 to 6, when the node is a heading.</param>
/// <param name="Live">How a change to it is announced: polite or assertive.</param>
/// <param name="Decorative">True when it carries no information and assistive technology should skip it.</param>
public sealed record MockupA11y(string? Label, int? Heading, string? Live, bool? Decorative);

/// <summary>
/// What an event does (#78: interactions). Any combination of the four, at least one.
/// </summary>
/// <param name="Go">The id of a screen to navigate to.</param>
/// <param name="Open">The id of a dialog to open.</param>
/// <param name="Close">The id of a dialog to close.</param>
/// <param name="Set">Node id to state name: each node is shown in that state (or <c>default</c>).</param>
public sealed record MockupAction(
    string? Go,
    string? Open,
    string? Close,
    IReadOnlyDictionary<string, string>? Set);

/// <summary>
/// One component of the tree. A property a component does not carry is null. The same record is used for an
/// override (a <see cref="States"/> or <see cref="At"/> value), whose <see cref="Type"/> is its owner's and
/// whose other properties are only the ones it changes.
/// </summary>
public sealed record MockupNode
{
    /// <summary>Which palette component it is.</summary>
    public required MockupComponent Type { get; init; }

    /// <summary>Unique in the document; what interactions and a verifier name it by.</summary>
    public string? Id { get; init; }

    /// <summary>Its visible text: a title, a label, a caption, the words of a text node.</summary>
    public string? Text { get; init; }

    /// <summary>
    /// Where a link goes (backlog 290), as the design writes it. Carried for a reader only: the prototype never makes
    /// it live and the design check never compares it, because a design's target is usually a placeholder.
    /// </summary>
    public string? Href { get; init; }

    /// <summary>How a container lays out its children.</summary>
    public MockupLayout? Layout { get; init; }

    /// <summary>Columns of a grid layout.</summary>
    public int? Cols { get; init; }

    /// <summary>A field's input kind; a field without one is a text input.</summary>
    public MockupInput? Input { get; init; }

    /// <summary>A field's sample value: a string, a number or a boolean.</summary>
    public JsonElement? Value { get; init; }

    /// <summary>A field's placeholder.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Whether a field must be filled in.</summary>
    public bool? Required { get; init; }

    /// <summary>The choices of a select or radio field.</summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>The name of the sample data set a list, table or tree shows.</summary>
    public string? Data { get; init; }

    /// <summary>The record key a list or tree shows from its data set.</summary>
    public string? Show { get; init; }

    /// <summary>A table's columns.</summary>
    public IReadOnlyList<MockupColumn>? Columns { get; init; }

    /// <summary>
    /// Inline items, kept as written: strings or <c>{"text", "items"}</c> objects for a list or tree, record
    /// objects for a table.
    /// </summary>
    public IReadOnlyList<JsonElement>? Items { get; init; }

    /// <summary>Accessibility intent.</summary>
    public MockupA11y? A11y { get; init; }

    /// <summary>The state it is shown in at first, one of its own <see cref="States"/>.</summary>
    public MockupState? State { get; init; }

    /// <summary>How it looks in each named state.</summary>
    public IReadOnlyDictionary<MockupState, MockupNode>? States { get; init; }

    /// <summary>How it looks at each declared viewport.</summary>
    public IReadOnlyDictionary<string, MockupNode>? At { get; init; }

    /// <summary>What each event it raises does.</summary>
    public IReadOnlyDictionary<MockupEvent, MockupAction>? On { get; init; }

    /// <summary>A container's children, in order.</summary>
    public IReadOnlyList<MockupNode>? Children { get; init; }
}

/// <summary>One thing wrong with a tree.</summary>
/// <param name="Path">Where, as a JSON path from the root (<c>$.screens[0].children[2].on.click.go</c>).</param>
/// <param name="Message">What is wrong and what would be allowed instead.</param>
public sealed record MockupError(string Path, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>A tree read: the document when it is valid, otherwise every error found.</summary>
public sealed record MockupReadResult(MockupDocument? Document, IReadOnlyList<MockupError> Errors)
{
    /// <summary>True when the tree passed every rule.</summary>
    public bool IsValid => Document is not null;
}
