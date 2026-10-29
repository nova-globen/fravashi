using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fravashi.Application.Mockups;
using Fravashi.Application.Verification;

namespace Fravashi.Application.Prototypes;

/// <summary>
/// What rendering a prototype produced: the standalone page, the overlay operations whose anchor the
/// renderer never emitted (ADR 0021 decision 6: "reports anything it can no longer place, rather than
/// silently discarding a user's work"), and the <c>at</c> overrides this slice has no visual mechanism for
/// at all (see <see cref="PrototypeUnexpressedOverride"/>) - a different kind of gap than an unplaced
/// overlay operation, but the same rule: say so, never just do nothing.
/// </summary>
/// <param name="Html">The whole standalone HTML document.</param>
/// <param name="Unplaced">Overlay operations, in their original order, whose anchor matched no node.</param>
/// <param name="UnexpressedOverrides">
/// Every <c>at</c> override property, in tree order, that no node in the tree ever got a CSS/DOM mechanism
/// for.
/// </param>
public sealed record PrototypeRenderResult(
    string Html, IReadOnlyList<PrototypeOperation> Unplaced, IReadOnlyList<PrototypeUnexpressedOverride> UnexpressedOverrides)
{
    /// <summary>
    /// Backlog 214 (plan @39): every anchor this render EMITTED, by its <c>data-mk-anchor</c> key, the first element
    /// carrying a key winning as it does in the page's script. The studio accepts a selection from the page only for
    /// a key in here, so a name the page invents, or one left over from an earlier render, selects nothing.
    /// </summary>
    public IReadOnlyDictionary<string, PrototypeRenderedAnchor> Anchors { get; init; } =
        new Dictionary<string, PrototypeRenderedAnchor>(StringComparer.Ordinal);

    /// <summary>
    /// Backlog 238 (plan @52): every PART this render emitted (a title, a caption, a tab, a column header), by its node's
    /// key, the part and the column, the first winning. A selection of a part from the page is accepted only for one in
    /// here, as a node's is only for a key in <see cref="Anchors"/>.
    /// </summary>
    public IReadOnlyDictionary<PrototypePartKey, PrototypeRenderedAnchor> Parts { get; init; } =
        new Dictionary<PrototypePartKey, PrototypeRenderedAnchor>();

    /// <summary>The node (no part) or the part this render emitted under that key, or null when it emitted none.</summary>
    public PrototypeRenderedAnchor? Find(string key, DesignPart part = DesignPart.Node, string? column = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (part == DesignPart.Node)
        {
            return column is null && Anchors.TryGetValue(key, out var node) ? node : null;
        }

        return Parts.TryGetValue(new PrototypePartKey(key, part, column), out var found) ? found : null;
    }
}

/// <summary>A part a render emitted, by its node's <c>data-mk-anchor</c> key, the part, and a column header's column.</summary>
public sealed record PrototypePartKey(string Key, DesignPart Part, string? Column);

/// <summary>One anchor a render emitted, as the edit panel needs it (backlog 214).</summary>
/// <param name="Key">The <c>data-mk-anchor</c> value: the node's id, or its tree path when it has none.</param>
/// <param name="Anchor">
/// The overlay anchor the key means, which is what an edit of this node is written against; for a part (backlog 238) it
/// carries the part and column too.
/// </param>
/// <param name="Type">The node's component, spelled as the schema spells it.</param>
/// <param name="Text">The text the page shows for it at rest: the overlay's last <c>setText</c>, else the node's own.</param>
public sealed record PrototypeRenderedAnchor(string Key, PrototypeAnchor Anchor, string Type, string? Text);

/// <summary>How to render (backlog 213 and 214).</summary>
/// <param name="StartScreen">The screen shown first; the first one when this is out of range.</param>
/// <param name="Edit">
/// An edit render: the root carries <c>data-mk-edit="edit"</c>, which the SAME constant script reads to select by
/// anchor instead of acting, so the script, its hash and the policy are those of a view render (plan @39).
/// </param>
/// <param name="Selected">In an edit render, the emitted anchor key to outline as selected, or null.</param>
/// <param name="SelectedPart">Backlog 238: the part of <paramref name="Selected"/> that is selected, or the node itself.</param>
/// <param name="SelectedColumn">The selected column header's column, with <see cref="DesignPart.ColumnHeader"/>.</param>
public sealed record PrototypeRenderOptions(
    int StartScreen = 0, bool Edit = false, string? Selected = null,
    DesignPart SelectedPart = DesignPart.Node, string? SelectedColumn = null);

/// <summary>
/// One <c>at</c> or <c>states</c> override property the page renders no visual difference for (plan @36: only
/// <c>text</c>, <c>layout</c>/<c>cols</c> and a table's <c>columns</c> get a CSS mechanism; <c>MockupView.Apply</c>
/// also merges <c>href</c>, <c>input</c>, <c>value</c>, <c>placeholder</c>, <c>required</c>, <c>options</c>, <c>data</c>,
/// <c>show</c>, <c>items</c>, <c>a11y</c> and <c>children</c>, none of which the page expresses; plan @38 applies
/// the same line to a state's override). Reported rather than silently dropped, for the same reason an unplaced
/// overlay operation is. Exactly one of <paramref name="Viewport"/> and <paramref name="State"/> is set.
/// </summary>
/// <param name="Anchor">The node's own anchor: its id, or its tree path.</param>
/// <param name="Viewport">The named viewport whose override carries the property, or null for a state's.</param>
/// <param name="Property">The override's own property name, spelled as the schema spells it.</param>
/// <param name="State">The state whose override carries the property, or null for a viewport's.</param>
public sealed record PrototypeUnexpressedOverride(string Anchor, string? Viewport, string Property, string? State = null);

/// <summary>
/// Renders a <see cref="MockupDocument"/> plus an overlay to one standalone HTML page (ADR 0021 decision 6,
/// backlog 212). Deterministic, so an overlay survives regeneration unharmed: the same tree and overlay
/// always produce byte-identical output. Every element generated for a node carries <c>data-mk-anchor</c>
/// (its id, or its tree path spelled the way <see cref="PrototypeAnchor.Path"/> is, an RFC-6901 pointer
/// rooted at the document), so the overlay - and a later regeneration - can always find it again. The
/// overlay is applied DURING emission, at each node's own anchor, so no HTML parser is needed; an
/// operation whose anchor no node ever carried comes back in <see cref="PrototypeRenderResult.Unplaced"/>,
/// never silently dropped. See plan @36 for the per-component mapping and the reasons behind each choice.
/// <para>
/// Backlog 213 (plan @38): the page ACTS on the tree. What the tree says (each node's <c>on</c>, its starting
/// <c>state</c>, which dialogs something opens) is written as data on the elements, and ONE constant inline
/// script (<see cref="Script"/>) interprets it: go, open, close, set, tabs that switch. Because the script never
/// changes, its hash does not either, and the page's content security policy (<see cref="ContentSecurityPolicy"/>,
/// the first element of the head) admits that one script by hash and nothing else, so the output stays
/// byte-identical and an injected handler or script URL is refused by the browser whatever the guard missed.
/// </para>
/// <para>
/// Backlog 238 (plan @52): what a node draws for itself without an anchor of its own (a screen's or dialog's title, a
/// caption, a column header, a panel's tab, <see cref="PrototypeParts"/>) is a PART: its element carries
/// <c>data-mk-part-of</c> (the node's key) and <c>data-mk-part</c>, takes exactly the overlay entries that name that
/// part, and is listed in <see cref="PrototypeRenderResult.Parts"/>. A node's own element takes only part-less entries.
/// </para>
/// </summary>
public static class PrototypeRenderer
{
    private const string AnchorAttribute = "data-mk-anchor";
    private const string ColumnAttribute = "data-mk-col";
    private const string TextOfAttribute = "data-mk-text-of";

    // Backlog 238 (plan @52): a part's element names its node and the part. Never data-mk-anchor: the script and every
    // per-node rule find a node by that, the first element winning, and a tab button is written before its panel.
    private const string PartOfAttribute = "data-mk-part-of";
    private const string PartAttribute = "data-mk-part";

    /// <summary>
    /// The page's one script: a fixed interpreter of the <c>data-mk-</c> attributes the renderer writes. It finds
    /// every target by <c>data-mk-anchor</c> (go, open, close and set name ids, and a node with an id is anchored by
    /// it), so it needs no ids of its own and builds no selector from tree text. It never contains a closing script
    /// tag, and it is the text between the page's <c>&lt;script&gt;</c> and <c>&lt;/script&gt;</c> exactly, which is
    /// what <see cref="ScriptHash"/> hashes.
    /// <para>
    /// Backlog 214 (plan @39): when the root carries <c>data-mk-edit="edit"</c> it wires NOTHING else; one capturing
    /// click listener cancels every click and posts <c>{mk: "select", anchor}</c> with the nearest anchor to the
    /// parent. The mode reaches the script as data, so an edit render carries these same bytes and this same hash.
    /// The target origin is <c>"*"</c> because an opaque-origin frame with no referrer cannot name its parent's; the
    /// message carries only an anchor the page itself shows, and the studio's listener checks the source, the shape
    /// and that the anchor was emitted.
    /// </para>
    /// <para>
    /// Backlog 238 (plan @52): the nearest element is a PART when it carries <c>data-mk-part-of</c> (a title, a caption,
    /// a tab, a column header), and then the message is <c>{mk: "select", anchor, part}</c>, with <c>column</c> added for
    /// a column header. The studio accepts a part only when the render emitted that very part.
    /// </para>
    /// </summary>
    public const string Script = """
        (function () {
          "use strict";
          if (document.documentElement.getAttribute("data-mk-edit") === "edit") {
            document.addEventListener("click", function (e) {
              e.preventDefault();
              e.stopPropagation();
              var start = e.target && e.target.nodeType === 1 ? e.target : e.target && e.target.parentElement;
              var picked = start ? start.closest("[data-mk-part-of], [data-mk-anchor]") : null;
              if (picked) {
                var message = { mk: "select", anchor: picked.getAttribute("data-mk-anchor") };
                if (picked.hasAttribute("data-mk-part-of")) {
                  message.anchor = picked.getAttribute("data-mk-part-of");
                  message.part = picked.getAttribute("data-mk-part");
                  if (message.part === "columnHeader") { message.column = picked.getAttribute("data-mk-col"); }
                }
                window.parent.postMessage(message, "*");
              }
            }, true);
            return;
          }
          var own = Object.prototype.hasOwnProperty;
          var anchors = {};
          var all = document.querySelectorAll("[data-mk-anchor]");
          for (var i = 0; i < all.length; i++) {
            var key = all[i].getAttribute("data-mk-anchor");
            if (!own.call(anchors, key)) { anchors[key] = all[i]; }
          }
          function find(id) { return typeof id === "string" && own.call(anchors, id) ? anchors[id] : null; }
          function actions(el) {
            if (!el || !el.hasAttribute || !el.hasAttribute("data-mk-on")) { return null; }
            try { return JSON.parse(el.getAttribute("data-mk-on")); } catch (e) { return null; }
          }
          function nearest(start, event) {
            for (var el = start; el && el.nodeType === 1; el = el.parentElement) {
              var on = actions(el);
              if (on && on[event]) { return { el: el, action: on[event] }; }
            }
            return null;
          }
          function setState(el, state) {
            var drop = [];
            for (var i = 0; i < el.classList.length; i++) {
              if (el.classList[i].indexOf("mk-state-") === 0) { drop.push(el.classList[i]); }
            }
            for (var j = 0; j < drop.length; j++) { el.classList.remove(drop[j]); }
            if (state !== "default") { el.classList.add("mk-state-" + state); }
          }
          function run(action) {
            if (!action) { return; }
            var screen = find(action.go);
            if (screen && screen.hasAttribute("data-mk-screen")) {
              var screens = document.querySelectorAll("[data-mk-screen]");
              for (var i = 0; i < screens.length; i++) { screens[i].hidden = screens[i] !== screen; }
            }
            var opened = find(action.open);
            if (opened && opened.tagName === "DIALOG") { opened.setAttribute("open", ""); }
            var closed = find(action.close);
            if (closed && closed.tagName === "DIALOG") { closed.removeAttribute("open"); }
            if (action.set && typeof action.set === "object") {
              for (var id in action.set) {
                var target = own.call(action.set, id) ? find(id) : null;
                if (target) { setState(target, String(action.set[id])); }
              }
            }
          }
          function submit(form) {
            var on = actions(form);
            if (on && on.submit) { run(on.submit); }
          }
          function chooseTab(tab) {
            var tabs = tab.closest("[data-mk-tabs]");
            if (!tabs) { return; }
            var index = parseInt(tab.getAttribute("data-mk-tab"), 10);
            var panel = 0;
            for (var i = 0; i < tabs.children.length; i++) {
              if (tabs.children[i].getAttribute("role") === "tabpanel") {
                tabs.children[i].hidden = panel !== index;
                panel++;
              }
            }
            var buttons = tabs.querySelectorAll("[data-mk-tab]");
            for (var j = 0; j < buttons.length; j++) {
              if (buttons[j].closest("[data-mk-tabs]") === tabs) {
                buttons[j].setAttribute("aria-selected", buttons[j] === tab ? "true" : "false");
              }
            }
          }
          document.addEventListener("click", function (e) {
            var target = e.target && e.target.nodeType === 1 ? e.target : e.target && e.target.parentElement;
            if (!target) { return; }
            if (target.closest("a[href]")) { e.preventDefault(); }
            var clicked = nearest(target, "click");
            if (clicked) { run(clicked.action); }
            var tab = target.closest("[data-mk-tab]");
            if (tab) { chooseTab(tab); }
            var row = tab || target.closest("li:not(.mk-caption), tbody tr");
            var owner = row ? row.closest("[data-mk-select]") : null;
            var chosen = owner ? actions(owner) : null;
            if (chosen && chosen.select) { run(chosen.select); }
            var button = target.closest("button");
            var form = button && !tab ? button.closest("form[data-mk-anchor]") : null;
            if (form && !(clicked && clicked.el === button)) { submit(form); }
          });
          document.addEventListener("change", function (e) {
            var changed = e.target && e.target.nodeType === 1 ? nearest(e.target, "change") : null;
            if (changed) { run(changed.action); }
          });
          document.addEventListener("keydown", function (e) {
            if (e.key !== "Enter" || !e.target || e.target.tagName !== "INPUT") { return; }
            var form = e.target.closest("form[data-mk-anchor]");
            if (form) { e.preventDefault(); submit(form); }
          });
        })();
        """;

    /// <summary>The CSP source that admits <see cref="Script"/> and nothing else: its SHA-256, base64.</summary>
    public static string ScriptHash { get; } =
        "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Script)));

    /// <summary>
    /// The page's content security policy, written as the first element of its head AND given to the viewer's
    /// frame as its <c>csp</c> attribute (plan @38). Nothing loads from anywhere (<c>default-src 'none'</c>), the one
    /// script runs by its hash and no other script, handler or script URL runs at all (no <c>'unsafe-inline'</c> for
    /// scripts), styles are the page's own, images are data only, and neither a base URL nor a form target can be
    /// set (<c>base-uri</c> and <c>form-action</c> do not fall back to <c>default-src</c>, so they are named).
    /// </summary>
    public static string ContentSecurityPolicy { get; } =
        $"default-src 'none'; script-src '{ScriptHash}'; style-src 'unsafe-inline'; img-src data:; "
        + "base-uri 'none'; form-action 'none'";

    /// <summary>Renders <paramref name="document"/> with <paramref name="overlay"/> applied, starting on its first screen.</summary>
    public static PrototypeRenderResult Render(MockupDocument document, IReadOnlyList<PrototypeOperation> overlay) =>
        Render(document, overlay, 0);

    /// <summary>
    /// Renders <paramref name="document"/> with <paramref name="overlay"/> applied, showing the screen at
    /// <paramref name="startScreen"/> first (the first one when that is out of range). The viewer chooses a screen
    /// this way because its frame has an opaque origin and cannot be scripted from outside.
    /// </summary>
    public static PrototypeRenderResult Render(
        MockupDocument document, IReadOnlyList<PrototypeOperation> overlay, int startScreen) =>
        Render(document, overlay, new PrototypeRenderOptions(startScreen));

    /// <summary>
    /// Renders <paramref name="document"/> with <paramref name="overlay"/> applied as <paramref name="options"/> say.
    /// An edit render differs from a view render ONLY in the root's <c>data-mk-edit</c> attribute and two outline
    /// rules at the end of the stylesheet: the script and the policy are the same bytes (backlog 214, plan @39).
    /// </summary>
    public static PrototypeRenderResult Render(
        MockupDocument document, IReadOnlyList<PrototypeOperation> overlay, PrototypeRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(options);

        var start = options.StartScreen >= 0 && options.StartScreen < document.Screens.Count ? options.StartScreen : 0;
        return new Pass(document, overlay, start, options).Run();
    }

    /// <summary>The anchor an operation names, or null for a page rule, which has none.</summary>
    public static PrototypeAnchor? AnchorOf(PrototypeOperation operation) => operation switch
    {
        PrototypeSetTextOperation op => op.Anchor,
        PrototypeSetCssOperation op => op.Anchor,
        PrototypeSetAttributeOperation op => op.Anchor,
        PrototypeAddClassOperation op => op.Anchor,
        PrototypeInsertHtmlOperation op => op.Anchor,
        _ => null,
    };

    /// <summary>One render. Holds the overlay index and which of its entries were placed.</summary>
    private sealed class Pass
    {
        private readonly MockupDocument document;
        private readonly IReadOnlyList<PrototypeOperation> overlay;
        private readonly Dictionary<string, List<int>> byId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<int>> byPath = new(StringComparer.Ordinal);
        private readonly HashSet<int> placed = [];
        private readonly StringBuilder body = new();
        private readonly List<string> styleRules = [];
        private readonly List<string> pageCssRules = [];
        private readonly List<PrototypeUnexpressedOverride> unexpressedOverrides = [];
        private readonly Dictionary<string, string> viewportBands;
        private readonly int startScreen;
        private readonly bool edit;
        private readonly string? selected;
        private readonly DesignPart selectedPart;
        private readonly string? selectedColumn;
        private readonly Dictionary<string, PrototypeRenderedAnchor> anchors = new(StringComparer.Ordinal);
        private readonly Dictionary<PrototypePartKey, PrototypeRenderedAnchor> parts = [];

        /// <summary>The ids of the dialogs some action opens: those start closed, every other dialog open.</summary>
        private readonly HashSet<string> openedDialogs = new(StringComparer.Ordinal);

        public Pass(
            MockupDocument document, IReadOnlyList<PrototypeOperation> overlay, int startScreen, PrototypeRenderOptions options)
        {
            this.document = document;
            this.overlay = overlay;
            this.startScreen = startScreen;
            edit = options.Edit;
            selected = options.Selected;
            selectedPart = options.SelectedPart;
            selectedColumn = options.SelectedColumn;
            viewportBands = ViewportBands(document.Viewports);

            for (var s = 0; s < document.Screens.Count; s++)
            {
                foreach (var place in MockupView.Walk(document.Screens[s], MockupView.ScreenPath(s)))
                {
                    foreach (var action in place.Node.On?.Values ?? [])
                    {
                        if (action.Open is { } opened)
                        {
                            openedDialogs.Add(opened);
                        }
                    }
                }
            }

            for (var i = 0; i < overlay.Count; i++)
            {
                if (overlay[i] is PrototypePageCssOperation pageCss)
                {
                    pageCssRules.Add(pageCss.Rule);
                    placed.Add(i);
                }
                else if (AnchorOf(overlay[i]) is { Id: { } id })
                {
                    Bucket(byId, id).Add(i);
                }
                else if (AnchorOf(overlay[i]) is { Path: { } path })
                {
                    Bucket(byPath, path).Add(i);
                }
            }
        }

        public PrototypeRenderResult Run()
        {
            for (var i = 0; i < document.Screens.Count; i++)
            {
                Screen(document.Screens[i], $"/screens/{i.ToString(CultureInfo.InvariantCulture)}", i == startScreen);
            }

            var html = Page();
            var unplaced = new List<PrototypeOperation>();
            for (var i = 0; i < overlay.Count; i++)
            {
                if (!placed.Contains(i))
                {
                    unplaced.Add(overlay[i]);
                }
            }

            return new PrototypeRenderResult(html, unplaced, unexpressedOverrides) { Anchors = anchors, Parts = parts };
        }

        // ---- the tree walk --------------------------------------------------------------------------

        private void Screen(MockupNode node, string path, bool shown)
        {
            var key = node.Id ?? path;
            var element = Begin(node, path, "section", ["mk-screen"]);
            element.Attrs.Add(("aria-label", node.Text ?? MockupVocabulary.Name(node.Type)));

            // Backlog 213: one screen at a time, as go moves between them; the script finds them by this.
            element.Attrs.Add(("data-mk-screen", "screen"));
            if (!shown)
            {
                element.Attrs.Add(("hidden", "hidden"));
            }
            LayoutCss(element, node, key);

            Open(element);
            Heading(element, "h1");
            Children(node, path);
            Close(element);
        }

        /// <summary>
        /// A screen's or dialog's title (backlog 238): its own element, so an operation on the title part reaches it and
        /// nothing else, and one on the node no longer reaches it, except the node's <c>setText</c>, whose text the title
        /// IS (a <c>setText</c> on the title wins over it).
        /// </summary>
        private void Heading(Element owner, string tag)
        {
            var title = BeginPart(owner.Node, owner.Path, tag, DesignPart.Title, null, [], []);
            title.TextOverride ??= owner.TextOverride;
            if (HasText(title, owner.Node))
            {
                OpenPart(title, title.TextOverride ?? owner.Node.Text);
                RenderText(title, owner.Node, owner.Key);
                Close(title);
            }
        }

        /// <summary>A table's, list's or tree's caption (backlog 238), drawn as <paramref name="tag"/>, as a title is.</summary>
        private void CaptionPart(Element owner, string tag, IReadOnlyList<string> classes)
        {
            var caption = BeginPart(owner.Node, owner.Path, tag, DesignPart.Caption, null, classes, []);
            caption.TextOverride ??= owner.TextOverride;
            if (HasText(caption, owner.Node))
            {
                OpenPart(caption, caption.TextOverride ?? owner.Node.Text);
                RenderText(caption, owner.Node, owner.Key);
                Close(caption);
            }
        }

        private void Node(MockupNode node, string path)
        {
            switch (node.Type)
            {
                case MockupComponent.Panel:
                case MockupComponent.Container:
                    Container(node, path, "div", node.Type == MockupComponent.Panel ? "mk-panel" : "mk-container");
                    break;
                case MockupComponent.Toolbar:
                    Container(node, path, "div", "mk-toolbar", [("role", "toolbar")]);
                    break;
                case MockupComponent.Form:
                    Container(node, path, "form", "mk-form");
                    break;
                case MockupComponent.Dialog:
                    Dialog(node, path);
                    break;
                case MockupComponent.Tabs:
                    Tabs(node, path);
                    break;
                case MockupComponent.List:
                    ListNode(node, path);
                    break;
                case MockupComponent.Tree:
                    TreeNode(node, path);
                    break;
                case MockupComponent.Table:
                    TableNode(node, path);
                    break;
                case MockupComponent.Field:
                    Field(node, path);
                    break;
                case MockupComponent.Button:
                    ButtonNode(node, path);
                    break;
                case MockupComponent.Link:
                    LinkNode(node, path);
                    break;
                case MockupComponent.Text:
                    TextNode(node, path);
                    break;
                case MockupComponent.Image:
                    ImageNode(node, path);
                    break;
                default:
                    Container(node, path, "div", "mk-node");
                    break;
            }
        }

        private void Children(MockupNode node, string path)
        {
            if (node.Children is not { } children)
            {
                return;
            }

            for (var i = 0; i < children.Count; i++)
            {
                Node(children[i], $"{path}/children/{i.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        // ---- per-component rendering -----------------------------------------------------------------

        private void Container(
            MockupNode node, string path, string tag, string baseClass, IReadOnlyList<(string, string)>? attrs = null)
        {
            var key = node.Id ?? path;
            var element = Begin(node, path, tag, [baseClass]);
            if (attrs is not null)
            {
                element.Attrs.AddRange(attrs);
            }

            A11y(element, node);
            LayoutCss(element, node, key);
            Open(element);
            if (HasText(element, node))
            {
                body.Append("<div class=\"mk-label\">");
                RenderText(element, node, key);
                body.Append("</div>");
            }

            Children(node, path);
            Close(element);
        }

        private void Dialog(MockupNode node, string path)
        {
            var element = Begin(node, path, "dialog", ["mk-dialog"]);

            // A <dialog> with no "open" attribute is display: none in every browser's own stylesheet. One that
            // some action opens starts closed, as the tree says (backlog 213); one that nothing opens stays open,
            // a non-modal open one rather than an invisible one nobody could ever reach.
            if (node.Id is not { } id || !openedDialogs.Contains(id))
            {
                element.Attrs.Add(("open", "open"));
            }
            var label = node.A11y?.Label ?? node.Text;
            if (label is not null)
            {
                element.Attrs.Add(("aria-label", label));
            }

            A11y(element, node);
            Open(element);
            Heading(element, "h2");
            Children(node, path);
            Close(element);
        }

        private void Tabs(MockupNode node, string path)
        {
            var element = Begin(node, path, "div", ["mk-tabs"]);

            // Backlog 213: tabs switch, the first panel shown at start; the script finds them by these.
            element.Attrs.Add(("data-mk-tabs", "tabs"));
            element.Attrs.Add(("data-mk-select", "tabs"));
            Open(element);

            var panels = node.Children ?? [];
            body.Append("<div class=\"mk-tablist\" role=\"tablist\">");
            for (var i = 0; i < panels.Count; i++)
            {
                // Backlog 238: the tab is its PANEL's part, as #221's Tab expectation anchors it, written before the panel.
                var index = i.ToString(CultureInfo.InvariantCulture);
                var tab = BeginPart(
                    panels[i], $"{path}/children/{index}", "button", DesignPart.Tab, null, [],
                    [("type", "button"), ("role", "tab"), ("data-mk-tab", index), ("aria-selected", i == 0 ? "true" : "false")]);
                var label = tab.TextOverride ?? panels[i].Text ?? MockupVocabulary.Name(panels[i].Type);
                OpenPart(tab, label);
                body.Append(Html.Encode(label));
                Close(tab);
            }

            body.Append("</div>");

            for (var i = 0; i < panels.Count; i++)
            {
                var panel = panels[i];
                var panelPath = $"{path}/children/{i.ToString(CultureInfo.InvariantCulture)}";
                var panelKey = panel.Id ?? panelPath;
                var panelElement = Begin(panel, panelPath, "section", ["mk-panel"]);
                panelElement.Attrs.Add(("role", "tabpanel"));
                if (panel.Text is { } panelLabel)
                {
                    panelElement.Attrs.Add(("aria-label", panelLabel));
                }

                if (i > 0)
                {
                    panelElement.Attrs.Add(("hidden", "hidden"));
                }

                A11y(panelElement, panel);
                Open(panelElement);
                if (HasText(panelElement, panel))
                {
                    body.Append("<div class=\"mk-label\">");
                    RenderText(panelElement, panel, panelKey);
                    body.Append("</div>");
                }

                Children(panel, panelPath);
                Close(panelElement);
            }

            Close(element);
        }

        private void ListNode(MockupNode node, string path)
        {
            var element = Begin(node, path, "ul", ["mk-list"]);
            element.Attrs.Add(("data-mk-select", "rows"));
            A11y(element, node);
            Open(element);
            CaptionPart(element, "li", ["mk-caption"]);

            foreach (var row in MockupView.Rows(node, document))
            {
                body.Append("<li>").Append(Html.Encode(row.Text)).Append("</li>");
            }

            Close(element);
        }

        private void TreeNode(MockupNode node, string path)
        {
            var element = Begin(node, path, "ul", ["mk-tree"]);
            element.Attrs.Add(("role", "tree"));
            element.Attrs.Add(("data-mk-select", "rows"));
            A11y(element, node);
            Open(element);
            CaptionPart(element, "li", ["mk-caption"]);

            foreach (var row in MockupView.Rows(node, document))
            {
                TreeRow(row);
            }

            Close(element);
        }

        private void TreeRow(MockupRow row)
        {
            body.Append("<li role=\"treeitem\">").Append(Html.Encode(row.Text));
            if (row.Children.Count > 0)
            {
                body.Append("<ul role=\"group\">");
                foreach (var child in row.Children)
                {
                    TreeRow(child);
                }

                body.Append("</ul>");
            }

            body.Append("</li>");
        }

        private void TableNode(MockupNode node, string path)
        {
            var key = node.Id ?? path;
            var element = Begin(node, path, "table", ["mk-table"]);
            element.Attrs.Add(("data-mk-select", "rows"));
            A11y(element, node);

            var columns = node.Columns ?? [];
            if (node.At is { } at)
            {
                foreach (var viewport in document.Viewports)
                {
                    if (at.TryGetValue(viewport.Name, out var over)
                        && HiddenColumns(columns, over, $"[{AnchorAttribute}=\"{Selector(key)}\"]") is { } selector)
                    {
                        styleRules.Add($"@media {viewportBands[viewport.Name]} {{ {selector} {{ display: none; }} }}");
                    }
                }
            }

            foreach (var (state, over) in States(node))
            {
                if (HiddenColumns(columns, over, $"[{AnchorAttribute}=\"{Selector(key)}\"].{StateClass(state)}") is { } selector)
                {
                    styleRules.Add($"{selector} {{ display: none !important; }}");
                }
            }

            Open(element);
            CaptionPart(element, "caption", []);

            if (columns.Count > 0)
            {
                body.Append("<thead><tr>");
                foreach (var column in columns)
                {
                    // Backlog 238: each header is the table's columnHeader part for its column, with its own overlay.
                    var header = BeginPart(node, path, "th", DesignPart.ColumnHeader, column.Key, [], [(ColumnAttribute, column.Key)]);
                    var label = header.TextOverride ?? column.Text ?? column.Key;
                    OpenPart(header, label);
                    body.Append(Html.Encode(label));
                    Close(header);
                }

                body.Append("</tr></thead>");
            }

            body.Append("<tbody>");
            foreach (var record in MockupView.Records(node, document))
            {
                body.Append("<tr>");
                foreach (var column in columns)
                {
                    body.Append("<td ").Append(ColumnAttribute).Append("=\"").Append(Html.EncodeAttribute(column.Key))
                        .Append("\">").Append(Html.Encode(MockupView.Cell(record, column.Key))).Append("</td>");
                }

                body.Append("</tr>");
            }

            body.Append("</tbody>");
            Close(element);
        }

        private void Field(MockupNode node, string path)
        {
            var key = node.Id ?? path;
            var input = node.Input ?? MockupInput.Text;

            if (input == MockupInput.Radio)
            {
                var element = Begin(node, path, "fieldset", ["mk-field"]);
                A11y(element, node);
                Open(element);
                if (HasText(element, node))
                {
                    body.Append("<legend>");
                    RenderText(element, node, key);
                    body.Append("</legend>");
                }

                var selected = ScalarText(node.Value);
                var name = Html.EncodeAttribute(key);
                foreach (var option in node.Options ?? [])
                {
                    body.Append("<label class=\"mk-option\"><input type=\"radio\" name=\"").Append(name).Append('"');
                    if (string.Equals(option, selected, StringComparison.Ordinal))
                    {
                        body.Append(" checked");
                    }

                    body.Append('>').Append(Html.Encode(option)).Append("</label>");
                }

                Close(element);
                return;
            }

            var labelElement = Begin(node, path, "label", ["mk-field"]);
            A11y(labelElement, node);
            Open(labelElement);
            if (HasText(labelElement, node))
            {
                body.Append("<span>");
                RenderText(labelElement, node, key);
                body.Append("</span>");
            }

            switch (input)
            {
                case MockupInput.Textarea:
                    body.Append("<textarea");
                    Placeholder(node);
                    Required(node);
                    body.Append('>').Append(Html.Encode(ScalarText(node.Value))).Append("</textarea>");
                    break;

                case MockupInput.Select:
                    body.Append("<select");
                    Required(node);
                    body.Append('>');
                    var selectedOption = ScalarText(node.Value);
                    foreach (var option in node.Options ?? [])
                    {
                        body.Append("<option");
                        if (string.Equals(option, selectedOption, StringComparison.Ordinal))
                        {
                            body.Append(" selected");
                        }

                        body.Append('>').Append(Html.Encode(option)).Append("</option>");
                    }

                    body.Append("</select>");
                    break;

                case MockupInput.Checkbox:
                    body.Append("<input type=\"checkbox\"");
                    if (node.Value is { ValueKind: JsonValueKind.True })
                    {
                        body.Append(" checked");
                    }

                    body.Append('>');
                    break;

                default:
                    body.Append("<input type=\"").Append(InputType(input)).Append('"');
                    Placeholder(node);
                    Required(node);
                    if (ScalarText(node.Value) is { Length: > 0 } value)
                    {
                        body.Append(" value=\"").Append(Html.EncodeAttribute(value)).Append('"');
                    }

                    body.Append('>');
                    break;
            }

            Close(labelElement);
        }

        private void Placeholder(MockupNode node)
        {
            if (node.Placeholder is { } placeholder)
            {
                body.Append(" placeholder=\"").Append(Html.EncodeAttribute(placeholder)).Append('"');
            }
        }

        private void Required(MockupNode node)
        {
            if (node.Required is true)
            {
                body.Append(" required");
            }
        }

        private void ButtonNode(MockupNode node, string path)
        {
            var key = node.Id ?? path;
            var element = Begin(node, path, "button", ["mk-button"]);
            element.Attrs.Add(("type", "button"));
            A11y(element, node);
            Open(element);
            if (!HasText(element, node) && element.Attrs.LastOrDefault(a => string.Equals(a.Name, "aria-label", StringComparison.Ordinal)).Value is { Length: > 0 } name)
            {
                // Backlog 239 (plan @51): an icon button the design names only by its label shows that name as a muted
                // placeholder, not an empty box. Hidden from the accessibility tree, so its name stays the aria-label alone.
                body.Append("<span class=\"mk-placeholder\" aria-hidden=\"true\">").Append(Html.Encode(name)).Append("</span>");
            }
            else
            {
                RenderText(element, node, key);
            }

            Close(element);
        }

        /// <summary>
        /// Backlog 290: a link is an <c>a</c> whose <c>href</c> is ALWAYS <c>#</c>. It needs one, because an <c>a</c>
        /// without an <c>href</c> is not a link to a browser's role engine; and it is never the design's target, so there
        /// is nothing live to follow, and on top of that the page's script cancels the click on an <c>a[href]</c> (an
        /// overlay's <c>setAttribute</c> of <c>href</c> included) and the edit render cancels every click. The design's
        /// target rides as <c>data-mk-href</c>, data for a reader that nothing acts on.
        /// </summary>
        private void LinkNode(MockupNode node, string path)
        {
            var key = node.Id ?? path;
            var element = Begin(node, path, "a", ["mk-link"]);
            element.Attrs.Add(("href", "#"));
            if (node.Href is { } href)
            {
                element.Attrs.Add(("data-mk-href", href));
            }

            A11y(element, node);
            Open(element);
            if (!HasText(element, node) && element.Attrs.LastOrDefault(a => string.Equals(a.Name, "aria-label", StringComparison.Ordinal)).Value is { Length: > 0 } name)
            {
                // As an icon button (backlog 239): the label shows as a muted placeholder, hidden from the accessibility tree.
                body.Append("<span class=\"mk-placeholder\" aria-hidden=\"true\">").Append(Html.Encode(name)).Append("</span>");
            }
            else
            {
                RenderText(element, node, key);
            }

            Close(element);
        }

        private void TextNode(MockupNode node, string path)
        {
            var key = node.Id ?? path;
            var tag = node.A11y?.Heading is { } level and >= 1 and <= 6
                ? "h" + level.ToString(CultureInfo.InvariantCulture)
                : "p";
            var element = Begin(node, path, tag, ["mk-text"]);
            A11y(element, node);
            Open(element);
            RenderText(element, node, key);
            Close(element);
        }

        private void ImageNode(MockupNode node, string path)
        {
            var key = node.Id ?? path;
            var element = Begin(node, path, "div", ["mk-image"]);
            element.Attrs.Add(("role", "img"));
            A11y(element, node);
            Open(element);
            if (HasText(element, node))
            {
                RenderText(element, node, key);
            }
            else
            {
                body.Append(Html.Encode(node.A11y?.Label ?? "Image"));
            }

            Close(element);
        }

        // ---- the element record: generated attrs + the overlay replayed onto it ----------------------

        /// <summary>One element in progress: its ordered attributes, classes, style and overlay effects.</summary>
        private sealed class Element(MockupNode node, string key, string path, string tag)
        {
            public MockupNode Node { get; } = node;
            public string Key { get; } = key;
            public string Path { get; } = path;
            public string Tag { get; } = tag;
            public List<string> Classes { get; } = [];
            public List<(string Name, string Value)> Attrs { get; } = [];
            public Dictionary<string, string> Style { get; } = new(StringComparer.Ordinal);
            public string? TextOverride { get; set; }
            public List<string> Before { get; } = [];
            public List<string> After { get; } = [];
            public List<string> Inside { get; } = [];

            /// <summary>Backlog 238: the part this element is of its node, or the node's own element.</summary>
            public DesignPart Part { get; init; } = DesignPart.Node;

            /// <summary>A column header's column.</summary>
            public string? Column { get; init; }

            /// <summary>
            /// The overlay entries this element took, counted as placed only once it is WRITTEN: a part that is not drawn
            /// (a title with no text) leaves its operations unplaced rather than silently doing nothing.
            /// </summary>
            public List<int> Took { get; } = [];
        }

        /// <summary>
        /// Starts a PART's element (backlog 238, plan @52) and replays onto it exactly the overlay entries that name this
        /// node AND this part (and column), after its generated classes and attributes, as <see cref="Open"/> does for a node.
        /// </summary>
        private Element BeginPart(
            MockupNode node, string path, string tag, DesignPart part, string? column,
            IReadOnlyList<string> classes, IReadOnlyList<(string, string)> attrs)
        {
            var element = new Element(node, node.Id ?? path, path, tag) { Part = part, Column = column };
            element.Classes.AddRange(classes);
            element.Attrs.AddRange(attrs);
            Apply(element, node.Id, path);
            return element;
        }

        /// <summary>
        /// Writes a part's opening tag: its node and part as <c>data-mk-part-of</c> and <c>data-mk-part</c>, then as a
        /// node's is written. Its operations count as placed from here, and the part is listed as emitted with
        /// <paramref name="text"/>, the words it shows.
        /// </summary>
        private void OpenPart(Element element, string? text)
        {
            placed.UnionWith(element.Took);
            var anchor = element.Node.Id is { } id
                ? new PrototypeAnchor(id, null, element.Part, element.Column)
                : new PrototypeAnchor(null, element.Path, element.Part, element.Column);
            parts.TryAdd(
                new PrototypePartKey(element.Key, element.Part, element.Column),
                new PrototypeRenderedAnchor(element.Key, anchor, MockupVocabulary.Name(element.Node.Type), text));

            foreach (var fragment in element.Before)
            {
                body.Append(fragment);
            }

            body.Append('<').Append(element.Tag);
            body.Append(' ').Append(PartOfAttribute).Append("=\"").Append(Html.EncodeAttribute(element.Key)).Append('"');
            body.Append(' ').Append(PartAttribute).Append("=\"").Append(PrototypeVocabulary.Name(element.Part)).Append('"');
            Rest(element);
        }

        /// <summary>
        /// Starts an element: its own generated classes only. The overlay is replayed in <see cref="Open"/>,
        /// not here, because every component adds its own attributes (an aria-label, a role, an input type)
        /// AFTER calling this and BEFORE calling <see cref="Open"/>; applying the overlay this early made a
        /// <c>setAttribute</c> of one of those names append a second copy instead of replacing it.
        /// </summary>
        private Element Begin(MockupNode node, string path, string tag, IReadOnlyList<string> baseClasses)
        {
            var element = new Element(node, node.Id ?? path, path, tag);
            element.Classes.AddRange(baseClasses);
            ReportUnexpressedOverrides(node, element.Key);
            StateCss(node, element.Key);
            return element;
        }

        /// <summary>
        /// A state's override as CSS under its class (backlog 213, plan @38): a layout, as a viewport's is. Its text
        /// is a variant span (<see cref="RenderText"/>) and a table's columns are hidden by <see cref="TableNode"/>;
        /// everything else a state's override carries is reported, never dropped.
        /// </summary>
        private void StateCss(MockupNode node, string key)
        {
            foreach (var (state, over) in States(node))
            {
                if (over.Layout is { } layout)
                {
                    var declarations = string.Join(' ', LayoutDeclarations(layout, over.Cols ?? node.Cols)
                        .Select(d => $"{d.Name}: {d.Value} !important;"));
                    styleRules.Add($"[{AnchorAttribute}=\"{Selector(key)}\"].{StateClass(state)} {{ {declarations} }}");
                }

                foreach (var property in Unexpressed(over))
                {
                    unexpressedOverrides.Add(new PrototypeUnexpressedOverride(key, null, property, MockupVocabulary.Name(state)));
                }
            }
        }

        /// <summary>
        /// The override properties no CSS/DOM mechanism here expresses (everything but <c>text</c>,
        /// <c>layout</c>/<c>cols</c> and a table's <c>columns</c>: plan @36), in the schema's order.
        /// </summary>
        private static IEnumerable<string> Unexpressed(MockupNode over)
        {
            if (over.Href is not null)
            {
                yield return "href";
            }

            if (over.Input is not null)
            {
                yield return "input";
            }

            if (over.Value is not null)
            {
                yield return "value";
            }

            if (over.Placeholder is not null)
            {
                yield return "placeholder";
            }

            if (over.Required is not null)
            {
                yield return "required";
            }

            if (over.Options is not null)
            {
                yield return "options";
            }

            if (over.Data is not null)
            {
                yield return "data";
            }

            if (over.Show is not null)
            {
                yield return "show";
            }

            if (over.Items is not null)
            {
                yield return "items";
            }

            if (over.A11y is not null)
            {
                yield return "a11y";
            }

            if (over.Children is not null)
            {
                yield return "children";
            }
        }

        /// <summary>A node's described states with their overrides, in the vocabulary's order (never the map's).</summary>
        private static IEnumerable<(MockupState State, MockupNode Over)> States(MockupNode node)
        {
            if (node.States is not { } states)
            {
                yield break;
            }

            foreach (var state in Enum.GetValues<MockupState>())
            {
                if (states.TryGetValue(state, out var over))
                {
                    yield return (state, over);
                }
            }
        }

        private static string StateClass(MockupState state) => "mk-state-" + MockupVocabulary.Name(state);

        /// <summary>
        /// One positive selector per column <paramref name="over"/> drops, under <paramref name="scope"/>, or null when
        /// it drops none or carries no columns. Never a <c>:not()</c> of the kept ones: <c>:not()</c> matches every
        /// OTHER element, so a <c>:not([data-mk-col="age"])</c> rule hides "name", the column meant to stay.
        /// </summary>
        private static string? HiddenColumns(IReadOnlyList<MockupColumn> columns, MockupNode over, string scope)
        {
            if (over.Columns is not { } overColumns)
            {
                return null;
            }

            var kept = overColumns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            var hide = columns.Select(c => c.Key).Where(k => !kept.Contains(k)).ToArray();
            return hide.Length == 0
                ? null
                : string.Join(", ", hide.Select(k => $"{scope} [{ColumnAttribute}=\"{Selector(k)}\"]"));
        }

        /// <summary>
        /// Every <c>at</c> override property on <paramref name="node"/> this renderer has no CSS/DOM
        /// mechanism for (everything but <c>text</c>, <c>layout</c>/<c>cols</c> and a table's <c>columns</c>
        /// - plan @36), queued for <see cref="PrototypeRenderResult.UnexpressedOverrides"/> rather than
        /// silently doing nothing, in document viewport order.
        /// </summary>
        private void ReportUnexpressedOverrides(MockupNode node, string key)
        {
            if (node.At is not { } at)
            {
                return;
            }

            foreach (var viewport in document.Viewports)
            {
                if (!at.TryGetValue(viewport.Name, out var over))
                {
                    continue;
                }

                foreach (var property in Unexpressed(over))
                {
                    unexpressedOverrides.Add(new PrototypeUnexpressedOverride(key, viewport.Name, property));
                }
            }
        }

        /// <summary>
        /// Replays every overlay operation anchored to this element, in overlay order. A node is matched
        /// both by its <c>id</c> (when it has one) and by its own tree path, independently: an overlay may
        /// legally address a node that has an id by its path instead, so the two namespaces are never
        /// collapsed into the single "id-or-path" string <see cref="Element.Key"/> uses for the emitted
        /// <c>data-mk-anchor</c> attribute (which must pick exactly one spelling to display).
        /// </summary>
        private void Apply(Element element, string? id, string path)
        {
            foreach (var i in Matches(id, path, element.Part, element.Column))
            {
                switch (overlay[i])
                {
                    case PrototypeSetTextOperation setText:
                        element.Took.Add(i);
                        element.TextOverride = setText.Text;
                        break;

                    case PrototypeSetCssOperation setCss:
                        element.Took.Add(i);
                        foreach (var (name, value) in setCss.Properties)
                        {
                            element.Style[name] = value;
                        }

                        break;

                    case PrototypeSetAttributeOperation setAttribute:
                        // Defense in depth, same rule PrototypeReader already enforces (PrototypeHtmlGuard):
                        // an operation built directly (bypassing the reader) rather than read from JSON must
                        // not be able to emit an attribute name the reader would have refused. A name this
                        // check rejects is left unplaced, never written.
                        if (PrototypeHtmlGuard.IsAttributeName(setAttribute.AttributeName))
                        {
                            element.Took.Add(i);
                            SetAttribute(element, setAttribute.AttributeName, setAttribute.Value);
                        }

                        break;

                    case PrototypeAddClassOperation addClass:
                        element.Took.Add(i);
                        element.Classes.Add(addClass.Class);
                        break;

                    case PrototypeInsertHtmlOperation insertHtml:
                        // The same defence in depth as setAttribute's name: a fragment the guard refuses, built
                        // directly rather than read, is left unplaced, never written. Not the boundary: whatever
                        // the guard misses lands, and the page's policy is what stops it (plan @38).
                        if (PrototypeHtmlGuard.FragmentRefusal(insertHtml.Html) is not null)
                        {
                            break;
                        }

                        element.Took.Add(i);
                        (insertHtml.Placement switch
                        {
                            PrototypePlacement.Before => element.Before,
                            PrototypePlacement.After => element.After,
                            _ => element.Inside,
                        }).Add(insertHtml.Html);
                        break;
                }
            }
        }

        private static void SetAttribute(Element element, string name, string value)
        {
            if (string.Equals(name, "class", StringComparison.Ordinal))
            {
                element.Classes.Clear();
                element.Classes.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                return;
            }

            if (string.Equals(name, "style", StringComparison.Ordinal))
            {
                foreach (var declaration in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = declaration.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        element.Style[parts[0].Trim()] = parts[1].Trim();
                    }
                }

                return;
            }

            for (var i = 0; i < element.Attrs.Count; i++)
            {
                if (string.Equals(element.Attrs[i].Name, name, StringComparison.Ordinal))
                {
                    element.Attrs[i] = (name, value);
                    return;
                }
            }

            element.Attrs.Add((name, value));
        }

        /// <summary>
        /// The overlay entries naming this node, by id or by path, AND this part and column (backlog 238): a node's own
        /// element takes only part-less entries, a part's element only its own.
        /// </summary>
        private List<int> Matches(string? id, string path, DesignPart part, string? column)
        {
            var found = new List<int>();
            if (id is not null && byId.TryGetValue(id, out var byIdList))
            {
                found.AddRange(byIdList);
            }

            if (byPath.TryGetValue(path, out var byPathList))
            {
                found.AddRange(byPathList);
            }

            found.RemoveAll(i => AnchorOf(overlay[i]) is not { } anchor
                || anchor.Part != part
                || !string.Equals(anchor.Column, column, StringComparison.Ordinal));
            found.Sort();
            return found;
        }

        private void Open(Element element)
        {
            // Backlog 213: what the tree says the node does, as data the page's one script reads, and the state
            // it starts in, as the class that state's rules hang off. Written before the overlay is replayed, which
            // can never set a data-mk- attribute (PrototypeHtmlGuard.IsAttributeName).
            if (element.Node.On is { Count: > 0 } on)
            {
                element.Attrs.Add(("data-mk-on", ActionsJson(on)));
            }

            if (element.Node.State is { } state)
            {
                element.Classes.Add(StateClass(state));
            }

            Apply(element, element.Node.Id, element.Path);
            placed.UnionWith(element.Took);

            // Backlog 214: what the edit panel may select, as emitted, the first element with a key winning.
            anchors.TryAdd(element.Key, new PrototypeRenderedAnchor(
                element.Key,
                element.Node.Id is { } id ? new PrototypeAnchor(id, null) : new PrototypeAnchor(null, element.Path),
                MockupVocabulary.Name(element.Node.Type),
                element.TextOverride ?? element.Node.Text));

            foreach (var fragment in element.Before)
            {
                body.Append(fragment);
            }

            body.Append('<').Append(element.Tag);
            body.Append(' ').Append(AnchorAttribute).Append("=\"").Append(Html.EncodeAttribute(element.Key)).Append('"');
            Rest(element);
        }

        /// <summary>An opening tag's class, attributes and inline style, then its close: the same for a node and a part.</summary>
        private void Rest(Element element)
        {
            if (element.Classes.Count > 0)
            {
                body.Append(" class=\"").Append(Html.EncodeAttribute(string.Join(' ', element.Classes))).Append('"');
            }

            foreach (var (name, value) in element.Attrs)
            {
                body.Append(' ').Append(name).Append("=\"").Append(Html.EncodeAttribute(value)).Append('"');
            }

            if (element.Style.Count > 0)
            {
                var declarations = element.Style.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => $"{p.Key}: {p.Value}");
                body.Append(" style=\"").Append(Html.EncodeAttribute(string.Join("; ", declarations))).Append('"');
            }

            body.Append('>');
        }

        private void Close(Element element)
        {
            foreach (var fragment in element.Inside)
            {
                body.Append(fragment);
            }

            body.Append("</").Append(element.Tag).Append('>');
            foreach (var fragment in element.After)
            {
                body.Append(fragment);
            }
        }

        /// <summary>
        /// Whether this element has any visible text to show: an overlay override, its own, or a variant a
        /// viewport or a state gives it (a list with no caption whose empty state says "Nobody yet").
        /// </summary>
        private static bool HasText(Element element, MockupNode node) =>
            element.TextOverride is not null
            || node.Text is not null
            || node.At?.Values.Any(o => o.Text is not null) == true
            || node.States?.Values.Any(o => o.Text is not null) == true;

        /// <summary>
        /// Writes an element's visible text to <see cref="body"/>: an overlay <c>setText</c> wins outright
        /// (at every viewport and in every state, like every other overlay effect); otherwise the node's own
        /// text, with a variant for every named viewport whose <c>at</c> override changes it (shown only in
        /// that viewport's band) and for every state whose override does (shown while the node is in it, at
        /// every width, because a state goes on top of a viewport as in <c>MockupView.Resolve</c>). Each span
        /// names its node by <c>data-mk-text-of</c>, so a rule for this node never reaches a node inside it.
        /// </summary>
        private void RenderText(Element element, MockupNode node, string key)
        {
            if (element.TextOverride is { } overridden)
            {
                body.Append(Html.Encode(overridden));
                return;
            }

            var own = node.Text ?? string.Empty;
            var variants = TextVariants(node, key);
            if (variants.Count == 0)
            {
                body.Append(Html.Encode(own));
                return;
            }

            var of = Html.EncodeAttribute(key);
            body.Append("<span class=\"mk-text-base\" ").Append(TextOfAttribute).Append("=\"").Append(of).Append("\">")
                .Append(Html.Encode(own)).Append("</span>");
            foreach (var (variantClass, text) in variants)
            {
                body.Append("<span class=\"mk-text-variant ").Append(variantClass).Append("\" ").Append(TextOfAttribute)
                    .Append("=\"").Append(of).Append("\">").Append(Html.Encode(text)).Append("</span>");
            }
        }

        /// <summary>
        /// Every text variant of this node, with the CSS that shows it already queued: one per named viewport whose
        /// <c>at</c> override changes the text, in document viewport order, then one per state whose override does,
        /// in the vocabulary's order. A variant is hidden by <c>.mk-text-variant</c> until its rule shows it.
        /// </summary>
        private List<(string VariantClass, string Text)> TextVariants(MockupNode node, string key)
        {
            var variants = new List<(string, string)>();
            var of = $"[{TextOfAttribute}=\"{Selector(key)}\"]";

            if (node.At is { } at)
            {
                foreach (var viewport in document.Viewports)
                {
                    if (!at.TryGetValue(viewport.Name, out var over) || over.Text is not { } overText)
                    {
                        continue;
                    }

                    variants.Add(($"mk-text-at-{viewport.Name}", overText));
                    styleRules.Add(
                        $"@media {viewportBands[viewport.Name]} "
                        + $"{{ {of}.mk-text-base {{ display: none; }} {of}.mk-text-at-{viewport.Name} {{ display: inline; }} }}");
                }
            }

            foreach (var (state, over) in States(node))
            {
                if (over.Text is not { } stateText)
                {
                    continue;
                }

                var stateClass = StateClass(state);
                variants.Add(($"mk-text-{stateClass}", stateText));
                var scope = $"[{AnchorAttribute}=\"{Selector(key)}\"].{stateClass}";
                styleRules.Add(
                    $"{scope} {of} {{ display: none !important; }} "
                    + $"{scope} {of}.mk-text-{stateClass} {{ display: inline !important; }}");
            }

            return variants;
        }

        /// <summary>
        /// A node's <c>on</c> as the JSON the page's script reads: events in the vocabulary's order, each action's
        /// parts in the schema's order (go, open, close, set) and <c>set</c>'s entries ordinally, so the same tree
        /// always writes the same bytes.
        /// </summary>
        private static string ActionsJson(IReadOnlyDictionary<MockupEvent, MockupAction> on)
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                foreach (var @event in Enum.GetValues<MockupEvent>())
                {
                    if (!on.TryGetValue(@event, out var action))
                    {
                        continue;
                    }

                    json.WriteStartObject(MockupVocabulary.Name(@event));
                    if (action.Go is { } go)
                    {
                        json.WriteString("go", go);
                    }

                    if (action.Open is { } open)
                    {
                        json.WriteString("open", open);
                    }

                    if (action.Close is { } close)
                    {
                        json.WriteString("close", close);
                    }

                    if (action.Set is { Count: > 0 } set)
                    {
                        json.WriteStartObject("set");
                        foreach (var (id, state) in set.OrderBy(p => p.Key, StringComparer.Ordinal))
                        {
                            json.WriteString(id, state);
                        }

                        json.WriteEndObject();
                    }

                    json.WriteEndObject();
                }

                json.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private void A11y(Element element, MockupNode node)
        {
            if (node.A11y is not { } a11y)
            {
                return;
            }

            if (a11y.Label is { } label)
            {
                element.Attrs.Add(("aria-label", label));
            }

            if (a11y.Live is { } live)
            {
                element.Attrs.Add(("aria-live", live));
            }

            if (a11y.Decorative is true)
            {
                element.Attrs.Add(("aria-hidden", "true"));
            }
        }

        // ---- `at` overrides as media queries (layout) -------------------------------------------------

        private void LayoutCss(Element element, MockupNode node, string key)
        {
            if (node.Layout is { } layout)
            {
                foreach (var (name, value) in LayoutDeclarations(layout, node.Cols))
                {
                    styleRules.Add($"[{AnchorAttribute}=\"{Selector(key)}\"] {{ {name}: {value}; }}");
                }
            }

            if (node.At is not { } at)
            {
                return;
            }

            foreach (var viewport in document.Viewports)
            {
                if (!at.TryGetValue(viewport.Name, out var over) || over.Layout is not { } overLayout)
                {
                    continue;
                }

                var declarations = string.Join(' ', LayoutDeclarations(overLayout, over.Cols ?? node.Cols)
                    .Select(d => $"{d.Name}: {d.Value};"));
                styleRules.Add(
                    $"@media {viewportBands[viewport.Name]} {{ [{AnchorAttribute}=\"{Selector(key)}\"] {{ {declarations} }} }}");
            }
        }

        private static IEnumerable<(string Name, string Value)> LayoutDeclarations(MockupLayout layout, int? cols) =>
            layout switch
            {
                MockupLayout.Row => [("display", "flex"), ("flex-direction", "row"), ("gap", "0.75rem")],
                MockupLayout.Column => [("display", "flex"), ("flex-direction", "column"), ("gap", "0.75rem")],
                MockupLayout.Grid =>
                [
                    ("display", "grid"),
                    ("grid-template-columns", $"repeat({(cols ?? 1).ToString(CultureInfo.InvariantCulture)}, 1fr)"),
                    ("gap", "0.75rem"),
                ],
                _ => [],
            };

        // ---- assembling the page -----------------------------------------------------------------------

        private string Page()
        {
            var style = new StringBuilder();
            style.Append(BaseCss);
            foreach (var rule in styleRules)
            {
                style.Append('\n').Append(rule);
            }

            foreach (var rule in pageCssRules)
            {
                style.Append('\n').Append(rule);
            }

            // Backlog 214 (plan @39): an edit render shows what a click would select and what is selected, after every
            // other rule so it wins. Only an anchor this render emitted is outlined; the key is escaped as every
            // selector here is.
            if (edit)
            {
                style.Append('\n').Append(EditCss);
                if (selected is { } key && selectedPart == DesignPart.Node && anchors.ContainsKey(key)
                    && !PrototypeHtmlGuard.ContainsStyleBreakout(key))
                {
                    style.Append('\n').Append($"[{AnchorAttribute}=\"{Selector(key)}\"] {{ outline: 2px solid #2f6fdf !important; outline-offset: 2px; }}");
                }

                // Backlog 238: a selected part is outlined by its own attributes, never its node's.
                if (selected is { } partOf && selectedPart != DesignPart.Node
                    && parts.ContainsKey(new PrototypePartKey(partOf, selectedPart, selectedColumn))
                    && !PrototypeHtmlGuard.ContainsStyleBreakout(partOf)
                    && !PrototypeHtmlGuard.ContainsStyleBreakout(selectedColumn ?? string.Empty))
                {
                    var column = selectedColumn is { } c ? $"[{ColumnAttribute}=\"{Selector(c)}\"]" : string.Empty;
                    style.Append('\n').Append(
                        $"[{PartOfAttribute}=\"{Selector(partOf)}\"][{PartAttribute}=\"{PrototypeVocabulary.Name(selectedPart)}\"]{column} "
                        + "{ outline: 2px solid #2f6fdf !important; outline-offset: 2px; }");
                }
            }

            // The policy is the head's FIRST element (plan @38), before anything else is parsed, so it governs
            // everything after it; an overlay places nothing in the head, and pageCss is refused a style breakout.
            var page = new StringBuilder();
            page.Append("<!DOCTYPE html>\n<html lang=\"en\"").Append(edit ? " data-mk-edit=\"edit\"" : string.Empty)
                .Append(">\n<head>\n");
            page.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"")
                .Append(Html.EncodeAttribute(ContentSecurityPolicy)).Append("\">\n");
            page.Append("<meta charset=\"utf-8\">\n");
            page.Append("<title>").Append(Html.Encode(document.Title ?? "Prototype")).Append("</title>\n");
            page.Append("<style>\n").Append(style).Append("\n</style>\n</head>\n<body>\n");
            page.Append(body);
            page.Append("\n<script>").Append(Script).Append("</script>\n</body>\n</html>\n");
            return page.ToString();
        }

        /// <summary>An edit render's own rules: what a click would select is outlined under the pointer.</summary>
        private const string EditCss =
            "[data-mk-anchor]:hover, [data-mk-part-of]:hover { outline: 1px dashed #2f6fdf; outline-offset: 1px; cursor: default; }";

        /// <summary>
        /// The page's own rules, in two kinds (backlog 237, plan @50). The LOOK rules, everything down to the tab list,
        /// are the neutral appearance and sit inside <c>:where(...)</c>, so they have zero specificity and ANY overlay
        /// rule wins over them whatever selector it uses: an overlay is a direct edit on the page (ADR 0021 decision 6),
        /// and <c>.mk-button</c> once outranked a page's <c>button { background-color }</c>, drawing white text on grey.
        /// The MECHANISM rules, from <c>[hidden]</c> on, hold the prototype's behaviour together and keep their
        /// specificity, so a general page rule such as <c>span { display: inline }</c> cannot show every text variant at
        /// once. The rules the design wrote for one node (<see cref="LayoutCss"/>, <see cref="StateCss"/>, the variant
        /// bands) are scoped by its anchor and keep theirs too, as a person expects from CSS.
        /// </summary>
        private const string BaseCss = """
            :where(*) { box-sizing: border-box; }
            :where(body) {
              margin: 0; padding: 1.5rem; display: flex; flex-direction: column; gap: 2rem;
              font-family: system-ui, -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
              color: #1a1a1a; background: #f7f7f8;
            }
            :where(.mk-screen) { background: #ffffff; border: 1px solid #d9d9df; border-radius: 0.5rem; padding: 1.5rem; }
            :where(.mk-screen h1) { margin-top: 0; font-size: 1.25rem; }
            :where(.mk-panel, .mk-container, .mk-toolbar, .mk-form) { display: block; }
            :where(.mk-label) { font-weight: 600; margin-bottom: 0.5rem; }
            :where(button, input, select, textarea) { font: inherit; }
            :where(.mk-button) {
              padding: 0.4rem 0.9rem; border-radius: 0.3rem; border: 1px solid #94949c;
              background: #eceef1; cursor: pointer;
            }
            :where(.mk-link) { color: #1f5bbf; text-decoration: underline; cursor: pointer; }
            :where(.mk-placeholder) { color: #5a5a63; font-style: italic; }
            :where(.mk-field) { display: flex; flex-direction: column; gap: 0.25rem; font-size: 0.9rem; }
            :where(.mk-field input, .mk-field select, .mk-field textarea) {
              padding: 0.35rem 0.5rem; border: 1px solid #94949c; border-radius: 0.3rem;
            }
            :where(.mk-option) { display: inline-flex; align-items: center; gap: 0.3rem; margin-right: 0.75rem; }
            :where(table.mk-table) { border-collapse: collapse; width: 100%; }
            :where(table.mk-table th, table.mk-table td) { border: 1px solid #d9d9df; padding: 0.35rem 0.5rem; text-align: left; }
            :where(ul.mk-list, ul.mk-tree) { list-style: none; margin: 0; padding-left: 1rem; }
            :where(.mk-caption) { font-weight: 600; list-style: none; }
            :where(.mk-image) {
              display: flex; align-items: center; justify-content: center; min-height: 3rem;
              background: #e4e4e9; border: 1px dashed #94949c; border-radius: 0.3rem; color: #5a5a63; font-size: 0.85rem;
            }
            :where(.mk-tablist) { display: flex; gap: 0.5rem; margin-bottom: 0.75rem; }
            :where(.mk-tablist button) { padding: 0.3rem 0.8rem; border: 1px solid #94949c; border-radius: 0.3rem; background: #eceef1; }
            :where(.mk-tablist button[aria-selected="true"]) { background: #ffffff; border-color: #2f6fdf; }
            [hidden], dialog:not([open]), .mk-state-hidden { display: none !important; }
            .mk-text-variant { display: none; }
            .mk-state-disabled { opacity: 0.5; pointer-events: none; }
            .mk-state-loading { cursor: progress; opacity: 0.75; }
            .mk-state-error { outline: 2px solid #c62828; outline-offset: 2px; }
            .mk-state-selected { outline: 2px solid #2f6fdf; outline-offset: 2px; }
            .mk-state-focused { box-shadow: 0 0 0 3px rgba(47, 111, 223, 0.35); }
            [data-mk-select="rows"] li:not(.mk-caption), [data-mk-select="rows"] tbody tr { cursor: pointer; }
            """;

        // ---- small helpers ----------------------------------------------------------------------------

        /// <summary>
        /// Each named viewport's own media-query CONDITION: a band, not a plain <c>max-width</c>, because
        /// <c>MockupView.Resolve</c> (the editor's own reading of <c>at</c>) applies exactly one override -
        /// whichever viewport is chosen - never several at once. A plain <c>max-width: {width}px</c> per
        /// viewport does not: at 390px a document's own <c>max-width: 1280px</c> rule matches too, so two
        /// text variants (or two layouts) would show at once, and whichever rule happens to come LATER in
        /// the stylesheet would decide which layout wins, regardless of which viewport is actually narrower.
        /// Sorting by width and bounding each viewport's band between the next-narrower viewport's width
        /// (exclusive) and its own (inclusive) makes the bands disjoint, so exactly one condition matches at
        /// any width, whatever order the document declares its viewports in. The narrowest viewport's band
        /// has no lower bound; nothing wider than the widest named viewport has a band at all, so a node's
        /// un-overridden base is what shows there (the convention #212's plan @36 records: a viewport names
        /// how something looks "at this width and narrower", not a true upper bound on the design).
        /// </summary>
        private static Dictionary<string, string> ViewportBands(IReadOnlyList<MockupViewport> viewports)
        {
            var sorted = viewports.OrderBy(v => v.Width).ToList();
            var bands = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < sorted.Count; i++)
            {
                var max = sorted[i].Width.ToString(CultureInfo.InvariantCulture);
                bands[sorted[i].Name] = i == 0
                    ? $"(max-width: {max}px)"
                    : $"(min-width: {(sorted[i - 1].Width + 1).ToString(CultureInfo.InvariantCulture)}px) and (max-width: {max}px)";
            }

            return bands;
        }

        /// <summary>Escapes a value for use inside a CSS string literal (a selector's attribute value).</summary>
        private static string Selector(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string InputType(MockupInput input) => input switch
        {
            MockupInput.Password => "password",
            MockupInput.Number => "number",
            MockupInput.Email => "email",
            MockupInput.Date => "date",
            MockupInput.Search => "search",
            _ => "text",
        };

        private static string? ScalarText(JsonElement? value) => value?.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };

        private static List<T> Bucket<T>(Dictionary<string, List<T>> map, string key)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            return list;
        }
    }

    /// <summary>A minimal hand-rolled HTML encoder: no library, matching this codebase's habit elsewhere.</summary>
    private static class Html
    {
        /// <summary>Encodes text content: <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>.</summary>
        public static string Encode(string? text) =>
            (text ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        /// <summary>Encodes a double-quoted attribute value: text encoding plus <c>"</c>.</summary>
        public static string EncodeAttribute(string? text) => Encode(text).Replace("\"", "&quot;");
    }
}
