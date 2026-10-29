namespace Fravashi.Application.Mockups;

/// <summary>
/// A short guide to the mockup tree's BEHAVIOUR vocabulary, for an agent that has only <see cref="MockupSchema"/>
/// to go by (backlog 208). <see cref="MockupCheckTool"/> can only refuse what is wrong, and leaving behaviour out
/// is never wrong, so its errors teach the static shape and never <c>states</c>, <c>on</c>, <c>at</c>, <c>a11y</c>
/// or <c>dialog</c>; this text is what fills that gap. Every fenced <c>json</c> example here is a COMPLETE
/// document (it starts <c>{"mockup": 1, "screens": [...]}</c>) so it can be, and is, checked against
/// <see cref="MockupReader"/> directly, by a test that reads the examples out of this very text.
/// <para>
/// Backlog 215 (plan @40) adds the prototype OVERLAY, what <c>prototype_adjust</c> writes. Its examples are fenced
/// <c>prototype</c>, not <c>json</c>, and each is a complete prototype document naming design @12 at version 1, so a
/// test passes every one to <see cref="Prototypes.PrototypeReader"/>.
/// </para>
/// </summary>
public static class MockupGuide
{
    /// <summary>The guide's text, markdown, written for an agent.</summary>
    public const string Text =
        """
        # The mockup tree's behaviour vocabulary

        The shape (which property belongs to which component, what is required) is in the schema. This is the
        other half: how a mockup describes what happens, not only what is shown. A tree with none of this is
        still a valid mockup, just a mute one.

        ## `states`: how a component looks other than at rest

        `states` maps a state name to an override: the properties that change in that state. The eight names are
        `disabled`, `loading`, `empty`, `error`, `selected`, `expanded`, `hidden`, `focused`. `state` names the one
        shown at first, and it must be a key in `states`. An empty override (`{}`) means "the same, in that state".

        ```json
        {
          "mockup": 1,
          "screens": [
            {
              "type": "window",
              "id": "sign-in",
              "children": [
                {
                  "type": "button",
                  "id": "save",
                  "text": "Save",
                  "states": {
                    "disabled": {},
                    "loading": { "text": "Saving..." }
                  }
                }
              ]
            }
          ]
        }
        ```

        ## `on` with `go`, `open`, `close`, `set`: what a component does

        `on` maps an event to an action. The events are `click` (anything but window, dialog and form), `submit`
        (form), `change` (field) and `select` (list, table, tree, tabs). An action carries one or more of `go` (a
        screen id, to navigate there), `open` / `close` (a dialog id) and `set` (node id to the state it is then
        shown in, one of THAT node's own described states, or `"default"` for how it looks at rest).

        ```json
        {
          "mockup": 1,
          "screens": [
            {
              "type": "window",
              "id": "sign-in",
              "children": [
                {
                  "type": "form",
                  "on": { "submit": { "set": { "save": "loading" }, "go": "home" } },
                  "children": [
                    {
                      "type": "button",
                      "id": "save",
                      "text": "Save",
                      "states": { "loading": { "text": "Saving..." } }
                    }
                  ]
                }
              ]
            },
            { "type": "window", "id": "home", "children": [] }
          ]
        }
        ```

        ## `at`: how a component differs by viewport width

        A design declares its `viewports` at the root (each a `name` and a `width`, 200 to 7680 CSS pixels). A
        node's `at` maps one of those names to an override, applied BEFORE any state override.

        ```json
        {
          "mockup": 1,
          "viewports": [{ "name": "wide", "width": 1280 }, { "name": "narrow", "width": 390 }],
          "screens": [
            {
              "type": "window",
              "id": "home",
              "children": [
                { "type": "toolbar", "id": "actions", "layout": "row", "at": { "narrow": { "layout": "column" } }, "children": [] }
              ]
            }
          ]
        }
        ```

        ## `a11y`: accessibility intent

        `label` is the accessible name (required on an image, or when a button, link or field has no `text`), `heading`
        is 1 to 6, `live` is `polite` or `assertive` for content that changes, `decorative` marks an image that
        carries no information. A node is labelled or decorative, never both.

        ```json
        {
          "mockup": 1,
          "screens": [
            {
              "type": "window",
              "id": "home",
              "children": [
                { "type": "text", "id": "heading", "text": "Rooms", "a11y": { "heading": 1 } },
                { "type": "button", "id": "refresh", "a11y": { "label": "Refresh" } }
              ]
            }
          ]
        }
        ```

        ## `dialog`: shown and hidden, never a screen

        A `dialog` needs an `id` and is never one of the document's `screens`; it sits inside the window it
        belongs to and is shown only through `open` and hidden only through `close` (see `on` above).

        ```json
        {
          "mockup": 1,
          "screens": [
            {
              "type": "window",
              "id": "rooms",
              "children": [
                { "type": "button", "id": "book", "text": "Book", "on": { "click": { "open": "booking" } } },
                {
                  "type": "dialog",
                  "id": "booking",
                  "children": [
                    { "type": "button", "id": "cancel", "text": "Cancel", "on": { "click": { "close": "booking" } } }
                  ]
                }
              ]
            }
          ]
        }
        ```

        ## `link`: something a person follows, not a button

        Draw a link as a `link`, never as a `button`: a browser reports the two as different roles (`link` and
        `button`), and the design check looks for the role the design names. A link needs `text` (or `a11y.label`
        when it shows only an icon) and may carry `href`, where it goes. `href` is kept for a reader only: the
        prototype never follows it and the check does not compare it, so a placeholder is fine. To show a move
        between the design's own screens, give the link `on` with `click` and `go`, as a button would; the
        prototype follows it, and the check does not.

        ```json
        {
          "mockup": 1,
          "title": "Not found",
          "screens": [
            {
              "type": "window",
              "id": "not-found",
              "text": "Not found",
              "children": [
                { "type": "text", "text": "Sorry, there's nothing at this address." },
                { "type": "link", "id": "go-home", "text": "Go to the start page", "href": "/", "on": { "click": { "go": "start" } } }
              ]
            },
            { "type": "window", "id": "start", "text": "Start", "children": [] }
          ]
        }
        ```

        # The prototype overlay

        A prototype is a design rendered as a page (`prototype_render`), plus an OVERLAY: direct edits kept on top
        of the rendered page, applied in order, the last `setText` or the last `setCss` of a property winning. A
        person edits it on the prototype's page; an agent adjusts it with `prototype_adjust`, which a person
        approves seeing each operation in words. Both write the same operations.

        The overlay is for how the page LOOKS. Anything structural (add or remove a field, a screen, a dialog or a
        behaviour, change a layout) belongs in the DESIGN: change the design, then `prototype_render` the prototype
        onto the design's new version. An overlay that rebuilds the design is a second truth nobody can review.

        Every operation but `pageCss` names its node by an `anchor`: `{"id": "save"}` for a node with an id, or
        `{"path": "/screens/0/children/2"}` (a JSON Pointer into the design's tree) for one without. The anchors a
        page emits are listed by `prototype_render`. `prototype_adjust` takes a list of operations like those in
        `overlay` below; a whole prototype document (what `prototype_check` reads) looks like these.

        An anchor may also name one PART of its node, so an edit reaches only that: `"part": "title"` (a screen's or
        a dialog's title), `"caption"` (a table's, list's or tree's), `"tab"` (on a panel of tabs: the tab it is
        chosen by), or `"columnHeader"` with `"column"`, the column's key (one header of a table). Without a part the
        anchor is the whole node, so a font size on a screen changes everything in it; on its title, only the title.
        `prototype_render` lists the parts each page draws, and a part a node does not have is refused.

        ## `setText` and `setCss`: words and styles

        `setText` replaces a node's visible text (it is shown as text, never as markup). `setCss` sets one or more
        inline style properties.

        ```prototype
        {
          "prototype": 1,
          "design": { "number": 12, "version": 1 },
          "overlay": [
            { "op": "setText", "anchor": { "id": "save" }, "text": "Save changes" },
            { "op": "setCss", "anchor": { "id": "save" }, "properties": { "background-color": "#0b5", "font-weight": "600" } },
            { "op": "setCss", "anchor": { "id": "people", "part": "title" }, "properties": { "font-size": "1.6rem" } },
            { "op": "setText", "anchor": { "id": "people-table", "part": "columnHeader", "column": "email" }, "text": "E-mail" }
          ]
        }
        ```

        ## `addClass`, `setAttribute` and `pageCss`: a class, an attribute, a page rule

        `addClass` adds one class; `pageCss` adds one rule to the page's own stylesheet and is the one operation
        with no anchor. `setAttribute` sets one attribute; an event handler (`on...`), the renderer's own
        `data-mk-` attributes and a `javascript:` URL are refused, because an overlay changes how a page looks,
        never what runs.

        ```prototype
        {
          "prototype": 1,
          "design": { "number": 12, "version": 1 },
          "overlay": [
            { "op": "addClass", "anchor": { "path": "/screens/0/children/0" }, "class": "emphasis" },
            { "op": "pageCss", "rule": ".emphasis { border: 2px solid #0b5; border-radius: 6px; }" },
            { "op": "setAttribute", "anchor": { "id": "save" }, "name": "title", "value": "Saves the booking" }
          ]
        }
        ```

        ## `insertHtml`: a small fragment

        `insertHtml` puts a fragment `before`, `after` or `inside` (as its last child) a node: a hint, a badge, a
        line of help. A script, an event handler, an element that loads, embeds or navigates (a frame, an object,
        a link, a form), a script URL and a `data-mk-` attribute are refused. If what you want is a new field or a
        new control, it is structure: put it in the design.

        ```prototype
        {
          "prototype": 1,
          "design": { "number": 12, "version": 1 },
          "overlay": [
            { "op": "insertHtml", "anchor": { "id": "save" }, "placement": "after", "html": "<p class=\"hint\">Nothing is booked until you save.</p>" }
          ]
        }
        ```
        """;
}
