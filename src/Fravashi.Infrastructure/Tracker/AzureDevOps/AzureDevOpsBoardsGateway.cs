using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fravashi.Application.Mirroring;
using Fravashi.Domain;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Providers;

namespace Fravashi.Infrastructure.Tracker.AzureDevOps;

/// <summary>
/// Azure DevOps Boards (REST 7.1) behind <see cref="IProviderGateway"/> (backlog 143): the second adapter the
/// reconciliation machinery of item 82 can drive, built to the shape <c>AtlassianJiraGateway</c> proved against a real
/// Jira. It reads its connection at each use (<see cref="AzureDevOpsBoardConnection"/>), so a board saved in a running
/// studio is the one used.
/// <para>
/// <b>What differs from Jira, and why it is handled here and nowhere else.</b> The VERSION is the work item's
/// <c>rev</c>, and an update or a move carries a JSON Patch <c>test</c> of it, so the site refuses a stale write
/// atomically instead of the read-then-write window Jira leaves. A MOVE is a change of <c>System.State</c> to any
/// state the item's type has; there is no transition id. The STAMP that finds a creation whose answer was lost is a
/// tag, found by WIQL. The DESCRIPTION is HTML on the site, and it crosses as plain text written so that reading it back
/// gives the same text, because a reconcile compares what it sent with what it reads and a lossy round trip would look
/// like somebody editing every item on the site.
/// </para>
/// </summary>
public sealed class AzureDevOpsBoardsGateway : IProviderGateway
{
    private const string Api = "api-version=7.1";
    private const string StampPrefix = "fravashi-op-";

    // The work item type a creation uses, in order of preference across the four built-in processes (Agile, Scrum,
    // CMMI, Basic); the first one the project has wins. The same order the onboarding access check uses.
    private static readonly string[] PreferredTypes = ["Task", "Issue", "User Story", "Product Backlog Item", "Requirement", "Bug"];

    private readonly HttpClient _http;
    private readonly AzureDevOpsBoardConnection _connection;

    // Asked once per scope: a type's states do not change during one sync.
    private readonly Dictionary<string, IReadOnlyList<(string Name, string Category)>> _states = new(StringComparer.OrdinalIgnoreCase);
    private string? _createType;

    public AzureDevOpsBoardsGateway(HttpClient http, AzureDevOpsBoardConnection connection)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <inheritdoc />
    public string Connection => "Azure DevOps";

    /// <inheritdoc />
    public ProviderCapabilities Capabilities { get; } =
        new(CanWrite: true, CanSearchByOperationId: true, CanTombstone: true, CanList: true, CanOfferTransitions: true);

    /// <summary>Whether a board connection to Azure DevOps is configured right now.</summary>
    public bool IsConfigured => _connection.Current() is not null;

    /// <inheritdoc />
    public async Task<ProviderResult<ProviderItem>> CreateAsync(string payload, string operationId, CancellationToken ct = default)
    {
        if (await SettingsAsync(ct).ConfigureAwait(false) is not { } site)
        {
            return NotConfigured<ProviderItem>();
        }

        var type = await CreateTypeAsync(site, ct).ConfigureAwait(false);
        if (!type.IsOk)
        {
            return type.As<ProviderItem>();
        }

        var operations = ToOperations(payload, forCreate: true, site);
        operations.Add(Op("add", "/fields/System.Tags", Stamp(operationId)));

        var created = await SendAsync(
            site, HttpMethod.Post,
            $"{site.Organization}/{Uri.EscapeDataString(site.Project)}/_apis/wit/workitems/${Uri.EscapeDataString(type.Value!)}?{Api}",
            operations, "creating a work item", body => body, ct).ConfigureAwait(false);

        if (!created.IsOk)
        {
            return created.As<ProviderItem>();
        }

        try
        {
            return ProviderResult<ProviderItem>.Ok(await ToItemAsync(site, created.Value, ct).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            // It said yes and what it answered cannot be read. The item exists; only what it looks like is unknown.
            return ProviderResult<ProviderItem>.Indeterminate(ProviderFailure.MissingField("Azure DevOps", "creating a work item", "id"));
        }
    }

    /// <inheritdoc />
    public async Task<ProviderResult<ProviderItem>> UpdateAsync(
        string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default)
    {
        if (await SettingsAsync(ct).ConfigureAwait(false) is not { } site)
        {
            return NotConfigured<ProviderItem>();
        }

        var operations = ToOperations(payload, forCreate: false, site);
        if (operations.Count == 0)
        {
            return await GetAsync(key, ct).ConfigureAwait(false);
        }

        return await PatchAsync(site, key, providerVersion, operations, $"changing {key}", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProviderResult<ProviderItem>> TransitionAsync(
        string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default)
    {
        if (await SettingsAsync(ct).ConfigureAwait(false) is not { } site)
        {
            return NotConfigured<ProviderItem>();
        }

        if (Values(payload).GetValueOrDefault(AtlassianStatusField) is not { Length: > 0 } wanted)
        {
            return ProviderResult<ProviderItem>.Refused($"The move for {key} names no state to move to.");
        }

        var offered = await OfferedTransitionsAsync(key, ct).ConfigureAwait(false);
        if (!offered.IsOk)
        {
            return offered.As<ProviderItem>();
        }

        var match = offered.Value!.FirstOrDefault(s => string.Equals(s, wanted, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return ProviderResult<ProviderItem>.Refused(
                $"{key}'s work item type has no state '{wanted}'. It has: {string.Join(", ", offered.Value!)}.");
        }

        return await PatchAsync(site, key, providerVersion, [Op("add", "/fields/System.State", match)], $"moving {key} to '{match}'", ct)
            .ConfigureAwait(false);
    }

    /// <summary>The payload field a move names its target in, shared with the Jira adapter so a queued move reads the same.</summary>
    private const string AtlassianStatusField = "NativeStatus";

    /// <inheritdoc />
    /// <remarks>Azure DevOps lets an item move to any state its type has, so every one of them is offered.</remarks>
    public async Task<ProviderResult<IReadOnlyList<string>>> OfferedTransitionsAsync(string key, CancellationToken ct = default)
    {
        if (await SettingsAsync(ct).ConfigureAwait(false) is not { } site)
        {
            return NotConfigured<IReadOnlyList<string>>();
        }

        var item = await RawAsync(site, key, ct).ConfigureAwait(false);
        if (!item.IsOk)
        {
            return item.As<IReadOnlyList<string>>();
        }

        var type = FieldText(item.Value, "System.WorkItemType") ?? string.Empty;
        var states = await StatesAsync(site, type, ct).ConfigureAwait(false);
        return states.IsOk
            ? ProviderResult<IReadOnlyList<string>>.Ok([.. states.Value!.Select(s => s.Name)])
            : states.As<IReadOnlyList<string>>();
    }

    /// <inheritdoc />
    public async Task<ProviderResult<ProviderItem?>> FindByOperationIdAsync(string operationId, CancellationToken ct = default)
    {
        if (await SettingsAsync(ct).ConfigureAwait(false) is not { } site)
        {
            return NotConfigured<ProviderItem?>();
        }

        var ids = await QueryAsync(
            site,
            $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND ([System.Tags] CONTAINS '{Stamp(operationId)}' OR [System.Tags] CONTAINS '{Stamp(operationId, FormerNames.TrackerStampPrefix)}')",
            2, "searching for a creation's stamp", ct).ConfigureAwait(false);
        if (!ids.IsOk)
        {
            return ids.As<ProviderItem?>();
        }

        if (ids.Value!.Count == 0)
        {
            return ProviderResult<ProviderItem?>.Ok(null);
        }

        var found = await GetAsync(ids.Value[0].ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        return found.IsOk
            ? ProviderResult<ProviderItem?>.Ok(found.Value)
            : found.As<ProviderItem?>();
    }

    // The batch work item GET's own hard limit (Microsoft's, not this product's): never more than this many
    // ids in one call. WIQL itself has no such limit worth naming here; see WiqlIdCeiling below.
    private const int BatchReadSize = 200;

    // WIQL's own real ceiling on how many ids one query can answer (Azure DevOps refuses a $top past this).
    // Asking for it ONCE per import, rather than per page, is what backlog 182 means by "the WIQL query takes
    // at most 200 ids per call": that limit is the BATCH READ's, and WIQL is asked for everything up front.
    private const int WiqlIdCeiling = 19999;

    /// <inheritdoc />
    /// <remarks>
    /// Backlog 182: a cursor here is the comma-joined ids still to come. The first call (<paramref name="cursor"/>
    /// null) runs the WIQL query exactly once, for every id the project has (not a page of them); every call,
    /// first or not, then batch-reads at most <see cref="BatchReadSize"/> of the remaining ids, which is the
    /// batch endpoint's own limit, and hands the rest back as the next cursor.
    /// </remarks>
    public async Task<ProviderResult<ProviderPage>> ListAsync(string? cursor, CancellationToken ct = default)
    {
        if (await SettingsAsync(ct).ConfigureAwait(false) is not { } site)
        {
            return NotConfigured<ProviderPage>();
        }

        IReadOnlyList<int> ids;
        if (cursor is { Length: > 0 } carried)
        {
            ids = [.. carried.Split(',').Select(id => int.Parse(id, System.Globalization.CultureInfo.InvariantCulture))];
        }
        else
        {
            var found = await QueryAsync(
                site,
                "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project ORDER BY [System.ChangedDate] DESC",
                WiqlIdCeiling, "asking what the project holds", ct).ConfigureAwait(false);
            if (!found.IsOk)
            {
                return found.As<ProviderPage>();
            }

            ids = found.Value!;
        }

        if (ids.Count == 0)
        {
            return ProviderResult<ProviderPage>.Ok(new ProviderPage([], null));
        }

        var batch = ids.Take(BatchReadSize).ToList();
        var rest = ids.Skip(BatchReadSize).ToList();

        var read = await SendAsync(
            site, HttpMethod.Get,
            $"{site.Organization}/_apis/wit/workitems?ids={string.Join(',', batch)}&{Api}",
            null, "reading the project's work items",
            body => body.TryGetProperty("value", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(e => e.Clone()).ToList()
                : throw new JsonException("the answer carries no value array"),
            ct).ConfigureAwait(false);
        if (!read.IsOk)
        {
            return read.As<ProviderPage>();
        }

        var items = new List<ProviderItem>();
        try
        {
            foreach (var element in read.Value!)
            {
                items.Add(await ToItemAsync(site, element, ct).ConfigureAwait(false));
            }
        }
        catch (JsonException)
        {
            return ProviderResult<ProviderPage>.Indeterminate(
                ProviderFailure.UnreadableBody("Azure DevOps", "reading the project's work items"));
        }

        return ProviderResult<ProviderPage>.Ok(new ProviderPage(items, rest.Count > 0 ? string.Join(',', rest) : null));
    }

    /// <inheritdoc />
    public async Task<ProviderResult<ProviderItem>> GetAsync(string key, CancellationToken ct = default)
    {
        if (await SettingsAsync(ct).ConfigureAwait(false) is not { } site)
        {
            return NotConfigured<ProviderItem>();
        }

        var raw = await RawAsync(site, key, ct).ConfigureAwait(false);
        if (!raw.IsOk)
        {
            return raw.As<ProviderItem>();
        }

        try
        {
            return ProviderResult<ProviderItem>.Ok(await ToItemAsync(site, raw.Value, ct).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            return ProviderResult<ProviderItem>.Indeterminate(ProviderFailure.UnreadableBody("Azure DevOps", $"the work item {key}"));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reads and writes
    // ---------------------------------------------------------------------------------------------------------

    private Task<ProviderResult<JsonElement>> RawAsync(AzureDevOpsBoardSettings site, string key, CancellationToken ct) =>
        SendAsync(
            site, HttpMethod.Get, $"{site.Organization}/_apis/wit/workitems/{Uri.EscapeDataString(key)}?{Api}", null,
            $"the work item {key}", body => body, ct);

    private async Task<ProviderResult<ProviderItem>> PatchAsync(
        AzureDevOpsBoardSettings site, string key, string? providerVersion, List<JsonObject> operations, string subject, CancellationToken ct)
    {
        if (int.TryParse(providerVersion, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var rev))
        {
            // Atomic: the site applies the change only while the item is still at the revision we last read.
            operations.Insert(0, Op("test", "/rev", rev));
        }

        var patched = await SendAsync(
            site, HttpMethod.Patch, $"{site.Organization}/_apis/wit/workitems/{Uri.EscapeDataString(key)}?{Api}",
            operations, subject, body => body, ct).ConfigureAwait(false);

        if (!patched.IsOk)
        {
            return patched.As<ProviderItem>();
        }

        try
        {
            return ProviderResult<ProviderItem>.Ok(await ToItemAsync(site, patched.Value, ct).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            // The change was accepted and the answer cannot be read: what the item now is, is not known here.
            return ProviderResult<ProviderItem>.Indeterminate(ProviderFailure.UnreadableBody("Azure DevOps", subject));
        }
    }

    private async Task<ProviderResult<IReadOnlyList<int>>> QueryAsync(
        AzureDevOpsBoardSettings site, string wiql, int top, string subject, CancellationToken ct) =>
        await SendAsync<IReadOnlyList<int>>(
            site, HttpMethod.Post,
            $"{site.Organization}/{Uri.EscapeDataString(site.Project)}/_apis/wit/wiql?$top={top}&{Api}",
            new JsonObject { ["query"] = wiql }, subject,
            body => body.TryGetProperty("workItems", out var items) && items.ValueKind == JsonValueKind.Array
                ? [.. items.EnumerateArray().Select(i => i.GetProperty("id").GetInt32())]
                : throw new JsonException("the query answer carries no workItems array"),
            ct).ConfigureAwait(false);

    /// <summary>The work item type a creation uses: the first of <see cref="PreferredTypes"/> the project has.</summary>
    private async Task<ProviderResult<string>> CreateTypeAsync(AzureDevOpsBoardSettings site, CancellationToken ct)
    {
        if (_createType is { } known)
        {
            return ProviderResult<string>.Ok(known);
        }

        var types = await SendAsync(
            site, HttpMethod.Get, $"{site.Organization}/{Uri.EscapeDataString(site.Project)}/_apis/wit/workitemtypes?{Api}", null,
            "the project's work item types",
            body => body.TryGetProperty("value", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(t => Text(t, "name")).OfType<string>().ToList()
                : throw new JsonException("the answer carries no value array"),
            ct).ConfigureAwait(false);
        if (!types.IsOk)
        {
            return types.As<string>();
        }

        _createType = PreferredTypes.FirstOrDefault(p => types.Value!.Contains(p, StringComparer.OrdinalIgnoreCase));
        return _createType is { } chosen
            ? ProviderResult<string>.Ok(chosen)
            : ProviderResult<string>.Refused(
                $"The project has none of the work item types an item could be created as ({string.Join(", ", PreferredTypes)}).");
    }

    /// <summary>A work item type's states and their coarse categories, asked once per type per scope.</summary>
    private async Task<ProviderResult<IReadOnlyList<(string Name, string Category)>>> StatesAsync(
        AzureDevOpsBoardSettings site, string type, CancellationToken ct)
    {
        if (_states.TryGetValue(type, out var known))
        {
            return ProviderResult<IReadOnlyList<(string Name, string Category)>>.Ok(known);
        }

        var states = await SendAsync<IReadOnlyList<(string Name, string Category)>>(
            site, HttpMethod.Get,
            $"{site.Organization}/{Uri.EscapeDataString(site.Project)}/_apis/wit/workitemtypes/{Uri.EscapeDataString(type)}/states?{Api}",
            null, $"the states of '{type}'",
            body => body.TryGetProperty("value", out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray()
                    .Select(s => (Name: Text(s, "name"), Category: Text(s, "category") ?? string.Empty))
                    .Where(s => s.Name is not null)
                    .Select(s => (s.Name!, s.Category))]
                : throw new JsonException("the answer carries no value array"),
            ct).ConfigureAwait(false);
        if (states.IsOk)
        {
            _states[type] = states.Value!;
        }

        return states;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Fields, both ways
    // ---------------------------------------------------------------------------------------------------------

    private static string Stamp(string operationId, string prefix = StampPrefix)
    {
        var safe = new StringBuilder(operationId.Length);
        foreach (var c in operationId)
        {
            // A tag is compared as text in WIQL, so anything that would need quoting there is replaced.
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        }

        return prefix + safe;
    }

    private static Dictionary<string, string?> Values(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(payload) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static JsonObject Op(string op, string path, JsonNode? value) =>
        new() { ["op"] = op, ["path"] = path, ["value"] = value };

    /// <summary>The board's provider-owned fields as JSON Patch operations. A field the payload does not mention is not sent.</summary>
    private static List<JsonObject> ToOperations(string payload, bool forCreate, AzureDevOpsBoardSettings site)
    {
        var values = Values(payload);
        var operations = new List<JsonObject>();

        if (values.TryGetValue(nameof(WorkItem.Title), out var title) && title is { Length: > 0 })
        {
            operations.Add(Op("add", "/fields/System.Title", title));
        }

        if (values.TryGetValue(nameof(WorkItem.Description), out var description) && description is not null)
        {
            operations.Add(Op("add", "/fields/System.Description", ToHtml(description)));
        }

        if (values.TryGetValue(nameof(WorkItem.Priority), out var priority) && priority is { Length: > 0 })
        {
            operations.Add(Op("add", "/fields/Microsoft.VSTS.Common.Priority", SitePriority(priority)));
        }

        // A parent is a RELATION on this site, not a field. It is set when an item is created under one; changing it
        // afterwards means removing a relation by its index, which this adapter does not do, so it is not mentioned.
        if (forCreate && values.TryGetValue(nameof(WorkItem.Parent), out var parent) && parent is { Length: > 0 })
        {
            operations.Add(Op("add", "/relations/-", new JsonObject
            {
                ["rel"] = "System.LinkTypes.Hierarchy-Reverse",
                ["url"] = $"{site.Organization}/_apis/wit/workItems/{Uri.EscapeDataString(parent)}",
            }));
        }

        return operations;
    }

    private async Task<ProviderItem> ToItemAsync(AzureDevOpsBoardSettings site, JsonElement item, CancellationToken ct)
    {
        var id = item.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.Number
            ? idValue.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : throw new JsonException("the work item carries no id");
        var rev = item.TryGetProperty("rev", out var revValue) && revValue.ValueKind == JsonValueKind.Number
            ? revValue.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;

        var carried = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(WorkItem.Title)] = FieldText(item, "System.Title"),
            [nameof(WorkItem.Description)] = FieldText(item, "System.Description") is { } html ? ToText(html) : null,
            [nameof(WorkItem.Priority)] = BoardPriority(Field(item, "Microsoft.VSTS.Common.Priority")),
            [nameof(WorkItem.Assignee)] = Assignee(Field(item, "System.AssignedTo")),
            [nameof(WorkItem.Parent)] = Field(item, "System.Parent") is { ValueKind: JsonValueKind.Number } parent
                ? parent.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null,
        };

        var state = FieldText(item, "System.State");
        string? category = null;
        if (state is not null && FieldText(item, "System.WorkItemType") is { } type)
        {
            var states = await StatesAsync(site, type, ct).ConfigureAwait(false);
            if (states.IsOk)
            {
                category = Category(states.Value!.FirstOrDefault(s => string.Equals(s.Name, state, StringComparison.OrdinalIgnoreCase)).Category);
            }
        }

        return new ProviderItem(id, rev, carried, state, category);
    }

    /// <summary>
    /// A state's category in the coarse vocabulary the status map degrades to (the one Jira's status categories use:
    /// new, indeterminate, done), so an unmapped state is still placed rather than ignored.
    /// </summary>
    private static string? Category(string? category) => category switch
    {
        "Proposed" => "new",
        "InProgress" or "Resolved" => "indeterminate",
        "Completed" or "Removed" => "done",
        _ => null,
    };

    private static JsonElement? Field(JsonElement item, string name) =>
        item.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object
        && fields.TryGetProperty(name, out var value)
            ? value
            : null;

    private static string? FieldText(JsonElement item, string name) =>
        Field(item, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static string? Assignee(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.Object } person => Text(person, "displayName"),
        { ValueKind: JsonValueKind.String } text => text.GetString(),
        _ => null,
    };

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The site's four priorities onto the board's four. Anything else is left unsaid, never guessed.</summary>
    private static string? BoardPriority(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Number } number
            ? number.GetInt32() switch
            {
                1 => StableNames.ToName(WorkItemPriority.Critical),
                2 => StableNames.ToName(WorkItemPriority.High),
                3 => StableNames.ToName(WorkItemPriority.Medium),
                4 => StableNames.ToName(WorkItemPriority.Low),
                _ => null,
            }
            : null;

    private static int SitePriority(string board) =>
        board == StableNames.ToName(WorkItemPriority.Critical) ? 1
        : board == StableNames.ToName(WorkItemPriority.High) ? 2
        : board == StableNames.ToName(WorkItemPriority.Low) ? 4
        : 3;

    /// <summary>
    /// Plain text as the site's HTML: one <c>div</c> per block, a <c>br</c> per line inside it, everything escaped.
    /// Written so that <see cref="ToText"/> gives the same text back.
    /// </summary>
    public static string ToHtml(string text)
    {
        var blocks = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n");
        return string.Concat(blocks.Select(block =>
            "<div>" + string.Join("<br>", block.Split('\n').Select(WebUtility.HtmlEncode)) + "</div>"));
    }

    /// <summary>The site's HTML as plain text: a blank line between blocks, a line break per <c>br</c>, tags removed.</summary>
    public static string ToText(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"</(div|p|h[1-6]|li|tr)>", "\u0001", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);
        var blocks = text.Split('\u0001');
        if (blocks.Length > 1 && blocks[^1].Length == 0)
        {
            blocks = blocks[..^1];
        }

        // Azure DevOps stores each block with a space before its closing tag (measured against a real organisation,
        // 2026-09-24: "...Phase E1 " for "...Phase E1"), so a description read back differed from the one sent in every
        // paragraph, and the reconcile recorded every item sent as edited on the site. The end of a block is trimmed.
        return string.Join("\n\n", blocks.Select(block => WebUtility.HtmlDecode(block).TrimEnd(' ', ' ')));
    }

    // ---------------------------------------------------------------------------------------------------------
    // The wire, and how a call that did not succeed is classified
    // ---------------------------------------------------------------------------------------------------------

    private async Task<AzureDevOpsBoardSettings?> SettingsAsync(CancellationToken ct)
    {
        await _connection.RefreshIfNeededAsync(ct).ConfigureAwait(false);
        return _connection.Current();
    }

    private static ProviderResult<T> NotConfigured<T>() =>
        ProviderResult<T>.Unreachable("No Azure DevOps board connection with a project is configured, so nothing was sent.");

    private async Task<ProviderResult<TOut>> SendAsync<TOut>(
        AzureDevOpsBoardSettings site,
        HttpMethod method,
        string url,
        object? payload,
        string subject,
        Func<JsonElement, TOut> map,
        CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = site.Authorization;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (payload is List<JsonObject> patch)
            {
                request.Content = new StringContent(
                    new JsonArray([.. patch]).ToJsonString(), Encoding.UTF8, "application/json-patch+json");
            }
            else if (payload is JsonNode node)
            {
                request.Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
            }

            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ProviderCallFailure.Catches(ex, ct))
        {
            // Never left (requeued) or left with no answer (looked up by its stamp): the one shared rule decides.
            return ProviderCallFailure.Result<TOut>("Azure DevOps", ex, ct);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Azure DevOps answers a credential it does not accept with 203 and its sign-in page, which a client
            // reading only the status would take for a yes.
            if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
            {
                return ProviderResult<TOut>.Refused(
                    $"Azure DevOps did not accept the board's credential for {subject}; it answered with its sign-in page.");
            }

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                    var mapped = map(document.RootElement);
                    return ProviderResult<TOut>.Ok(mapped is JsonElement element ? (TOut)(object)element.Clone() : mapped);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
                {
                    // It said yes and we cannot read what it did. Not knowing, not a refusal, and for a search NOT
                    // "no such item": that reading is what makes a second work item.
                    return ProviderResult<TOut>.Indeterminate(ProviderFailure.UnreadableBody("Azure DevOps", subject, text));
                }
            }

            var reason = ProviderFailure.Describe("Azure DevOps", subject, response.StatusCode, text);
            return response.StatusCode switch
            {
                HttpStatusCode.NotFound => ProviderResult<TOut>.Gone(reason),

                // A failed test of the revision: somebody changed the item since we read it.
                HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => ProviderResult<TOut>.Conflict(reason),
                HttpStatusCode.TooManyRequests => ProviderResult<TOut>.Unreachable(reason),
                >= HttpStatusCode.InternalServerError => ProviderResult<TOut>.Indeterminate(reason),
                _ => ProviderResult<TOut>.Refused(reason),
            };
        }
    }
}
