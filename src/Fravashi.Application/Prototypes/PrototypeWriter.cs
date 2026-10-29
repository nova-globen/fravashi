using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Fravashi.Application.Prototypes;

/// <summary>
/// Writes a prototype document in its canonical form, so that two equal documents are equal bytes and a
/// review diff shows only what changed (the same reason <see cref="Mockups.MockupWriter"/> does this).
/// Fields come in a fixed order whatever order they were read in, a <c>setCss</c> operation's properties are
/// written in ordinal order, and the overlay's own order (which operation came first) is never normalised,
/// because that order is part of what the overlay means: two inserts at the same anchor apply in list order.
/// </summary>
public static class PrototypeWriter
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
    public static string Write(PrototypeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            writer.WriteStartObject();
            writer.WriteNumber("prototype", document.Version);

            writer.WriteStartObject("design");
            writer.WriteNumber("number", document.Design.Number);
            writer.WriteNumber("version", document.Design.Version);
            writer.WriteEndObject();

            writer.WriteStartArray("overlay");
            foreach (var operation in document.Overlay)
            {
                Operation(writer, operation);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void Operation(Utf8JsonWriter writer, PrototypeOperation operation)
    {
        writer.WriteStartObject();
        writer.WriteString("op", operation.Op);

        switch (operation)
        {
            case PrototypeSetTextOperation setText:
                Anchor(writer, setText.Anchor);
                writer.WriteString("text", setText.Text);
                break;

            case PrototypeSetCssOperation setCss:
                Anchor(writer, setCss.Anchor);
                writer.WriteStartObject("properties");
                foreach (var (name, value) in setCss.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WriteString(name, value);
                }

                writer.WriteEndObject();
                break;

            case PrototypeSetAttributeOperation setAttribute:
                Anchor(writer, setAttribute.Anchor);
                writer.WriteString("name", setAttribute.AttributeName);
                writer.WriteString("value", setAttribute.Value);
                break;

            case PrototypeAddClassOperation addClass:
                Anchor(writer, addClass.Anchor);
                writer.WriteString("class", addClass.Class);
                break;

            case PrototypePageCssOperation pageCss:
                writer.WriteString("rule", pageCss.Rule);
                break;

            case PrototypeInsertHtmlOperation insertHtml:
                Anchor(writer, insertHtml.Anchor);
                writer.WriteString("placement", PrototypeVocabulary.Name(insertHtml.Placement));
                writer.WriteString("html", insertHtml.Html);
                break;
        }

        writer.WriteEndObject();
    }

    private static void Anchor(Utf8JsonWriter writer, PrototypeAnchor anchor)
    {
        writer.WriteStartObject("anchor");
        if (anchor.Id is not null)
        {
            writer.WriteString("id", anchor.Id);
        }
        else if (anchor.Path is not null)
        {
            writer.WriteString("path", anchor.Path);
        }

        // Backlog 238: only an anchor on a part writes one, so every part-less overlay keeps its bytes.
        if (anchor.Part != Verification.DesignPart.Node)
        {
            writer.WriteString("part", PrototypeVocabulary.Name(anchor.Part));
            if (anchor.Column is not null)
            {
                writer.WriteString("column", anchor.Column);
            }
        }

        writer.WriteEndObject();
    }
}
