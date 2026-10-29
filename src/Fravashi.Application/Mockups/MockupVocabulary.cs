namespace Fravashi.Application.Mockups;

/// <summary>The fixed, product-neutral component palette of ADR 0021, decision 3.</summary>
public enum MockupComponent
{
    Window,
    Panel,
    Toolbar,
    List,
    Table,
    Form,
    Field,
    Button,

    /// <summary>
    /// Backlog 290: something a person follows to go somewhere, which a browser reports as role <c>link</c>, not
    /// <c>button</c>. Before it a design drew a link as a button, and the design check then looked for the wrong role.
    /// </summary>
    Link,
    Tabs,
    Tree,
    Dialog,
    Text,
    Image,
    Container,
}

/// <summary>How a container lays out its children.</summary>
public enum MockupLayout
{
    Row,
    Column,
    Grid,
}

/// <summary>What kind of input a field is.</summary>
public enum MockupInput
{
    Text,
    Password,
    Number,
    Email,
    Date,
    Search,
    Textarea,
    Select,
    Radio,
    Checkbox,
}

/// <summary>
/// The component states a design can name (#78: a verifier needs to know how a component looks when it is
/// disabled, loading, empty or in error, not only how it looks at rest). Fixed rather than free text, so a
/// verifier and an editor can both act on a name without guessing what an author meant by it.
/// </summary>
public enum MockupState
{
    Disabled,
    Loading,
    Empty,
    Error,
    Selected,
    Expanded,
    Hidden,
    Focused,
}

/// <summary>The events an interaction can hang off.</summary>
public enum MockupEvent
{
    Click,
    Submit,
    Change,
    Select,
}

/// <summary>
/// The names of the version 1 vocabulary as they are spelled in a tree, and which property belongs to which
/// component. One place, read by the reader, the writer and the schema tests, so the three cannot disagree
/// about a spelling.
/// </summary>
public static class MockupVocabulary
{
    /// <summary>The schema version this build reads and writes. A tree says which it is with <c>"mockup": 1</c>.</summary>
    public const int Version = 1;

    /// <summary>The longest an id, a viewport name or a data set name may be.</summary>
    public const int MaxNameLength = 64;

    /// <summary>How deep a tree may nest. A wireframe deeper than this is a mistake, or a loop an agent wrote.</summary>
    public const int MaxDepth = 32;

    /// <summary>How many nodes a document may hold, overrides included.</summary>
    public const int MaxNodes = 5000;

    /// <summary>The narrowest and widest a viewport may be, in CSS pixels.</summary>
    public const int MinViewportWidth = 200;

    /// <summary>The widest a viewport may be, in CSS pixels.</summary>
    public const int MaxViewportWidth = 7680;

    /// <summary>The properties a document's root may carry, in canonical order.</summary>
    public static IReadOnlyList<string> RootProperties { get; } = ["mockup", "title", "viewports", "data", "screens"];

    /// <summary>
    /// Every property a node may carry, in the canonical order the writer emits. Which component may carry
    /// which is <see cref="PropertiesOf"/>.
    /// </summary>
    public static IReadOnlyList<string> NodeProperties { get; } =
    [
        "type", "id", "text", "href", "layout", "cols", "input", "value", "placeholder", "required", "options",
        "data", "show", "columns", "items", "a11y", "state", "states", "at", "on", "children",
    ];

    /// <summary>The properties of an <c>a11y</c> object, in canonical order.</summary>
    public static IReadOnlyList<string> A11yProperties { get; } = ["label", "heading", "live", "decorative"];

    /// <summary>The properties of an action, in canonical order.</summary>
    public static IReadOnlyList<string> ActionProperties { get; } = ["go", "open", "close", "set"];

    /// <summary>The properties of a viewport, in canonical order.</summary>
    public static IReadOnlyList<string> ViewportProperties { get; } = ["name", "width"];

    /// <summary>The values <c>a11y.live</c> may take.</summary>
    public static IReadOnlyList<string> LiveValues { get; } = ["polite", "assertive"];

    /// <summary>The value of a <c>set</c> action that returns a node to how it looks at rest.</summary>
    public const string DefaultState = "default";

    /// <summary>Properties no override may carry: an override changes how a node looks, never what it is.</summary>
    public static IReadOnlySet<string> NotInOverride { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "type", "id", "state", "states", "at", "on" };

    /// <summary>Components that hold children.</summary>
    public static IReadOnlySet<MockupComponent> Containers { get; } = new HashSet<MockupComponent>
    {
        MockupComponent.Window, MockupComponent.Panel, MockupComponent.Toolbar, MockupComponent.Form,
        MockupComponent.Tabs, MockupComponent.Dialog, MockupComponent.Container,
    };

    private static readonly string[] Common = ["type", "id", "text", "a11y", "state", "states", "at", "on"];
    private static readonly string[] Laid = ["layout", "cols", "children"];

    private static readonly IReadOnlyDictionary<MockupComponent, IReadOnlySet<string>> Properties =
        new Dictionary<MockupComponent, IReadOnlySet<string>>
        {
            [MockupComponent.Window] = Set(Laid),
            [MockupComponent.Panel] = Set(Laid),
            [MockupComponent.Toolbar] = Set(Laid),
            [MockupComponent.Form] = Set(Laid),
            [MockupComponent.Dialog] = Set(Laid),
            [MockupComponent.Container] = Set(Laid),
            [MockupComponent.Tabs] = Set(["children"]),
            [MockupComponent.Field] = Set(["input", "value", "placeholder", "required", "options"]),
            [MockupComponent.List] = Set(["items", "data", "show"]),
            [MockupComponent.Tree] = Set(["items", "data", "show"]),
            [MockupComponent.Table] = Set(["items", "data", "columns"]),
            [MockupComponent.Button] = Set([]),
            [MockupComponent.Link] = Set(["href"]),
            [MockupComponent.Text] = Set([]),
            [MockupComponent.Image] = Set([]),
        };

    private static readonly IReadOnlyDictionary<MockupEvent, IReadOnlySet<MockupComponent>> EventTargets =
        new Dictionary<MockupEvent, IReadOnlySet<MockupComponent>>
        {
            [MockupEvent.Click] = Enum.GetValues<MockupComponent>()
                .Where(c => c is not (MockupComponent.Window or MockupComponent.Dialog or MockupComponent.Form))
                .ToHashSet(),
            [MockupEvent.Submit] = new HashSet<MockupComponent> { MockupComponent.Form },
            [MockupEvent.Change] = new HashSet<MockupComponent> { MockupComponent.Field },
            [MockupEvent.Select] = new HashSet<MockupComponent>
            {
                MockupComponent.List, MockupComponent.Table, MockupComponent.Tree, MockupComponent.Tabs,
            },
        };

    /// <summary>The properties a component may carry. Every component has an entry.</summary>
    public static IReadOnlySet<string> PropertiesOf(MockupComponent component) => Properties[component];

    /// <summary>Whether a component can raise an event.</summary>
    public static bool Raises(MockupComponent component, MockupEvent @event) =>
        EventTargets[@event].Contains(component);

    /// <summary>The components that can raise an event, for a message that says what would be allowed.</summary>
    public static IEnumerable<MockupComponent> Raising(MockupEvent @event) =>
        Enum.GetValues<MockupComponent>().Where(c => EventTargets[@event].Contains(c));

    /// <summary>The spelling of an enum value in a tree: its name in lower case.</summary>
    public static string Name<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();

    /// <summary>Every spelling of an enum's values, in declaration order.</summary>
    public static IReadOnlyList<string> Names<T>()
        where T : struct, Enum => Enum.GetValues<T>().Select(Name).ToArray();

    /// <summary>Reads a spelling back, exactly: <c>Button</c> is not <c>button</c>.</summary>
    public static bool TryParse<T>(string? text, out T value)
        where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(Name(candidate), text, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Whether a name can be an id, a viewport name or a data set name: a letter, then letters, digits,
    /// hyphens or underscores. Kept to what is safe as an HTML id and a CSS class, because #53 renders them.
    /// </summary>
    public static bool IsName(string? text) =>
        !string.IsNullOrEmpty(text)
        && text.Length <= MaxNameLength
        && char.IsAsciiLetter(text[0])
        && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static HashSet<string> Set(IEnumerable<string> specific) =>
        new(Common.Concat(specific), StringComparer.Ordinal);
}
