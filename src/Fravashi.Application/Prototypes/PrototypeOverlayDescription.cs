namespace Fravashi.Application.Prototypes;

/// <summary>
/// Describes one overlay operation in words (backlog 211: "lists the overlay in words"). Nothing renders
/// yet, so this is how the prototype's page shows what the overlay does without drawing it.
/// </summary>
public static class PrototypeOverlayDescription
{
    /// <summary>A sentence saying what the operation does.</summary>
    public static string Describe(PrototypeOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return operation switch
        {
            PrototypeSetTextOperation op => $"Sets the text of {Anchor(op.Anchor)} to \"{op.Text}\".",
            PrototypeSetCssOperation op => $"Sets {Properties(op.Properties)} on {Anchor(op.Anchor)}.",
            PrototypeSetAttributeOperation op =>
                $"Sets \"{op.AttributeName}\" to \"{op.Value}\" on {Anchor(op.Anchor)}.",
            PrototypeAddClassOperation op => $"Adds the class \"{op.Class}\" to {Anchor(op.Anchor)}.",
            PrototypePageCssOperation op => $"Adds a page-level rule: {op.Rule}",
            PrototypeInsertHtmlOperation op =>
                $"Inserts HTML {PrototypeVocabulary.Name(op.Placement)} {Anchor(op.Anchor)}.",
            _ => "An operation this page does not know how to describe.",
        };
    }

    /// <summary>The anchor in words; backlog 238: a part as "the title of ...", a column header with its column.</summary>
    public static string Anchor(PrototypeAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var node = anchor.Id is { } id ? $"\"{id}\"" : $"the node at {anchor.Path}";
        return anchor.Part switch
        {
            Verification.DesignPart.Node => node,
            Verification.DesignPart.ColumnHeader => $"the column header \"{anchor.Column}\" of {node}",
            var part => $"the {PrototypeVocabulary.Words(part)} of {node}",
        };
    }

    private static string Properties(IReadOnlyDictionary<string, string> properties) =>
        string.Join(", ", properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}: {p.Value}"));
}
