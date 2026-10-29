namespace Fravashi.Application.Mockups;

/// <summary>
/// The mockup tree as a JSON Schema (draft 2020-12), for an agent to author against and for a turn that
/// asks for a mockup through <c>--json-schema</c> (ADR 0022).
/// <para>
/// <b>It describes the SHAPE; <see cref="MockupReader"/> is the judge.</b> The schema carries every
/// vocabulary and every property's type, and which properties belong to which component is in each
/// property's description. What a schema cannot say cheaply is left to the reader: per-component property
/// sets, ids being unique, an action's target existing, a table column's key being in the data. A tree this
/// schema accepts can still be refused, always with a message that says why. The vocabularies here are held
/// equal to <see cref="MockupVocabulary"/> by tests, so the two cannot drift.
/// </para>
/// </summary>
public static class MockupSchema
{
    /// <summary>The schema's own id, which names the version it describes.</summary>
    public const string Id = "https://fravashi.local/schemas/mockup/1";

    /// <summary>The JSON Schema text.</summary>
    public const string Json =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "https://fravashi.local/schemas/mockup/1",
          "title": "Fravashi mockup UI-tree, version 1",
          "description": "A wireframe as a tree of components. Screens are windows; go navigates between them. Per-component rules, unique ids and that every action target exists are checked by the studio's reader, which explains any refusal.",
          "type": "object",
          "required": ["mockup", "screens"],
          "additionalProperties": false,
          "properties": {
            "mockup": { "const": 1, "description": "The schema version. Always 1 for this schema." },
            "title": { "type": "string" },
            "viewports": {
              "type": "array",
              "description": "The widths the design is specified at. A node's 'at' names them.",
              "items": {
                "type": "object",
                "required": ["name", "width"],
                "additionalProperties": false,
                "properties": {
                  "name": { "$ref": "#/$defs/name" },
                  "width": { "type": "integer", "minimum": 200, "maximum": 7680 }
                }
              }
            },
            "data": {
              "type": "object",
              "description": "Named sample data sets. A list, table or tree shows one through 'data'.",
              "propertyNames": { "$ref": "#/$defs/name" },
              "additionalProperties": { "type": "array", "items": { "$ref": "#/$defs/record" } }
            },
            "screens": {
              "type": "array",
              "minItems": 1,
              "description": "Each screen is a window with an id.",
              "items": { "$ref": "#/$defs/node" }
            }
          },
          "$defs": {
            "name": {
              "type": "string",
              "pattern": "^[A-Za-z][A-Za-z0-9_-]{0,63}$"
            },
            "record": {
              "type": "object",
              "additionalProperties": {
                "anyOf": [
                  { "type": ["string", "number", "boolean", "null"] },
                  { "type": "array", "items": { "$ref": "#/$defs/record" } }
                ]
              }
            },
            "item": {
              "anyOf": [
                { "type": "string" },
                {
                  "type": "object",
                  "required": ["text"],
                  "additionalProperties": false,
                  "properties": {
                    "text": { "type": "string" },
                    "items": { "type": "array", "items": { "$ref": "#/$defs/item" }, "description": "Tree only." }
                  }
                }
              ]
            },
            "a11y": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "label": { "type": "string", "minLength": 1, "description": "The accessible name. Required on an image (or decorative), and on a button, link or field with no text." },
                "heading": { "type": "integer", "minimum": 1, "maximum": 6 },
                "live": { "enum": ["polite", "assertive"] },
                "decorative": { "type": "boolean" }
              }
            },
            "action": {
              "type": "object",
              "minProperties": 1,
              "additionalProperties": false,
              "properties": {
                "go": { "$ref": "#/$defs/name", "description": "The id of a screen." },
                "open": { "$ref": "#/$defs/name", "description": "The id of a dialog." },
                "close": { "$ref": "#/$defs/name", "description": "The id of a dialog." },
                "set": {
                  "type": "object",
                  "minProperties": 1,
                  "description": "Node id to the state it is then shown in: one of that node's own states, or 'default'.",
                  "additionalProperties": { "type": "string" }
                }
              }
            },
            "override": {
              "type": "object",
              "description": "What changes in a state or at a viewport: any property of the node except type, id, state, states, at and on.",
              "properties": {
                "type": false, "id": false, "state": false, "states": false, "at": false, "on": false
              },
              "allOf": [ { "$ref": "#/$defs/fields" } ]
            },
            "node": {
              "type": "object",
              "required": ["type"],
              "allOf": [ { "$ref": "#/$defs/fields" } ]
            },
            "fields": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "type": {
                  "enum": ["window", "panel", "toolbar", "list", "table", "form", "field", "button", "link", "tabs", "tree", "dialog", "text", "image", "container"],
                  "description": "window only as a screen; dialog never as one; tabs hold panels. A link is something a person follows to go somewhere (role link); a button is something they press to do something (role button)."
                },
                "id": { "$ref": "#/$defs/name", "description": "Unique in the document. Required on a screen and a dialog." },
                "text": { "type": "string", "description": "Visible text: a title, label, caption or the words of a text node. Required on text; a panel inside tabs uses it as the tab label; a button or link needs it unless a11y.label names it." },
                "href": { "type": "string", "minLength": 1, "description": "Link only, optional: where it goes. Kept for a reader; the prototype never follows it and the design check does not compare it." },
                "layout": { "enum": ["row", "column", "grid"], "description": "Containers: window, panel, toolbar, form, dialog, container." },
                "cols": { "type": "integer", "minimum": 1, "maximum": 12, "description": "Columns of a grid layout." },
                "input": { "enum": ["text", "password", "number", "email", "date", "search", "textarea", "select", "radio", "checkbox"], "description": "Field only. Default text." },
                "value": { "type": ["string", "number", "boolean"], "description": "Field only. A sample value; true or false for a checkbox." },
                "placeholder": { "type": "string", "description": "Field only." },
                "required": { "type": "boolean", "description": "Field only." },
                "options": { "type": "array", "minItems": 1, "items": { "type": "string", "minLength": 1 }, "description": "Field only, and required, for select and radio." },
                "data": { "$ref": "#/$defs/name", "description": "List, table, tree: the name of a data set. Not with items." },
                "show": { "type": "string", "minLength": 1, "description": "List, tree: the record key shown, required with data." },
                "columns": {
                  "type": "array",
                  "minItems": 1,
                  "description": "Table only, and required: record keys, or {key, text}.",
                  "items": {
                    "anyOf": [
                      { "type": "string", "minLength": 1 },
                      {
                        "type": "object",
                        "required": ["key"],
                        "additionalProperties": false,
                        "properties": { "key": { "type": "string", "minLength": 1 }, "text": { "type": "string" } }
                      }
                    ]
                  }
                },
                "items": {
                  "type": "array",
                  "description": "List, tree: strings or {text, items}; table: records. Not with data.",
                  "items": { "anyOf": [ { "$ref": "#/$defs/item" }, { "$ref": "#/$defs/record" } ] }
                },
                "a11y": { "$ref": "#/$defs/a11y" },
                "state": {
                  "enum": ["disabled", "loading", "empty", "error", "selected", "expanded", "hidden", "focused"],
                  "description": "The state shown at first; it must be described in states."
                },
                "states": {
                  "type": "object",
                  "propertyNames": { "enum": ["disabled", "loading", "empty", "error", "selected", "expanded", "hidden", "focused"] },
                  "additionalProperties": { "$ref": "#/$defs/override" }
                },
                "at": {
                  "type": "object",
                  "description": "Viewport name (declared at the root) to what changes at that width.",
                  "additionalProperties": { "$ref": "#/$defs/override" }
                },
                "on": {
                  "type": "object",
                  "description": "click: anything but window, dialog and form. submit: form. change: field. select: list, table, tree, tabs.",
                  "propertyNames": { "enum": ["click", "submit", "change", "select"] },
                  "additionalProperties": { "$ref": "#/$defs/action" }
                },
                "children": {
                  "type": "array",
                  "description": "Containers only: window, panel, toolbar, form, tabs, dialog, container.",
                  "items": { "$ref": "#/$defs/node" }
                }
              }
            }
          }
        }
        """;
}
