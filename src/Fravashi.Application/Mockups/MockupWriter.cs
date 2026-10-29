using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Fravashi.Application.Mockups;

/// <summary>
/// Writes a mockup in its canonical form, so that two equal trees are equal bytes and a review diff shows
/// only what changed (ADR 0021: "it diffs cleanly"). Properties come in <see cref="MockupVocabulary"/>'s
/// order whatever order they were read in, nothing absent is written, maps follow the vocabulary's order (and
/// "at" the document's viewport order), and the keys of every sample record are written in ordinal order.
/// <para>
/// What is NOT normalised: the order of screens, children, items, records, columns and options, because that
/// order is the design; and the shorthand a column was written in follows whether it has a heading.
/// </para>
/// </summary>
public static class MockupWriter
{
    // Relaxed escaping writes "Ada Lovelace's <b>" and non-Latin text as themselves rather than as \u
    // escapes: the output is stored and read by people in review, never embedded in a script or a page.
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The canonical JSON of a document, ending in a newline.</summary>
    public static string Write(MockupDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            writer.WriteStartObject();
            writer.WriteNumber("mockup", document.Version);
            if (document.Title is not null)
            {
                writer.WriteString("title", document.Title);
            }

            if (document.Viewports.Count > 0)
            {
                writer.WriteStartArray("viewports");
                foreach (var viewport in document.Viewports)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", viewport.Name);
                    writer.WriteNumber("width", viewport.Width);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (document.Data.Count > 0)
            {
                writer.WriteStartObject("data");
                foreach (var set in document.Data.OrderBy(s => s.Name, StringComparer.Ordinal))
                {
                    writer.WriteStartArray(set.Name);
                    foreach (var record in set.Records)
                    {
                        Canonical(writer, record);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteStartArray("screens");
            foreach (var screen in document.Screens)
            {
                Node(writer, screen, document.Viewports, isOverride: false);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void Node(Utf8JsonWriter writer, MockupNode node, IReadOnlyList<MockupViewport> viewports, bool isOverride)
    {
        writer.WriteStartObject();

        if (!isOverride)
        {
            writer.WriteString("type", MockupVocabulary.Name(node.Type));
        }

        if (node.Id is not null)
        {
            writer.WriteString("id", node.Id);
        }

        if (node.Text is not null)
        {
            writer.WriteString("text", node.Text);
        }

        if (node.Href is not null)
        {
            writer.WriteString("href", node.Href);
        }

        if (node.Layout is { } layout)
        {
            writer.WriteString("layout", MockupVocabulary.Name(layout));
        }

        if (node.Cols is { } cols)
        {
            writer.WriteNumber("cols", cols);
        }

        if (node.Input is { } input)
        {
            writer.WriteString("input", MockupVocabulary.Name(input));
        }

        if (node.Value is { } value)
        {
            writer.WritePropertyName("value");
            value.WriteTo(writer);
        }

        if (node.Placeholder is not null)
        {
            writer.WriteString("placeholder", node.Placeholder);
        }

        if (node.Required is { } required)
        {
            writer.WriteBoolean("required", required);
        }

        if (node.Options is not null)
        {
            writer.WriteStartArray("options");
            foreach (var option in node.Options)
            {
                writer.WriteStringValue(option);
            }

            writer.WriteEndArray();
        }

        if (node.Data is not null)
        {
            writer.WriteString("data", node.Data);
        }

        if (node.Show is not null)
        {
            writer.WriteString("show", node.Show);
        }

        if (node.Columns is not null)
        {
            writer.WriteStartArray("columns");
            foreach (var column in node.Columns)
            {
                if (column.Text is null)
                {
                    writer.WriteStringValue(column.Key);
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteString("key", column.Key);
                    writer.WriteString("text", column.Text);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
        }

        if (node.Items is not null)
        {
            writer.WriteStartArray("items");
            foreach (var item in node.Items)
            {
                Canonical(writer, item);
            }

            writer.WriteEndArray();
        }

        if (node.A11y is { } a11y)
        {
            writer.WriteStartObject("a11y");
            if (a11y.Label is not null)
            {
                writer.WriteString("label", a11y.Label);
            }

            if (a11y.Heading is { } heading)
            {
                writer.WriteNumber("heading", heading);
            }

            if (a11y.Live is not null)
            {
                writer.WriteString("live", a11y.Live);
            }

            if (a11y.Decorative is { } decorative)
            {
                writer.WriteBoolean("decorative", decorative);
            }

            writer.WriteEndObject();
        }

        if (node.State is { } state)
        {
            writer.WriteString("state", MockupVocabulary.Name(state));
        }

        if (node.States is not null)
        {
            writer.WriteStartObject("states");
            foreach (var name in Enum.GetValues<MockupState>().Where(node.States.ContainsKey))
            {
                writer.WritePropertyName(MockupVocabulary.Name(name));
                Node(writer, node.States[name], viewports, isOverride: true);
            }

            writer.WriteEndObject();
        }

        if (node.At is not null)
        {
            writer.WriteStartObject("at");
            foreach (var viewport in viewports.Where(v => node.At.ContainsKey(v.Name)))
            {
                writer.WritePropertyName(viewport.Name);
                Node(writer, node.At[viewport.Name], viewports, isOverride: true);
            }

            writer.WriteEndObject();
        }

        if (node.On is not null)
        {
            writer.WriteStartObject("on");
            foreach (var @event in Enum.GetValues<MockupEvent>().Where(node.On.ContainsKey))
            {
                writer.WritePropertyName(MockupVocabulary.Name(@event));
                Action(writer, node.On[@event]);
            }

            writer.WriteEndObject();
        }

        if (node.Children is not null)
        {
            writer.WriteStartArray("children");
            foreach (var child in node.Children)
            {
                Node(writer, child, viewports, isOverride: false);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void Action(Utf8JsonWriter writer, MockupAction action)
    {
        writer.WriteStartObject();
        if (action.Go is not null)
        {
            writer.WriteString("go", action.Go);
        }

        if (action.Open is not null)
        {
            writer.WriteString("open", action.Open);
        }

        if (action.Close is not null)
        {
            writer.WriteString("close", action.Close);
        }

        if (action.Set is not null)
        {
            writer.WriteStartObject("set");
            foreach (var (id, state) in action.Set.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteString(id, state);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    /// <summary>Writes a value with every object's keys in ordinal order, at every depth.</summary>
    private static void Canonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Canonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Canonical(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
