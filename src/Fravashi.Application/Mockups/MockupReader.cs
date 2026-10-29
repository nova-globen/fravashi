using System.Globalization;
using System.Text.Json;

namespace Fravashi.Application.Mockups;

/// <summary>
/// Reads a mockup tree from JSON, and is the ONLY validator of one: parsing and checking are one pass, so
/// there is no way to hold a <see cref="MockupDocument"/> that broke a rule.
/// <para>
/// <b>Strict, unlike the turn envelope.</b> <see cref="Sessions.AgentTurnDocumentSchema"/> is permissive
/// because every refusal there burns one of the CLI's five attempts. A mockup is the opposite case: it is a
/// stored compatibility surface (ADR 0021) that an editor, a renderer and a verifier all read, so a misspelt
/// property quietly ignored is a design quietly changed. Here an unknown property is an error that names
/// what is allowed.
/// </para>
/// <para>
/// <b>Every error, not the first.</b> The reader keeps going past a mistake wherever the rest can still be
/// judged, so an agent repairs a tree in one round rather than one error per round. Each error names its
/// place as a JSON path and says what would be allowed instead.
/// </para>
/// </summary>
public static class MockupReader
{
    /// <summary>Reads and validates a tree.</summary>
    public static MockupReadResult Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        }
        catch (JsonException ex)
        {
            return new MockupReadResult(null, [new MockupError("$", $"This is not valid JSON: {ex.Message}")]);
        }

        using (parsed)
        {
            return new Pass().Run(parsed.RootElement);
        }
    }

    /// <summary>One read. Holds what a later rule needs from an earlier part of the tree.</summary>
    private sealed class Pass
    {
        private readonly List<MockupError> errors = [];
        private readonly Dictionary<string, Declared> ids = new(StringComparer.Ordinal);
        private readonly HashSet<string> screens = new(StringComparer.Ordinal);
        private readonly HashSet<string> viewports = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<JsonElement>> data = new(StringComparer.Ordinal);
        private readonly List<(string Path, MockupAction Action)> actions = [];
        private int nodes;
        private bool tooMany;

        public MockupReadResult Run(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                Error("$", $"A mockup is a JSON object that starts {{\"mockup\": {MockupVocabulary.Version}, ...}}; "
                    + $"this is {Describe(root)}.");
                return Result(null);
            }

            var properties = Properties(root, "$", MockupVocabulary.RootProperties, "the document");

            if (!properties.TryGetValue("mockup", out var version))
            {
                Error("$", $"This has no \"mockup\" version. A mockup starts {{\"mockup\": {MockupVocabulary.Version}, ...}}, "
                    + "which says it is a mockup and which version of the schema it is written in.");
                return Result(null);
            }

            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
            {
                Error("$.mockup", $"The version is a whole number; this build reads {MockupVocabulary.Version}, "
                    + $"and this is {Describe(version)}.");
                return Result(null);
            }

            if (number != MockupVocabulary.Version)
            {
                // A different version has different rules, so judging the rest by these ones would only
                // produce errors that are not true of it.
                Error("$.mockup", $"This is mockup version {number}, and this build reads version "
                    + $"{MockupVocabulary.Version} only.");
                return Result(null);
            }

            var title = properties.TryGetValue("title", out var t) ? String(t, "$.title") : null;

            // Viewports and data first: nodes refer to both, whatever order the author wrote the root in.
            var viewportList = properties.TryGetValue("viewports", out var v) ? Viewports(v) : [];
            var dataSets = properties.TryGetValue("data", out var d) ? DataSets(d) : [];

            var screenList = new List<MockupNode>();
            if (!properties.TryGetValue("screens", out var s))
            {
                Error("$", "This has no \"screens\". A mockup has at least one screen: "
                    + "\"screens\": [{\"type\": \"window\", \"id\": \"main\", ...}].");
            }
            else if (s.ValueKind != JsonValueKind.Array || s.GetArrayLength() == 0)
            {
                Error("$.screens", $"\"screens\" is a list of at least one window; this is {Describe(s)}.");
            }
            else
            {
                // Screen ids are collected before any node is read, so a "go" can name a later screen.
                foreach (var screen in s.EnumerateArray())
                {
                    if (screen.ValueKind == JsonValueKind.Object
                        && screen.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String)
                    {
                        screens.Add(id.GetString()!);
                    }
                }

                var index = 0;
                foreach (var screen in s.EnumerateArray())
                {
                    var path = $"$.screens[{index++}]";
                    if (Node(screen, path, depth: 0, owner: null, parent: null) is { } node)
                    {
                        screenList.Add(node);
                    }
                }
            }

            CheckActions();

            return Result(new MockupDocument(number, title, viewportList, dataSets, screenList));
        }

        private MockupReadResult Result(MockupDocument? document) =>
            errors.Count == 0 && document is not null
                ? new MockupReadResult(document, [])
                : new MockupReadResult(null, errors.ToArray());

        private void Error(string path, string message) => errors.Add(new MockupError(path, message));

        private List<MockupViewport> Viewports(JsonElement element)
        {
            var list = new List<MockupViewport>();
            if (element.ValueKind != JsonValueKind.Array)
            {
                Error("$.viewports", "\"viewports\" is a list of {\"name\", \"width\"} objects, "
                    + $"for example [{{\"name\": \"wide\", \"width\": 1280}}, {{\"name\": \"narrow\", \"width\": 390}}]; "
                    + $"this is {Describe(element)}.");
                return list;
            }

            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var path = $"$.viewports[{index++}]";
                if (item.ValueKind != JsonValueKind.Object)
                {
                    Error(path, $"A viewport is {{\"name\", \"width\"}}; this is {Describe(item)}.");
                    continue;
                }

                var properties = Properties(item, path, MockupVocabulary.ViewportProperties, "a viewport");
                var name = properties.TryGetValue("name", out var n) ? Name(n, $"{path}.name", "A viewport name") : null;
                int? width = null;
                if (!properties.TryGetValue("width", out var w))
                {
                    Error(path, "A viewport needs a \"width\" in CSS pixels.");
                }
                else if (w.ValueKind != JsonValueKind.Number
                    || !w.TryGetInt32(out var pixels)
                    || pixels < MockupVocabulary.MinViewportWidth
                    || pixels > MockupVocabulary.MaxViewportWidth)
                {
                    Error($"{path}.width", $"A viewport width is a whole number of CSS pixels from "
                        + $"{MockupVocabulary.MinViewportWidth} to {MockupVocabulary.MaxViewportWidth}; this is {Describe(w)}.");
                }
                else
                {
                    width = pixels;
                }

                if (name is null && !properties.ContainsKey("name"))
                {
                    Error(path, "A viewport needs a \"name\", which a node's \"at\" refers to.");
                }

                if (name is not null && !viewports.Add(name))
                {
                    Error($"{path}.name", $"The viewport \"{name}\" is declared twice.");
                }

                if (name is not null && width is { } value)
                {
                    list.Add(new MockupViewport(name, value));
                }
            }

            return list;
        }

        private List<MockupDataSet> DataSets(JsonElement element)
        {
            var list = new List<MockupDataSet>();
            if (element.ValueKind != JsonValueKind.Object)
            {
                Error("$.data", "\"data\" is an object of named sample data sets, each a list of records, "
                    + $"for example {{\"people\": [{{\"name\": \"Ada\"}}]}}; this is {Describe(element)}.");
                return list;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var set in element.EnumerateObject())
            {
                var path = Member("$.data", set.Name);
                if (!seen.Add(set.Name))
                {
                    Error(path, $"The data set \"{set.Name}\" is declared twice.");
                    continue;
                }

                if (!MockupVocabulary.IsName(set.Name))
                {
                    Error(path, NameRule($"The data set name \"{set.Name}\""));
                    continue;
                }

                if (set.Value.ValueKind != JsonValueKind.Array)
                {
                    Error(path, $"A data set is a list of records (JSON objects); this is {Describe(set.Value)}.");
                    continue;
                }

                var records = new List<JsonElement>();
                var index = 0;
                foreach (var record in set.Value.EnumerateArray())
                {
                    if (Record(record, $"{path}[{index++}]"))
                    {
                        records.Add(record.Clone());
                    }
                }

                data[set.Name] = records;
                list.Add(new MockupDataSet(set.Name, records));
            }

            return list;
        }

        /// <summary>
        /// A record is an object whose values are strings, numbers, booleans or null, or lists of records
        /// (which is how a tree's data nests).
        /// </summary>
        private bool Record(JsonElement record, string path)
        {
            if (record.ValueKind != JsonValueKind.Object)
            {
                Error(path, $"A record is a JSON object, for example {{\"name\": \"Ada\", \"role\": \"Admin\"}}; "
                    + $"this is {Describe(record)}.");
                return false;
            }

            var ok = true;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in record.EnumerateObject())
            {
                var fieldPath = Member(path, field.Name);
                if (!seen.Add(field.Name))
                {
                    Error(fieldPath, $"The key \"{field.Name}\" appears twice in one record.");
                    ok = false;
                    continue;
                }

                switch (field.Value.ValueKind)
                {
                    case JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True
                        or JsonValueKind.False or JsonValueKind.Null:
                        break;
                    case JsonValueKind.Array:
                        var index = 0;
                        foreach (var nested in field.Value.EnumerateArray())
                        {
                            ok &= Record(nested, $"{fieldPath}[{index++}]");
                        }

                        break;
                    default:
                        Error(fieldPath, "A record value is a string, a number, a boolean, null, or a list of "
                            + $"records; this is {Describe(field.Value)}.");
                        ok = false;
                        break;
                }
            }

            return ok;
        }

        /// <summary>
        /// Reads one node. <paramref name="owner"/> is set when this is an override (a value of "states" or
        /// "at"), and is the node it overrides; <paramref name="parent"/> is the containing component.
        /// </summary>
        private MockupNode? Node(JsonElement element, string path, int depth, MockupNode? owner, MockupComponent? parent)
        {
            var isOverride = owner is not null;

            if (element.ValueKind != JsonValueKind.Object)
            {
                Error(path, isOverride
                    ? $"An override is an object of the properties that change; this is {Describe(element)}."
                    : $"A node is an object with a \"type\", for example {{\"type\": \"button\", \"text\": \"Save\"}}; "
                        + $"this is {Describe(element)}.");
                return null;
            }

            if (depth > MockupVocabulary.MaxDepth)
            {
                Error(path, $"The tree is nested deeper than {MockupVocabulary.MaxDepth} levels here. A wireframe "
                    + "does not need that; flatten it.");
                return null;
            }

            if (++nodes > MockupVocabulary.MaxNodes)
            {
                if (!tooMany)
                {
                    tooMany = true;
                    Error(path, $"The document holds more than {MockupVocabulary.MaxNodes} nodes. Split it into "
                        + "several mockups.");
                }

                return null;
            }

            MockupComponent type;
            if (isOverride)
            {
                type = owner!.Type;
            }
            else if (!element.TryGetProperty("type", out var typeElement))
            {
                Error(path, $"This node has no \"type\". It is one of {Options(MockupVocabulary.Names<MockupComponent>())}.");
                return null;
            }
            else if (typeElement.ValueKind != JsonValueKind.String
                || !MockupVocabulary.TryParse(typeElement.GetString(), out type))
            {
                Error($"{path}.type", $"{Quote(typeElement)} is not a component. The palette is "
                    + $"{Options(MockupVocabulary.Names<MockupComponent>())}.");
                return null;
            }

            var typeName = MockupVocabulary.Name(type);
            var allowed = MockupVocabulary.PropertiesOf(type);
            var properties = NodeProperties(element, path, type, isOverride);

            var node = new MockupNode { Type = type };

            if (properties.TryGetValue("id", out var idElement))
            {
                var id = Name(idElement, $"{path}.id", "An id");
                if (id is not null)
                {
                    if (ids.TryGetValue(id, out var earlier))
                    {
                        Error($"{path}.id", $"The id \"{id}\" is already used at {earlier.Path}; an id is unique "
                            + "in a document.");
                    }
                    else
                    {
                        ids[id] = new Declared(path, type, new HashSet<MockupState>());
                    }

                    node = node with { Id = id };
                }
            }

            if (properties.TryGetValue("text", out var text))
            {
                node = node with { Text = String(text, $"{path}.text") };
            }

            if (properties.TryGetValue("href", out var href))
            {
                node = node with { Href = NonEmpty(href, $"{path}.href", "\"href\" (where the link goes)") };
            }

            if (properties.TryGetValue("layout", out var layout))
            {
                node = node with { Layout = Choice<MockupLayout>(layout, $"{path}.layout", "A layout") };
            }

            if (properties.TryGetValue("cols", out var cols))
            {
                node = node with { Cols = Whole(cols, $"{path}.cols", "\"cols\" (columns of a grid)", 1, 12) };
            }

            if (properties.TryGetValue("input", out var input))
            {
                node = node with { Input = Choice<MockupInput>(input, $"{path}.input", "An input") };
            }

            if (properties.TryGetValue("value", out var value))
            {
                if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    node = node with { Value = value.Clone() };
                }
                else
                {
                    Error($"{path}.value", $"A field's sample value is a string, a number or a boolean; this is {Describe(value)}.");
                }
            }

            if (properties.TryGetValue("placeholder", out var placeholder))
            {
                node = node with { Placeholder = String(placeholder, $"{path}.placeholder") };
            }

            if (properties.TryGetValue("required", out var required))
            {
                node = node with { Required = Boolean(required, $"{path}.required") };
            }

            if (properties.TryGetValue("options", out var options))
            {
                node = node with { Options = OptionList(options, $"{path}.options") };
            }

            if (properties.TryGetValue("data", out var dataName))
            {
                node = node with { Data = Binding(dataName, $"{path}.data") };
            }

            if (properties.TryGetValue("show", out var show))
            {
                node = node with { Show = NonEmpty(show, $"{path}.show", "\"show\" (the record key to show)") };
            }

            if (properties.TryGetValue("columns", out var columns))
            {
                node = node with { Columns = ColumnList(columns, $"{path}.columns") };
            }

            if (properties.TryGetValue("items", out var items))
            {
                node = node with { Items = ItemList(items, $"{path}.items", type) };
            }

            if (properties.TryGetValue("a11y", out var a11y))
            {
                node = node with { A11y = Accessibility(a11y, $"{path}.a11y") };
            }

            if (properties.TryGetValue("children", out var children))
            {
                node = node with { Children = Children(children, $"{path}.children", depth, type) };
            }

            // States and viewports are read after the node's own properties, so an override can be judged
            // against the node it overrides.
            if (properties.TryGetValue("states", out var states))
            {
                node = node with { States = StateMap(states, $"{path}.states", depth, node) };
            }

            if (properties.TryGetValue("at", out var at))
            {
                node = node with { At = ViewportMap(at, $"{path}.at", depth, node) };
            }

            if (properties.TryGetValue("state", out var state))
            {
                var initial = Choice<MockupState>(state, $"{path}.state", "A state");
                if (initial is { } s && (node.States is null || !node.States.ContainsKey(s)))
                {
                    Error($"{path}.state", $"This {typeName} starts in the state \"{MockupVocabulary.Name(s)}\" but "
                        + $"does not describe it; add \"states\": {{\"{MockupVocabulary.Name(s)}\": {{...}}}}.");
                }

                node = node with { State = initial };
            }

            if (properties.TryGetValue("on", out var on))
            {
                node = node with { On = EventMap(on, $"{path}.on", type) };
            }

            if (node.Id is { } declared && ids.TryGetValue(declared, out var entry) && entry.Path == path)
            {
                ids[declared] = entry with { States = node.States?.Keys.ToHashSet() ?? new HashSet<MockupState>() };
            }

            if (!isOverride)
            {
                Rules(node, path, parent);
            }
            else if (allowed.Contains("cols") && node.Cols is not null && (node.Layout ?? owner!.Layout) != MockupLayout.Grid)
            {
                Error($"{path}.cols", "\"cols\" applies to a grid layout, and neither this override nor the node "
                    + "it overrides has \"layout\": \"grid\".");
            }

            return node;
        }

        /// <summary>The rules about a node as a whole, beyond the shape of each property.</summary>
        private void Rules(MockupNode node, string path, MockupComponent? parent)
        {
            var typeName = MockupVocabulary.Name(node.Type);

            if (parent is null && node.Type != MockupComponent.Window)
            {
                Error(path, $"A screen is a window, and this is a {typeName}. Put it inside "
                    + "{\"type\": \"window\", \"id\": \"...\", \"children\": [...]}.");
            }

            if (parent is null && node.Type == MockupComponent.Window && node.Id is null)
            {
                Error(path, "A screen needs an \"id\", which is how \"go\" navigates to it.");
            }

            if (parent is not null && node.Type == MockupComponent.Window)
            {
                Error(path, "A window is a screen and stands only at the top, in \"screens\". Inside a window, "
                    + "use a panel or a container; for something that opens over it, use a dialog.");
            }

            if (parent is MockupComponent.Tabs && node.Type != MockupComponent.Panel)
            {
                Error(path, $"Tabs hold panels, one per tab, and this is a {typeName}.");
            }

            if (parent is MockupComponent.Tabs && node.Type == MockupComponent.Panel && string.IsNullOrEmpty(node.Text))
            {
                Error(path, "A panel inside tabs needs \"text\": it is the tab's label.");
            }

            if (node.Type == MockupComponent.Dialog && node.Id is null)
            {
                Error(path, "A dialog needs an \"id\", which is how \"open\" and \"close\" name it.");
            }

            if (node.Cols is not null && node.Layout != MockupLayout.Grid)
            {
                Error($"{path}.cols", "\"cols\" applies to a grid layout; add \"layout\": \"grid\" or remove \"cols\".");
            }

            switch (node.Type)
            {
                case MockupComponent.Text when string.IsNullOrEmpty(node.Text):
                    Error(path, "A text node needs \"text\": it is the words it shows.");
                    break;

                case MockupComponent.Button when string.IsNullOrEmpty(node.Text) && string.IsNullOrEmpty(node.A11y?.Label):
                    Error(path, "A button needs \"text\", or, when it shows only an icon, \"a11y\": {\"label\": \"...\"} "
                        + "so assistive technology can name it.");
                    break;

                case MockupComponent.Link when string.IsNullOrEmpty(node.Text) && string.IsNullOrEmpty(node.A11y?.Label):
                    Error(path, "A link needs \"text\", the words a person follows, or, when it shows only an icon, "
                        + "\"a11y\": {\"label\": \"...\"} so assistive technology can name it.");
                    break;

                case MockupComponent.Image when string.IsNullOrEmpty(node.A11y?.Label) && node.A11y?.Decorative != true:
                    Error(path, "An image needs \"a11y\": {\"label\": \"...\"} saying what it shows, or "
                        + "\"a11y\": {\"decorative\": true} when it shows nothing a reader needs.");
                    break;

                case MockupComponent.Field:
                    FieldRules(node, path);
                    break;

                case MockupComponent.List or MockupComponent.Tree or MockupComponent.Table:
                    CollectionRules(node, path);
                    break;
            }
        }

        private void FieldRules(MockupNode node, string path)
        {
            var input = node.Input ?? MockupInput.Text;

            // Added after an agent's tree (the team directory fixture, attempt 2) gave a search box only a
            // placeholder: a placeholder disappears on typing and is not a dependable accessible name, so a
            // field with neither a label nor an accessible label is a field assistive technology cannot name.
            if (string.IsNullOrEmpty(node.Text) && string.IsNullOrEmpty(node.A11y?.Label))
            {
                Error(path, "A field needs \"text\" (its visible label), or \"a11y\": {\"label\": \"...\"} when it "
                    + "shows none. A placeholder is not a label: it disappears as soon as somebody types.");
            }

            var choosing = input is MockupInput.Select or MockupInput.Radio;
            if (choosing && node.Options is null)
            {
                Error(path, $"A {MockupVocabulary.Name(input)} field needs \"options\": the choices it offers.");
            }

            if (!choosing && node.Options is not null)
            {
                Error($"{path}.options", $"\"options\" belongs to a select or radio field, and this field's input is "
                    + $"{MockupVocabulary.Name(input)}.");
            }

            if (input == MockupInput.Checkbox && node.Value is { } v && v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                Error($"{path}.value", "A checkbox's value is true or false.");
            }

            if (choosing && node.Options is { } options && node.Value is { ValueKind: JsonValueKind.String } chosen
                && !options.Contains(chosen.GetString()!, StringComparer.Ordinal))
            {
                Error($"{path}.value", $"\"{chosen.GetString()}\" is not one of this field's options "
                    + $"({Options(options)}).");
            }
        }

        private void CollectionRules(MockupNode node, string path)
        {
            var typeName = MockupVocabulary.Name(node.Type);

            if (node.Items is not null && node.Data is not null)
            {
                Error(path, $"A {typeName} shows either inline \"items\" or a \"data\" set, not both.");
            }

            if (node.Type == MockupComponent.Table && node.Columns is null)
            {
                Error(path, "A table needs \"columns\": [{\"key\": \"name\", \"text\": \"Name\"}, ...], one per record key it shows.");
            }

            if (node.Type != MockupComponent.Table && node.Data is not null && node.Show is null)
            {
                Error(path, $"A {typeName} bound to a data set needs \"show\": the record key whose value it lists.");
            }

            if (node.Show is not null && node.Data is null)
            {
                Error($"{path}.show", "\"show\" names a key of the bound data set, and this has no \"data\".");
            }

            var records = node.Data is { } name && data.TryGetValue(name, out var set)
                ? set
                : node.Type == MockupComponent.Table ? node.Items : null;

            if (records is null || records.Count == 0)
            {
                return;
            }

            var keys = records
                .Where(r => r.ValueKind == JsonValueKind.Object)
                .SelectMany(r => r.EnumerateObject().Select(p => p.Name))
                .ToHashSet(StringComparer.Ordinal);

            if (node.Show is { } shown && !keys.Contains(shown))
            {
                Error($"{path}.show", $"No record in \"{node.Data}\" has the key \"{shown}\"; its keys are {Options(keys)}.");
            }

            var index = 0;
            foreach (var column in node.Columns ?? [])
            {
                if (!keys.Contains(column.Key))
                {
                    Error($"{path}.columns[{index}]", $"No record has the key \"{column.Key}\", so this column would "
                        + $"always be empty; the records' keys are {Options(keys)}.");
                }

                index++;
            }
        }

        /// <summary>
        /// Reads a node's properties, refusing the unknown, the misplaced and the repeated, each in words
        /// that say what would be allowed.
        /// </summary>
        private Dictionary<string, JsonElement> NodeProperties(JsonElement element, string path, MockupComponent type, bool isOverride)
        {
            var allowed = MockupVocabulary.PropertiesOf(type);
            var typeName = MockupVocabulary.Name(type);
            var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            foreach (var property in element.EnumerateObject())
            {
                var at = Member(path, property.Name);
                if (found.ContainsKey(property.Name))
                {
                    Error(at, $"\"{property.Name}\" appears twice in one node.");
                    continue;
                }

                if (isOverride && MockupVocabulary.NotInOverride.Contains(property.Name))
                {
                    Error(at, $"An override changes how a node looks, not what it is, so it cannot carry \"{property.Name}\".");
                    continue;
                }

                if (!MockupVocabulary.NodeProperties.Contains(property.Name))
                {
                    Error(at, $"\"{property.Name}\" is not a property of the mockup schema. A {typeName} may carry "
                        + $"{Options(Usable(allowed, isOverride))}.");
                    continue;
                }

                if (!allowed.Contains(property.Name))
                {
                    var owners = Enum.GetValues<MockupComponent>()
                        .Where(c => MockupVocabulary.PropertiesOf(c).Contains(property.Name))
                        .Select(c => MockupVocabulary.Name(c));
                    Error(at, $"A {typeName} cannot carry \"{property.Name}\"; it belongs on {Options(owners)}. A "
                        + $"{typeName} may carry {Options(Usable(allowed, isOverride))}.");
                    continue;
                }

                found[property.Name] = property.Value;
            }

            return found;
        }

        private static IEnumerable<string> Usable(IReadOnlySet<string> allowed, bool isOverride) =>
            MockupVocabulary.NodeProperties.Where(p => allowed.Contains(p)
                && !(isOverride && MockupVocabulary.NotInOverride.Contains(p)));

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

        private List<MockupNode>? Children(JsonElement element, string path, int depth, MockupComponent type)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                Error(path, $"\"children\" is a list of nodes; this is {Describe(element)}.");
                return null;
            }

            var list = new List<MockupNode>();
            var index = 0;
            foreach (var child in element.EnumerateArray())
            {
                if (Node(child, $"{path}[{index++}]", depth + 1, owner: null, parent: type) is { } node)
                {
                    list.Add(node);
                }
            }

            return list;
        }

        private Dictionary<MockupState, MockupNode>? StateMap(JsonElement element, string path, int depth, MockupNode owner)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Error(path, "\"states\" maps a state name to what changes in it, for example "
                    + $"{{\"disabled\": {{\"text\": \"Saving...\"}}}}; this is {Describe(element)}.");
                return null;
            }

            var map = new Dictionary<MockupState, MockupNode>();
            foreach (var property in element.EnumerateObject())
            {
                var at = Member(path, property.Name);
                if (!MockupVocabulary.TryParse<MockupState>(property.Name, out var state))
                {
                    Error(at, $"\"{property.Name}\" is not a state. The states are "
                        + $"{Options(MockupVocabulary.Names<MockupState>())}.");
                    continue;
                }

                if (map.ContainsKey(state))
                {
                    Error(at, $"The state \"{property.Name}\" is described twice.");
                    continue;
                }

                if (Node(property.Value, at, depth + 1, owner, parent: null) is { } node)
                {
                    map[state] = node;
                }
            }

            return map;
        }

        private Dictionary<string, MockupNode>? ViewportMap(JsonElement element, string path, int depth, MockupNode owner)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Error(path, "\"at\" maps a viewport name to what changes at that width, for example "
                    + $"{{\"narrow\": {{\"layout\": \"column\"}}}}; this is {Describe(element)}.");
                return null;
            }

            var map = new Dictionary<string, MockupNode>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var at = Member(path, property.Name);
                if (!viewports.Contains(property.Name))
                {
                    Error(at, viewports.Count == 0
                        ? $"\"{property.Name}\" is not a declared viewport, and this document declares none; add "
                            + "\"viewports\": [{\"name\": \"" + property.Name + "\", \"width\": ...}] at the root."
                        : $"\"{property.Name}\" is not a declared viewport. The viewports are {Options(viewports)}.");
                    continue;
                }

                if (map.ContainsKey(property.Name))
                {
                    Error(at, $"The viewport \"{property.Name}\" is described twice.");
                    continue;
                }

                if (Node(property.Value, at, depth + 1, owner, parent: null) is { } node)
                {
                    map[property.Name] = node;
                }
            }

            return map;
        }

        private Dictionary<MockupEvent, MockupAction>? EventMap(JsonElement element, string path, MockupComponent type)
        {
            var typeName = MockupVocabulary.Name(type);
            if (element.ValueKind != JsonValueKind.Object)
            {
                Error(path, "\"on\" maps an event to what it does, for example {\"click\": {\"go\": \"home\"}}; "
                    + $"this is {Describe(element)}.");
                return null;
            }

            var map = new Dictionary<MockupEvent, MockupAction>();
            foreach (var property in element.EnumerateObject())
            {
                var at = Member(path, property.Name);
                if (!MockupVocabulary.TryParse<MockupEvent>(property.Name, out var @event))
                {
                    Error(at, $"\"{property.Name}\" is not an event. The events are "
                        + $"{Options(MockupVocabulary.Names<MockupEvent>())}.");
                    continue;
                }

                if (!MockupVocabulary.Raises(type, @event))
                {
                    Error(at, $"A {typeName} does not raise \"{property.Name}\"; "
                        + $"{Options(MockupVocabulary.Raising(@event).Select(c => MockupVocabulary.Name(c)))} do.");
                    continue;
                }

                if (map.ContainsKey(@event))
                {
                    Error(at, $"The event \"{property.Name}\" is described twice.");
                    continue;
                }

                if (Action(property.Value, at) is { } action)
                {
                    map[@event] = action;
                }
            }

            return map;
        }

        private MockupAction? Action(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Error(path, "An action is an object of go, open, close and set, for example {\"open\": \"confirm\"}; "
                    + $"this is {Describe(element)}.");
                return null;
            }

            var properties = Properties(element, path, MockupVocabulary.ActionProperties, "an action");
            if (properties.Count == 0)
            {
                if (!element.EnumerateObject().Any())
                {
                    Error(path, "An action does something: give it at least one of "
                        + $"{Options(MockupVocabulary.ActionProperties)}.");
                }

                return null;
            }

            var go = properties.TryGetValue("go", out var g) ? Name(g, $"{path}.go", "A screen id") : null;
            var open = properties.TryGetValue("open", out var o) ? Name(o, $"{path}.open", "A dialog id") : null;
            var close = properties.TryGetValue("close", out var c) ? Name(c, $"{path}.close", "A dialog id") : null;

            Dictionary<string, string>? set = null;
            if (properties.TryGetValue("set", out var s))
            {
                if (s.ValueKind != JsonValueKind.Object || !s.EnumerateObject().Any())
                {
                    Error($"{path}.set", "\"set\" maps node ids to the state each is then shown in, for example "
                        + $"{{\"save\": \"loading\"}}; this is {Describe(s)}.");
                }
                else
                {
                    set = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var target in s.EnumerateObject())
                    {
                        var at = Member($"{path}.set", target.Name);
                        if (target.Value.ValueKind != JsonValueKind.String)
                        {
                            Error(at, $"A state name is a string; this is {Describe(target.Value)}.");
                        }
                        else if (!set.TryAdd(target.Name, target.Value.GetString()!))
                        {
                            Error(at, $"\"{target.Name}\" is set twice in one action.");
                        }
                    }
                }
            }

            var action = new MockupAction(go, open, close, set);
            actions.Add((path, action));
            return action;
        }

        /// <summary>Every target an action names, checked once the whole tree has been read.</summary>
        private void CheckActions()
        {
            foreach (var (path, action) in actions)
            {
                if (action.Go is { } go && !screens.Contains(go))
                {
                    Error($"{path}.go", $"There is no screen \"{go}\". The screens are {Options(screens)}.");
                }

                DialogTarget(action.Open, $"{path}.open");
                DialogTarget(action.Close, $"{path}.close");

                foreach (var (id, stateName) in action.Set ?? new Dictionary<string, string>())
                {
                    var at = Member($"{path}.set", id);
                    if (!ids.TryGetValue(id, out var target))
                    {
                        Error(at, $"There is no node with the id \"{id}\".");
                        continue;
                    }

                    if (stateName == MockupVocabulary.DefaultState)
                    {
                        continue;
                    }

                    if (!MockupVocabulary.TryParse<MockupState>(stateName, out var state))
                    {
                        Error(at, $"\"{stateName}\" is not a state. The states are "
                            + $"{Options(MockupVocabulary.Names<MockupState>())}, or \"{MockupVocabulary.DefaultState}\" "
                            + "for how it looks at rest.");
                    }
                    else if (!target.States.Contains(state))
                    {
                        Error(at, $"The node \"{id}\" (at {target.Path}) does not describe the state \"{stateName}\"; "
                            + $"add it to that node's \"states\".");
                    }
                }
            }
        }

        private void DialogTarget(string? id, string path)
        {
            if (id is null)
            {
                return;
            }

            if (!ids.TryGetValue(id, out var target))
            {
                Error(path, $"There is no dialog with the id \"{id}\".");
            }
            else if (target.Type != MockupComponent.Dialog)
            {
                Error(path, $"\"{id}\" is a {MockupVocabulary.Name(target.Type)}, and only a dialog opens and closes.");
            }
        }

        private MockupA11y? Accessibility(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Error(path, "\"a11y\" is an object, for example {\"label\": \"Close\"}; "
                    + $"this is {Describe(element)}.");
                return null;
            }

            var properties = Properties(element, path, MockupVocabulary.A11yProperties, "a11y");
            var label = properties.TryGetValue("label", out var l) ? NonEmpty(l, $"{path}.label", "An accessible label") : null;
            var heading = properties.TryGetValue("heading", out var h) ? Whole(h, $"{path}.heading", "A heading level", 1, 6) : null;
            string? live = null;
            if (properties.TryGetValue("live", out var lv))
            {
                live = lv.ValueKind == JsonValueKind.String && MockupVocabulary.LiveValues.Contains(lv.GetString()!)
                    ? lv.GetString()
                    : null;
                if (live is null)
                {
                    Error($"{path}.live", $"\"live\" is {Options(MockupVocabulary.LiveValues)}; this is {Describe(lv)}.");
                }
            }

            var decorative = properties.TryGetValue("decorative", out var d) ? Boolean(d, $"{path}.decorative") : null;
            if (decorative == true && label is not null)
            {
                Error(path, "A node is either decorative or labelled, not both: a decorative node is skipped by "
                    + "assistive technology, so its label would never be read.");
            }

            return new MockupA11y(label, heading, live, decorative);
        }

        private List<string>? OptionList(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
            {
                Error(path, $"\"options\" is a list of at least one choice, each a string; this is {Describe(element)}.");
                return null;
            }

            var list = new List<string>();
            var index = 0;
            foreach (var option in element.EnumerateArray())
            {
                var at = $"{path}[{index++}]";
                if (option.ValueKind != JsonValueKind.String || option.GetString()!.Length == 0)
                {
                    Error(at, $"An option is a non-empty string; this is {Describe(option)}.");
                }
                else if (list.Contains(option.GetString()!, StringComparer.Ordinal))
                {
                    Error(at, $"The option \"{option.GetString()}\" is listed twice.");
                }
                else
                {
                    list.Add(option.GetString()!);
                }
            }

            return list;
        }

        private List<MockupColumn>? ColumnList(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
            {
                Error(path, "\"columns\" is a list of at least one column, each a record key or "
                    + $"{{\"key\", \"text\"}}; this is {Describe(element)}.");
                return null;
            }

            var list = new List<MockupColumn>();
            var index = 0;
            foreach (var column in element.EnumerateArray())
            {
                var at = $"{path}[{index++}]";
                MockupColumn? read = null;
                if (column.ValueKind == JsonValueKind.String && column.GetString()!.Length > 0)
                {
                    read = new MockupColumn(column.GetString()!, null);
                }
                else if (column.ValueKind == JsonValueKind.Object)
                {
                    var properties = Properties(column, at, ["key", "text"], "a column");
                    var key = properties.TryGetValue("key", out var k) ? NonEmpty(k, $"{at}.key", "A column key") : null;
                    var heading = properties.TryGetValue("text", out var t) ? String(t, $"{at}.text") : null;
                    if (key is null && !properties.ContainsKey("key"))
                    {
                        Error(at, "A column needs a \"key\": the record key it shows.");
                    }

                    read = key is null ? null : new MockupColumn(key, heading);
                }
                else
                {
                    Error(at, $"A column is a record key or {{\"key\", \"text\"}}; this is {Describe(column)}.");
                }

                if (read is null)
                {
                    continue;
                }

                if (list.Any(c => c.Key == read.Key))
                {
                    Error(at, $"The column \"{read.Key}\" is listed twice.");
                }
                else
                {
                    list.Add(read);
                }
            }

            return list;
        }

        private List<JsonElement>? ItemList(JsonElement element, string path, MockupComponent type)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                Error(path, $"\"items\" is a list; this is {Describe(element)}.");
                return null;
            }

            var list = new List<JsonElement>();
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var at = $"{path}[{index++}]";
                var ok = type == MockupComponent.Table ? Record(item, at) : Item(item, at, nests: type == MockupComponent.Tree);
                if (ok)
                {
                    list.Add(item.Clone());
                }
            }

            return list;
        }

        /// <summary>A list or tree item: a string, or <c>{"text", "items"}</c>, where only a tree nests.</summary>
        private bool Item(JsonElement item, string path, bool nests)
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                return true;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                Error(path, $"An item is a string or {{\"text\": \"...\"}}; this is {Describe(item)}.");
                return false;
            }

            var properties = Properties(item, path, nests ? ["text", "items"] : ["text"], nests ? "a tree item" : "a list item");
            var ok = properties.Count == item.EnumerateObject().Count();
            if (!properties.TryGetValue("text", out var text) || text.ValueKind != JsonValueKind.String)
            {
                Error(path, "An item object needs \"text\", a string.");
                ok = false;
            }

            if (properties.TryGetValue("items", out var children))
            {
                if (children.ValueKind != JsonValueKind.Array)
                {
                    Error($"{path}.items", $"\"items\" is a list; this is {Describe(children)}.");
                    return false;
                }

                var index = 0;
                foreach (var child in children.EnumerateArray())
                {
                    ok &= Item(child, $"{path}.items[{index++}]", nests);
                }
            }

            return ok;
        }

        private string? Binding(JsonElement element, string path)
        {
            var name = NonEmpty(element, path, "\"data\" (the name of a data set)");
            if (name is null)
            {
                return null;
            }

            if (!data.ContainsKey(name))
            {
                Error(path, data.Count == 0
                    ? $"There is no data set \"{name}\", and this document declares none; add "
                        + "\"data\": {\"" + name + "\": [{...}, ...]} at the root."
                    : $"There is no data set \"{name}\". The data sets are {Options(data.Keys)}.");
            }

            return name;
        }

        private string? Name(JsonElement element, string path, string what)
        {
            if (element.ValueKind == JsonValueKind.String && MockupVocabulary.IsName(element.GetString()))
            {
                return element.GetString();
            }

            Error(path, NameRule(element.ValueKind == JsonValueKind.String ? $"{what} \"{element.GetString()}\"" : $"{what} ({Describe(element)})"));
            return null;
        }

        private static string NameRule(string subject) =>
            $"{subject} is not a valid name: it starts with a letter, holds only letters, digits, hyphens and "
            + $"underscores, and is at most {MockupVocabulary.MaxNameLength} characters.";

        private string? String(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return element.GetString();
            }

            Error(path, $"This is a string; it is {Describe(element)}.");
            return null;
        }

        private string? NonEmpty(JsonElement element, string path, string what)
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString()!.Length > 0)
            {
                return element.GetString();
            }

            Error(path, $"{what} is a non-empty string; this is {Describe(element)}.");
            return null;
        }

        private bool? Boolean(JsonElement element, string path)
        {
            if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return element.GetBoolean();
            }

            Error(path, $"This is true or false; it is {Describe(element)}.");
            return null;
        }

        private int? Whole(JsonElement element, string path, string what, int min, int max)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number) && number >= min && number <= max)
            {
                return number;
            }

            Error(path, $"{what} is a whole number from {min} to {max}; this is {Describe(element)}.");
            return null;
        }

        private T? Choice<T>(JsonElement element, string path, string what)
            where T : struct, System.Enum
        {
            if (element.ValueKind == JsonValueKind.String && MockupVocabulary.TryParse<T>(element.GetString(), out var value))
            {
                return value;
            }

            Error(path, $"{Quote(element)} is not valid here. {what} is {Options(MockupVocabulary.Names<T>())}.");
            return null;
        }
    }

    /// <summary>What an id names, for the checks made once the whole tree is read.</summary>
    private sealed record Declared(string Path, MockupComponent Type, IReadOnlySet<MockupState> States);

    /// <summary>A member's JSON path, spelled the way every error names one; the editor's refusals use it too.</summary>
    internal static string Member(string path, string name) =>
        MockupVocabulary.IsName(name) ? $"{path}.{name}" : $"{path}[{JsonSerializer.Serialize(name)}]";

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
        _ => element.ValueKind.ToString().ToLower(CultureInfo.InvariantCulture),
    };
}
