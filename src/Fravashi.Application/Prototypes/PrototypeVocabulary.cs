using Fravashi.Application.Verification;

namespace Fravashi.Application.Prototypes;

/// <summary>
/// The fixed shape of a prototype document (ADR 0021 decision 6, backlog 211): the schema version, the
/// closed set of overlay operations, and which fields each one carries. Held here, once, so
/// <see cref="PrototypeReader"/> (the judge) and <see cref="PrototypeWriter"/> (the canonical form) cannot
/// drift apart about what is allowed.
/// </summary>
public static class PrototypeVocabulary
{
    /// <summary>The schema version. Always 1 for this schema.</summary>
    public const int Version = 1;

    /// <summary>The root object's properties.</summary>
    public static readonly IReadOnlyList<string> RootProperties = ["prototype", "design", "overlay"];

    /// <summary>The design reference's properties.</summary>
    public static readonly IReadOnlyList<string> DesignProperties = ["number", "version"];

    /// <summary>
    /// An anchor's properties. Exactly one of <c>id</c> and <c>path</c> is present on any anchor; <c>part</c> is optional
    /// (backlog 238), and <c>column</c> goes with the <see cref="ColumnHeader"/> part only.
    /// </summary>
    public static readonly IReadOnlyList<string> AnchorProperties = ["id", "path", "part", "column"];

    /// <summary>A screen's or dialog's title.</summary>
    public const string Title = "title";

    /// <summary>A table's, list's or tree's caption.</summary>
    public const string Caption = "caption";

    /// <summary>The tab button a panel of tabs is chosen by.</summary>
    public const string Tab = "tab";

    /// <summary>One of a table's column headers, named by its column's record key.</summary>
    public const string ColumnHeader = "columnHeader";

    /// <summary>The part names an anchor may carry (backlog 238), in the order an error lists them.</summary>
    public static readonly IReadOnlyList<string> Parts = [Title, Caption, Tab, ColumnHeader];

    /// <summary>A part's stable name; <see cref="DesignPart.Node"/> has none, because it is the node itself.</summary>
    public static string Name(DesignPart part) => part switch
    {
        DesignPart.Title => Title,
        DesignPart.Caption => Caption,
        DesignPart.Tab => Tab,
        DesignPart.ColumnHeader => ColumnHeader,
        _ => throw new ArgumentOutOfRangeException(nameof(part), part, "The node itself has no part name."),
    };

    /// <summary>A part from its stable name. False for anything else, "node" included: no part means the node.</summary>
    public static bool TryParsePart(string? name, out DesignPart part)
    {
        switch (name)
        {
            case Title:
                part = DesignPart.Title;
                return true;
            case Caption:
                part = DesignPart.Caption;
                return true;
            case Tab:
                part = DesignPart.Tab;
                return true;
            case ColumnHeader:
                part = DesignPart.ColumnHeader;
                return true;
            default:
                part = DesignPart.Node;
                return false;
        }
    }

    /// <summary>A part in words, for a person: "title", "column header".</summary>
    public static string Words(DesignPart part) => part == DesignPart.ColumnHeader ? "column header" : Name(part);

    /// <summary>Replaces an anchor's visible text.</summary>
    public const string SetText = "setText";

    /// <summary>Sets inline style properties on an anchor.</summary>
    public const string SetCss = "setCss";

    /// <summary>Sets one attribute on an anchor.</summary>
    public const string SetAttribute = "setAttribute";

    /// <summary>Adds one class to an anchor.</summary>
    public const string AddClass = "addClass";

    /// <summary>A page-level CSS rule, with no anchor.</summary>
    public const string PageCss = "pageCss";

    /// <summary>Inserts a fragment of HTML relative to an anchor.</summary>
    public const string InsertHtml = "insertHtml";

    /// <summary>The closed set of operation names.</summary>
    public static readonly IReadOnlyList<string> Operations =
        [SetText, SetCss, SetAttribute, AddClass, PageCss, InsertHtml];

    /// <summary>The operations that carry no <c>anchor</c>: just <see cref="PageCss"/>.</summary>
    public static readonly IReadOnlySet<string> Unanchored = new HashSet<string>(StringComparer.Ordinal) { PageCss };

    /// <summary>Every field name any operation may carry, "op" included, for the "unknown property" message.</summary>
    public static readonly IReadOnlyList<string> OperationFields =
        ["op", "anchor", "text", "properties", "name", "value", "class", "rule", "placement", "html"];

    /// <summary>The fields a given operation carries, besides <c>op</c> itself.</summary>
    public static IReadOnlyList<string> FieldsOf(string op) => op switch
    {
        SetText => ["anchor", "text"],
        SetCss => ["anchor", "properties"],
        SetAttribute => ["anchor", "name", "value"],
        AddClass => ["anchor", "class"],
        PageCss => ["rule"],
        InsertHtml => ["anchor", "placement", "html"],
        _ => [],
    };

    /// <summary>Where an inserted fragment may land, by its stored name.</summary>
    public static readonly IReadOnlyList<string> Placements = ["before", "after", "inside"];

    /// <summary>A placement's stable name.</summary>
    public static string Name(PrototypePlacement placement) => placement switch
    {
        PrototypePlacement.Before => "before",
        PrototypePlacement.After => "after",
        PrototypePlacement.Inside => "inside",
        _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, null),
    };

    /// <summary>A placement from its stable name.</summary>
    public static bool TryParsePlacement(string? name, out PrototypePlacement placement)
    {
        switch (name)
        {
            case "before":
                placement = PrototypePlacement.Before;
                return true;
            case "after":
                placement = PrototypePlacement.After;
                return true;
            case "inside":
                placement = PrototypePlacement.Inside;
                return true;
            default:
                placement = default;
                return false;
        }
    }
}
