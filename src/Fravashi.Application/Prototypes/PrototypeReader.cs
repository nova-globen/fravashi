using System.Text.Json;
using Fravashi.Application.Mockups;
using Fravashi.Application.Verification;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Prototypes;

/// <summary>
/// Reads a prototype document from JSON, and is the ONLY validator of one: parsing and checking are one
/// pass, so there is no way to hold a <see cref="PrototypeDocument"/> that broke a rule. Mirrors
/// <see cref="Mockups.MockupReader"/>'s shape and its two rules: every error, not the first, and each one
/// names its place as a JSON path and says what would be allowed instead.
/// <para>
/// Unlike <see cref="Mockups.MockupReader"/> this reader needs the wiki, because a prototype names a design
/// by number and version and that reference has to resolve to something real: a document that exists, is a
/// design, and is at a version the document has actually reached. Checking that is this reader's only I/O.
/// It does NOT read the design's own tree and resolve anchors against it: resolving is the renderer's job
/// (#212, "no HTML parser, unplaced operations reported"), and re-resolving a path anchor after the design
/// moves on is explicitly slice 6's ("regenerate from a newer design and settle unplaced operations").
/// </para>
/// </summary>
public static class PrototypeReader
{
    /// <summary>Reads and validates a prototype document.</summary>
    public static async Task<PrototypeReadResult> ReadAsync(string json, IWikiLibrary wiki, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(wiki);

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            return new PrototypeReadResult(null, [new PrototypeError("$", $"This is not valid JSON: {ex.Message}")]);
        }

        using (parsed)
        {
            return await new Pass().RunAsync(parsed.RootElement, wiki, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads a JSON list of overlay operations on their own (backlog 215, plan @40): what an agent hands
    /// <c>prototype_adjust</c>. Each operation goes through exactly the code a document's <c>overlay</c> does, so an
    /// operation accepted here is one a saved prototype accepts and a refusal is the same sentence; only the paths
    /// differ, rooted at <c>$.operations</c>. Needs no wiki: an operation names no document.
    /// </summary>
    public static PrototypeOperationsRead ReadOperations(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            return new PrototypeOperationsRead(null, [new PrototypeError(OperationsPath, $"This is not valid JSON: {ex.Message}")]);
        }

        using (parsed)
        {
            return new Pass().Operations(parsed.RootElement);
        }
    }

    /// <summary>The path <see cref="ReadOperations"/> roots its errors at.</summary>
    public const string OperationsPath = "$.operations";

    /// <summary>
    /// Backlog 238 (plan @52): every operation in <paramref name="operations"/> whose anchor names a part its node does
    /// not have in <paramref name="tree"/>, as an error at that anchor's <c>part</c> (or <c>column</c>) with the reason
    /// (<see cref="PrototypeParts"/>). The tree half of reading an anchor, asked where an operation is new against a known
    /// tree; the document read itself stays tree-free (see the class summary). <paramref name="at"/> is the list's own
    /// path: <see cref="OperationsPath"/> for an adjustment, <c>$.overlay</c> for a document.
    /// </summary>
    public static IReadOnlyList<PrototypeError> CheckParts(
        IReadOnlyList<PrototypeOperation> operations, MockupDocument tree, string at = OperationsPath)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(at);

        var errors = new List<PrototypeError>();
        for (var i = 0; i < operations.Count; i++)
        {
            if (PrototypeRenderer.AnchorOf(operations[i]) is { } anchor && PrototypeParts.Refusal(tree, anchor) is { } refusal)
            {
                errors.Add(new PrototypeError(
                    Member(Member($"{at}[{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}]", "anchor"), refusal.Field),
                    refusal.Message));
            }
        }

        return errors;
    }

    /// <summary>One read. Holds the errors found as the document is walked.</summary>
    private sealed class Pass
    {
        private readonly List<PrototypeError> errors = [];

        public async Task<PrototypeReadResult> RunAsync(JsonElement root, IWikiLibrary wiki, CancellationToken ct)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                Error("$", $"A prototype is a JSON object that starts {{\"prototype\": {PrototypeVocabulary.Version}, ...}}; "
                    + $"this is {Describe(root)}.");
                return Result(null);
            }

            var properties = Properties(root, "$", PrototypeVocabulary.RootProperties, "the document");

            if (!properties.TryGetValue("prototype", out var version))
            {
                Error("$", $"This has no \"prototype\" version. A prototype starts {{\"prototype\": "
                    + $"{PrototypeVocabulary.Version}, ...}}, which says it is a prototype and which version of "
                    + "the schema it is written in.");
                return Result(null);
            }

            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
            {
                Error("$.prototype", $"The version is a whole number; this build reads {PrototypeVocabulary.Version}, "
                    + $"and this is {Describe(version)}.");
                return Result(null);
            }

            if (number != PrototypeVocabulary.Version)
            {
                Error("$.prototype", $"This is prototype version {number}, and this build reads version "
                    + $"{PrototypeVocabulary.Version} only.");
                return Result(null);
            }

            PrototypeDesignRef? design = null;
            if (!properties.TryGetValue("design", out var designElement))
            {
                Error("$", "This has no \"design\". A prototype names the design it is generated from: "
                    + "\"design\": {\"number\": 12, \"version\": 1}.");
            }
            else
            {
                design = await DesignRefAsync(designElement, wiki, ct).ConfigureAwait(false);
            }

            var overlay = properties.TryGetValue("overlay", out var overlayElement)
                ? Overlay(overlayElement, "$.overlay", "\"overlay\"")
                : [];

            return Result(design is null ? null : new PrototypeDocument(number, design, overlay));
        }

        public PrototypeOperationsRead Operations(JsonElement root)
        {
            var list = Overlay(root, OperationsPath, "\"operations\"");
            if (errors.Count == 0 && list.Count == 0)
            {
                Error(OperationsPath, "\"operations\" lists at least one operation; an adjustment with none changes nothing.");
            }

            return errors.Count == 0
                ? new PrototypeOperationsRead(list, [])
                : new PrototypeOperationsRead(null, errors.ToArray());
        }

        private async Task<PrototypeDesignRef?> DesignRefAsync(JsonElement element, IWikiLibrary wiki, CancellationToken ct)
        {
            const string path = "$.design";
            if (element.ValueKind != JsonValueKind.Object)
            {
                Error(path, "\"design\" is an object naming the design this prototype is generated from, for "
                    + $"example {{\"number\": 12, \"version\": 1}}; this is {Describe(element)}.");
                return null;
            }

            var properties = Properties(element, path, PrototypeVocabulary.DesignProperties, "a design reference");

            int? number = null;
            if (!properties.TryGetValue("number", out var numberElement))
            {
                Error(path, "A design reference needs \"number\": the design document's number.");
            }
            else if (numberElement.ValueKind != JsonValueKind.Number || !numberElement.TryGetInt32(out var n) || n <= 0)
            {
                Error(Member(path, "number"), $"\"number\" is a positive whole number; this is {Describe(numberElement)}.");
            }
            else
            {
                number = n;
            }

            int? version = null;
            if (!properties.TryGetValue("version", out var versionElement))
            {
                Error(path, "A design reference needs \"version\": the design's version this prototype was "
                    + "generated from.");
            }
            else if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var v) || v <= 0)
            {
                Error(Member(path, "version"), $"\"version\" is a positive whole number; this is {Describe(versionElement)}.");
            }
            else
            {
                version = v;
            }

            if (number is not { } designNumber || version is not { } designVersion)
            {
                return null;
            }

            var item = await wiki.GetByNumberAsync(designNumber, ct).ConfigureAwait(false);
            if (item is null)
            {
                Error(Member(path, "number"), $"There is no wiki document numbered {designNumber}.");
                return null;
            }

            if (item.Type != WikiItemType.Design)
            {
                Error(Member(path, "number"), $"@{designNumber} is a {WikiNames.ToName(item.Type)}, not a design; "
                    + "a prototype is generated from a design.");
                return null;
            }

            if (designVersion > item.Version)
            {
                Error(Member(path, "version"), $"Design @{designNumber} has no version {designVersion}; it is at "
                    + $"version {item.Version}.");
                return null;
            }

            return new PrototypeDesignRef(designNumber, designVersion);
        }

        private List<PrototypeOperation> Overlay(JsonElement element, string at, string name)
        {
            var list = new List<PrototypeOperation>();
            if (element.ValueKind != JsonValueKind.Array)
            {
                Error(at, $"{name} is a list of operations; this is {Describe(element)}.");
                return list;
            }

            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var path = $"{at}[{index++}]";
                if (Operation(item, path) is { } operation)
                {
                    list.Add(operation);
                }
            }

            return list;
        }

        private PrototypeOperation? Operation(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Error(path, $"An overlay operation is an object with an \"op\"; this is {Describe(element)}.");
                return null;
            }

            if (!element.TryGetProperty("op", out var opElement))
            {
                Error(path, $"This operation has no \"op\". It is one of {Options(PrototypeVocabulary.Operations)}.");
                return null;
            }

            if (opElement.ValueKind != JsonValueKind.String
                || !PrototypeVocabulary.Operations.Contains(opElement.GetString(), StringComparer.Ordinal))
            {
                Error(Member(path, "op"), $"{Quote(opElement)} is not an operation. The operations are "
                    + $"{Options(PrototypeVocabulary.Operations)}.");
                return null;
            }

            var op = opElement.GetString()!;
            var allowed = PrototypeVocabulary.FieldsOf(op);
            var properties = OperationProperties(element, path, op, allowed);

            PrototypeAnchor? anchor = null;
            if (!PrototypeVocabulary.Unanchored.Contains(op))
            {
                if (!properties.TryGetValue("anchor", out var anchorElement))
                {
                    Error(path, "This operation has no \"anchor\": name the node it applies to by \"id\" or \"path\".");
                    return null;
                }

                anchor = Anchor(anchorElement, Member(path, "anchor"));
                if (anchor is null)
                {
                    return null;
                }
            }

            return op switch
            {
                PrototypeVocabulary.SetText => SetText(properties, path, anchor!),
                PrototypeVocabulary.SetCss => SetCss(properties, path, anchor!),
                PrototypeVocabulary.SetAttribute => SetAttribute(properties, path, anchor!),
                PrototypeVocabulary.AddClass => AddClass(properties, path, anchor!),
                PrototypeVocabulary.PageCss => PageCss(properties, path),
                PrototypeVocabulary.InsertHtml => InsertHtml(properties, path, anchor!),
                _ => null,
            };
        }

        private PrototypeAnchor? Anchor(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Error(path, $"An anchor is an object naming a node, by \"id\" or \"path\"; this is {Describe(element)}.");
                return null;
            }

            var properties = Properties(element, path, PrototypeVocabulary.AnchorProperties, "an anchor");
            var hasId = properties.TryGetValue("id", out var idElement);
            var hasPath = properties.TryGetValue("path", out var pathElement);

            if (hasId == hasPath)
            {
                Error(path, hasId
                    ? "An anchor names a node by \"id\" or \"path\", never both."
                    : "An anchor needs exactly one of \"id\" (a node's id) or \"path\" (its tree path when it has none).");
                return null;
            }

            PrototypeAnchor? node;
            if (hasId)
            {
                if (idElement.ValueKind != JsonValueKind.String || idElement.GetString()!.Length == 0)
                {
                    Error(Member(path, "id"), $"An anchor's \"id\" is a non-empty string; this is {Describe(idElement)}.");
                    node = null;
                }
                else
                {
                    node = new PrototypeAnchor(idElement.GetString(), null);
                }
            }
            else if (pathElement.ValueKind != JsonValueKind.String
                || pathElement.GetString() is not { Length: > 0 } pointer
                || pointer[0] != '/')
            {
                Error(Member(path, "path"), "An anchor's \"path\" is a non-empty JSON Pointer rooted at the "
                    + $"design's tree, for example \"/screens/0/children/2\"; this is {Describe(pathElement)}.");
                node = null;
            }
            else
            {
                node = new PrototypeAnchor(null, pointer);
            }

            var part = Part(properties, path, out var partOk);
            return node is not null && partOk ? node with { Part = part.Part, Column = part.Column } : null;
        }

        /// <summary>
        /// An anchor's optional part (backlog 238, plan @52): one of <see cref="PrototypeVocabulary.Parts"/>, and a
        /// column header's <c>column</c> with it and with nothing else. Whether the NODE has that part is the design's
        /// fact, checked against a tree by <see cref="CheckParts"/>, never here.
        /// </summary>
        private (DesignPart Part, string? Column) Part(Dictionary<string, JsonElement> properties, string path, out bool ok)
        {
            ok = true;
            var hasPart = properties.TryGetValue("part", out var partElement);
            var hasColumn = properties.TryGetValue("column", out var columnElement);
            if (!hasPart)
            {
                if (hasColumn)
                {
                    Error(Member(path, "column"), $"\"column\" names which column header; it goes with \"part\": "
                        + $"\"{PrototypeVocabulary.ColumnHeader}\" only.");
                    ok = false;
                }

                return (DesignPart.Node, null);
            }

            if (partElement.ValueKind != JsonValueKind.String
                || !PrototypeVocabulary.TryParsePart(partElement.GetString(), out var part))
            {
                Error(Member(path, "part"), $"{Quote(partElement)} is not a part. A part is one of "
                    + $"{Options(PrototypeVocabulary.Parts)}; leave \"part\" out to address the node itself.");
                ok = false;
                return (DesignPart.Node, null);
            }

            if (part != DesignPart.ColumnHeader)
            {
                if (hasColumn)
                {
                    Error(Member(path, "column"), $"\"column\" names which column header; a \"{PrototypeVocabulary.Name(part)}\" "
                        + "part has no column.");
                    ok = false;
                }

                return (part, null);
            }

            if (!hasColumn)
            {
                Error(path, $"A \"{PrototypeVocabulary.ColumnHeader}\" part needs \"column\": the column's record key, "
                    + "as the table's columns name it.");
                ok = false;
                return (part, null);
            }

            if (columnElement.ValueKind != JsonValueKind.String || columnElement.GetString()!.Length == 0)
            {
                Error(Member(path, "column"), $"\"column\" is a non-empty string, a column's record key; this is "
                    + $"{Describe(columnElement)}.");
                ok = false;
                return (part, null);
            }

            return (part, columnElement.GetString());
        }

        private PrototypeOperation? SetText(Dictionary<string, JsonElement> properties, string path, PrototypeAnchor anchor)
        {
            if (!properties.TryGetValue("text", out var text) || text.ValueKind != JsonValueKind.String)
            {
                Error(path, "A setText operation needs \"text\": a string, the words to show.");
                return null;
            }

            return new PrototypeSetTextOperation(anchor, text.GetString()!);
        }

        private PrototypeOperation? SetCss(Dictionary<string, JsonElement> properties, string path, PrototypeAnchor anchor)
        {
            if (!properties.TryGetValue("properties", out var propsElement))
            {
                Error(path, "A setCss operation needs \"properties\": the style properties to set, for example "
                    + "{\"color\": \"red\"}.");
                return null;
            }

            if (propsElement.ValueKind != JsonValueKind.Object || !propsElement.EnumerateObject().Any())
            {
                Error(Member(path, "properties"), "\"properties\" is an object of at least one CSS property to "
                    + $"its value; this is {Describe(propsElement)}.");
                return null;
            }

            var css = new Dictionary<string, string>(StringComparer.Ordinal);
            var ok = true;
            foreach (var property in propsElement.EnumerateObject())
            {
                var at = Member(Member(path, "properties"), property.Name);
                if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length == 0)
                {
                    Error(at, $"A CSS property's value is a non-empty string; this is {Describe(property.Value)}.");
                    ok = false;
                    continue;
                }

                if (!css.TryAdd(property.Name, property.Value.GetString()!))
                {
                    Error(at, $"\"{property.Name}\" is set twice in one operation.");
                    ok = false;
                }
            }

            return ok ? new PrototypeSetCssOperation(anchor, css) : null;
        }

        private PrototypeOperation? SetAttribute(Dictionary<string, JsonElement> properties, string path, PrototypeAnchor anchor)
        {
            if (!properties.TryGetValue("name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String
                || nameElement.GetString()!.Length == 0)
            {
                Error(path, "A setAttribute operation needs \"name\": a non-empty string, the attribute to set.");
                return null;
            }

            var name = nameElement.GetString()!;
            if (!PrototypeHtmlGuard.IsAttributeName(name))
            {
                Error(Member(path, "name"), AttributeNameRefusal(name));
                return null;
            }

            if (!properties.TryGetValue("value", out var valueElement) || valueElement.ValueKind != JsonValueKind.String)
            {
                Error(path, "A setAttribute operation needs \"value\": a string, the attribute's value.");
                return null;
            }

            var value = valueElement.GetString()!;
            if (PrototypeHtmlGuard.ContainsScriptUrl(value))
            {
                Error(Member(path, "value"), "This value contains a javascript: or vbscript: URL, and an overlay "
                    + "never sets one. An overlay adjusts how a wireframe looks, never what runs.");
                return null;
            }

            return new PrototypeSetAttributeOperation(anchor, name, value);
        }

        private PrototypeOperation? AddClass(Dictionary<string, JsonElement> properties, string path, PrototypeAnchor anchor)
        {
            if (!properties.TryGetValue("class", out var classElement)
                || classElement.ValueKind != JsonValueKind.String
                || classElement.GetString()!.Length == 0)
            {
                Error(path, "An addClass operation needs \"class\": a non-empty class name.");
                return null;
            }

            return new PrototypeAddClassOperation(anchor, classElement.GetString()!);
        }

        private PrototypeOperation? PageCss(Dictionary<string, JsonElement> properties, string path)
        {
            if (!properties.TryGetValue("rule", out var ruleElement)
                || ruleElement.ValueKind != JsonValueKind.String
                || ruleElement.GetString()!.Length == 0)
            {
                Error(path, "A pageCss operation needs \"rule\": a non-empty CSS rule.");
                return null;
            }

            var rule = ruleElement.GetString()!;
            if (PrototypeHtmlGuard.ContainsStyleBreakout(rule))
            {
                Error(Member(path, "rule"), "This rule contains \"</style\", which would close the style element "
                    + "it lands inside; a page-level rule cannot carry that.");
                return null;
            }

            return new PrototypePageCssOperation(rule);
        }

        private PrototypeOperation? InsertHtml(Dictionary<string, JsonElement> properties, string path, PrototypeAnchor anchor)
        {
            PrototypePlacement? placement = null;
            if (!properties.TryGetValue("placement", out var placementElement))
            {
                Error(path, $"An insertHtml operation needs \"placement\": {Options(PrototypeVocabulary.Placements)}.");
            }
            else if (placementElement.ValueKind != JsonValueKind.String
                || !PrototypeVocabulary.TryParsePlacement(placementElement.GetString(), out var parsedPlacement))
            {
                Error(Member(path, "placement"), $"{Quote(placementElement)} is not a placement. It is "
                    + $"{Options(PrototypeVocabulary.Placements)}.");
            }
            else
            {
                placement = parsedPlacement;
            }

            if (!properties.TryGetValue("html", out var htmlElement)
                || htmlElement.ValueKind != JsonValueKind.String
                || htmlElement.GetString()!.Length == 0)
            {
                Error(path, "An insertHtml operation needs \"html\": a non-empty fragment of HTML.");
                return null;
            }

            var html = htmlElement.GetString()!;
            if (PrototypeHtmlGuard.FragmentRefusal(html) is { } refusal)
            {
                Error(Member(path, "html"), refusal);
                return null;
            }

            return placement is { } p ? new PrototypeInsertHtmlOperation(anchor, p, html) : null;
        }

        /// <summary>Reads an operation's own properties, refusing the unknown, the misplaced and the repeated.</summary>
        private Dictionary<string, JsonElement> OperationProperties(
            JsonElement element, string path, string op, IReadOnlyList<string> allowed)
        {
            var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == "op")
                {
                    continue;
                }

                var at = Member(path, property.Name);
                if (found.ContainsKey(property.Name))
                {
                    Error(at, $"\"{property.Name}\" appears twice in one operation.");
                    continue;
                }

                if (!PrototypeVocabulary.OperationFields.Contains(property.Name))
                {
                    Error(at, $"\"{property.Name}\" is not a property of the overlay schema. A \"{op}\" operation "
                        + $"may carry {Options(allowed)}.");
                    continue;
                }

                if (!allowed.Contains(property.Name))
                {
                    var owners = PrototypeVocabulary.Operations.Where(o => PrototypeVocabulary.FieldsOf(o).Contains(property.Name));
                    Error(at, $"A \"{op}\" operation cannot carry \"{property.Name}\"; it belongs on {Options(owners)}. "
                        + $"A \"{op}\" operation may carry {Options(allowed)}.");
                    continue;
                }

                found[property.Name] = property.Value;
            }

            return found;
        }

        /// <summary>Reads a fixed-shape object's properties: anything not in <paramref name="allowed"/> is an error.</summary>
        private Dictionary<string, JsonElement> Properties(JsonElement element, string path, IReadOnlyList<string> allowed, string what)
        {
            var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var at = Member(path, property.Name);
                if (found.ContainsKey(property.Name))
                {
                    Error(at, $"\"{property.Name}\" appears twice.");
                }
                else if (!allowed.Contains(property.Name))
                {
                    Error(at, $"\"{property.Name}\" is not a property of {what}, which may carry {Options(allowed)}.");
                }
                else
                {
                    found[property.Name] = property.Value;
                }
            }

            return found;
        }

        private PrototypeReadResult Result(PrototypeDocument? document) =>
            errors.Count == 0 && document is not null
                ? new PrototypeReadResult(document, [])
                : new PrototypeReadResult(null, errors.ToArray());

        private void Error(string path, string message) => errors.Add(new PrototypeError(path, message));
    }

    /// <summary>
    /// Why <see cref="PrototypeHtmlGuard.IsAttributeName"/> refused <paramref name="name"/>, picked to name
    /// the specific reason (an event handler, the renderer's reserved name, or an unsafe shape) rather than
    /// one generic sentence for all three.
    /// </summary>
    private static string AttributeNameRefusal(string name)
    {
        if (name.Length > 2 && name.StartsWith("on", StringComparison.OrdinalIgnoreCase) && char.IsLetter(name[2]))
        {
            return $"\"{name}\" is an event-handler attribute, and an overlay never sets one. An overlay "
                + "adjusts how a wireframe looks, never what runs.";
        }

        if (string.Equals(name, PrototypeHtmlGuard.ReservedAttributeName, StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{name}\" is the attribute the renderer names every element by; an overlay never sets it.";
        }

        if (name.StartsWith(PrototypeHtmlGuard.ReservedAttributePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{name}\" is in the data-mk- namespace, the renderer's own wiring of the page; an overlay "
                + "never sets one.";
        }

        return $"\"{name}\" is not a plain attribute name: only letters, digits, \"-\", \"_\", \":\" and \".\", "
            + "starting with a letter.";
    }

    /// <summary>A member's JSON path, spelled the way every error names one.</summary>
    internal static string Member(string path, string name) =>
        IsSimpleName(name) ? $"{path}.{name}" : $"{path}[{JsonSerializer.Serialize(name)}]";

    private static bool IsSimpleName(string name) =>
        name.Length > 0
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static string Quote(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? $"\"{element.GetString()}\"" : Describe(element);

    private static string Options(IEnumerable<string> names)
    {
        var list = names.Select(n => $"\"{n}\"").ToList();
        return list.Count switch
        {
            0 => "none",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " or " + list[^1],
        };
    }

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => element.GetArrayLength() == 0 ? "an empty list" : "a list",
        JsonValueKind.String => $"the string \"{element.GetString()}\"",
        JsonValueKind.Number => $"the number {element.GetRawText()}",
        JsonValueKind.True or JsonValueKind.False => $"the boolean {element.GetRawText()}",
        JsonValueKind.Null => "null",
        _ => element.ValueKind.ToString().ToLowerInvariant(),
    };
}
