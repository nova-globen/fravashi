using Fravashi.Application.Mockups;

namespace Fravashi.Application.Verification;

/// <summary>
/// The role one part of a design has in a browser's accessibility tree, and whether a built page is asked to show it.
/// </summary>
/// <param name="Role">
/// The ARIA role a browser reports for what <see cref="Prototypes.PrototypeRenderer"/> emits for this part, spelled as
/// Playwright's role engine and Chromium spell it.
/// </param>
/// <param name="Asked">
/// True when a built page must show an element of this role (ADR 0030 decision 2). False for the parts that give order
/// and containment only (spike @44): a built page's toolbar or panel may be any element at all.
/// </param>
public sealed record DesignRole(string Role, bool Asked);

/// <summary>
/// The ONE component-to-role table of ADR 0030 decision 2 (backlog 221, plan @45). <see cref="DesignExpectations"/>
/// reads roles from here and nowhere else. The renderer does not read it: a role is what a browser DERIVES from markup,
/// so <c>DesignRolesTests</c> pins this table to the renderer's real output instead, deriving each role from the
/// emitted tag and attributes, and fails for any component, input or part where the two disagree. Spike @44's first run
/// was wrong three times here (number, radio, later tabs), which is why it is held once and pinned.
/// </summary>
public static class DesignRoles
{
    /// <summary>A screen's text, which the renderer writes as an <c>h1</c>.</summary>
    public static DesignRole ScreenTitle { get; } = new("heading", true);

    /// <summary>A text node with <c>a11y.heading</c>, written as <c>h1</c> to <c>h6</c>.</summary>
    public static DesignRole Heading { get; } = new("heading", true);

    /// <summary>The tab a tabs node shows for each of its panels.</summary>
    public static DesignRole Tab { get; } = new("tab", true);

    /// <summary>A panel that is a tab's content. Not asked: @44 asks the tab, not the panel.</summary>
    public static DesignRole TabPanel { get; } = new("tabpanel", false);

    /// <summary>A table column's heading. Asked, unlike its rows, which are sample data.</summary>
    public static DesignRole ColumnHeader { get; } = new("columnheader", true);

    /// <summary>
    /// Each component's own element. A field's entry is its default input's (text); <see cref="Inputs"/> has every
    /// input. A plain text node is a paragraph and not asked as a role: its WORDS are (plan @45), because a build may
    /// put words in any element. A form is unnamed unless it carries <c>a11y.label</c>, and an unnamed form is generic.
    /// </summary>
    public static IReadOnlyDictionary<MockupComponent, DesignRole> Components { get; } =
        new Dictionary<MockupComponent, DesignRole>
        {
            [MockupComponent.Window] = new("region", false),
            [MockupComponent.Panel] = new("generic", false),
            [MockupComponent.Toolbar] = new("toolbar", false),
            [MockupComponent.List] = new("list", true),
            [MockupComponent.Table] = new("table", true),
            [MockupComponent.Form] = new("generic", false),
            [MockupComponent.Field] = new("textbox", true),
            [MockupComponent.Button] = new("button", true),

            // Backlog 290: an a WITH an href is a link; without one it is generic, so the renderer always writes one.
            [MockupComponent.Link] = new("link", true),
            [MockupComponent.Tabs] = new("generic", false),
            [MockupComponent.Tree] = new("tree", true),
            [MockupComponent.Dialog] = new("dialog", true),
            [MockupComponent.Text] = new("paragraph", false),
            [MockupComponent.Image] = new("img", true),
            [MockupComponent.Container] = new("generic", false),
        };

    /// <summary>
    /// The control each field input renders as. A radio field is a <c>fieldset</c>, so a group; password and date are
    /// textboxes, as Playwright's role engine reports them.
    /// </summary>
    public static IReadOnlyDictionary<MockupInput, DesignRole> Inputs { get; } =
        new Dictionary<MockupInput, DesignRole>
        {
            [MockupInput.Text] = new("textbox", true),
            [MockupInput.Password] = new("textbox", true),
            [MockupInput.Number] = new("spinbutton", true),
            [MockupInput.Email] = new("textbox", true),
            [MockupInput.Date] = new("textbox", true),
            [MockupInput.Search] = new("searchbox", true),
            [MockupInput.Textarea] = new("textbox", true),
            [MockupInput.Select] = new("combobox", true),
            [MockupInput.Radio] = new("group", true),
            [MockupInput.Checkbox] = new("checkbox", true),
        };

    /// <summary>
    /// The role of a node's own element: its input's for a field, a heading for a text node with a heading level,
    /// otherwise its component's.
    /// </summary>
    public static DesignRole Of(MockupNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Type switch
        {
            MockupComponent.Field => Inputs[node.Input ?? MockupInput.Text],
            MockupComponent.Text when node.A11y?.Heading is >= 1 and <= 6 => Heading,
            _ => Components[node.Type],
        };
    }
}
