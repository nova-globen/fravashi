using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fravashi.Application.Mirroring;
using Fravashi.Domain;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Providers;
using Microsoft.Extensions.Options;

namespace Fravashi.Infrastructure.Tracker.Atlassian;

/// <summary>
/// Jira Cloud (REST v3) behind <see cref="IProviderGateway"/> (backlog 47, first increment): the first adapter
/// the reconciliation machinery of item 82 can drive. Like the Confluence adapters it reads the configuration at
/// each use and puts the credential on each request, so a connection saved in a running studio is the one used.
/// <para>
/// <b>What carries, and what deliberately does not.</b> Title, description, priority, assignee and parent are
/// carried in the board's own field names. LEVEL and KIND are not: what a site's issue types mean is per-board
/// MAPPING, which this item's acceptance says is configuration and not code, so a field this adapter leaves out
/// reads as "not mentioned" rather than being guessed from an issue type's name. A description crosses as plain
/// paragraphs; the site's document format carries formatting the board's text does not, and that is lost on
/// the way in rather than rendered as markup nobody wrote.
/// </para>
/// <para>
/// <b>The stamp.</b> A creation is labelled <c>fravashi-op-{operation id}</c>, and
/// <see cref="FindByOperationIdAsync"/> searches for that label. It is the only thing that can tell a creation
/// that landed with no answer from one that never happened, which is what makes a crashed creation recoverable
/// instead of a duplicate. An answer that cannot be read is NEVER taken for "no such item".
/// </para>
/// <para>
/// <b>The version.</b> Jira has no etag, so an item's <c>updated</c> stamp is its version token, kept verbatim.
/// An update or a transition reads the item first and refuses with a conflict when the stamp has moved. That is
/// a check and then an act, not one atomic call: a change made on the site in between is overwritten. The
/// window is one round trip, and it is stated here rather than hidden.
/// </para>
/// </summary>
public sealed class AtlassianJiraGateway : IProviderGateway
{
    /// <summary>The field the transition payload names its target in: the site's own status name.</summary>
    public const string NativeStatusField = "NativeStatus";

    private const string StampPrefix = "fravashi-op-";
    private const string ReadFields = "summary,description,issuetype,priority,assignee,status,parent,labels,updated";

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<AtlassianConfig> _options;

    public AtlassianJiraGateway(HttpClient http, IOptionsMonitor<AtlassianConfig> options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string Connection => "Jira";

    /// <inheritdoc />
    public ProviderCapabilities Capabilities { get; } =
        new(CanWrite: true, CanSearchByOperationId: true, CanTombstone: true, CanList: true, CanOfferTransitions: true);

    /// <inheritdoc />
    public async Task<ProviderResult<ProviderItem>> CreateAsync(string payload, string operationId, CancellationToken ct = default)
    {
        var config = _options.CurrentValue;
        var fields = ToSiteFields(payload);
        fields["project"] = new JsonObject { ["key"] = config.JiraProject };
        fields["issuetype"] = new JsonObject { ["name"] = "Task" };
        fields["labels"] = new JsonArray(Stamp(operationId));

        var created = await SendAsync(
            config, HttpMethod.Post, $"{config.JiraBaseUrl}/issue", new JsonObject { ["fields"] = fields },
            "creating an issue", body => Text(body, "key"), ct).ConfigureAwait(false);

        if (!created.IsOk)
        {
            return created.As<ProviderItem>();
        }

        if (created.Value is not { Length: > 0 } key)
        {
            // It said yes and did not say what it made. The issue exists; only its key is unknown here.
            return ProviderResult<ProviderItem>.Indeterminate(ProviderFailure.MissingField("Jira", "creating an issue", "key"));
        }

        // The creation's answer names the issue and nothing else, so the item is read back. If that read fails
        // the creation still happened: say what is known rather than reporting the write as lost.
        var read = await GetAsync(key, ct).ConfigureAwait(false);
        return read.IsOk
            ? read
            : ProviderResult<ProviderItem>.Ok(new ProviderItem(key, null, new Dictionary<string, string?>(), null));
    }

    /// <inheritdoc />
    public async Task<ProviderResult<ProviderItem>> UpdateAsync(
        string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default)
    {
        var config = _options.CurrentValue;
        if (await StaleAsync(key, providerVersion, ct).ConfigureAwait(false) is { } stale)
        {
            return stale;
        }

        var sent = await SendAsync(
            config, HttpMethod.Put, $"{config.JiraBaseUrl}/issue/{Uri.EscapeDataString(key)}",
            new JsonObject { ["fields"] = ToSiteFields(payload) }, $"changing {key}", _ => true, ct).ConfigureAwait(false);

        return sent.IsOk
            ? await GetAsync(key, ct).ConfigureAwait(false)
            : sent.As<ProviderItem>();
    }

    /// <inheritdoc />
    public async Task<ProviderResult<ProviderItem>> TransitionAsync(
        string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default)
    {
        var config = _options.CurrentValue;
        if (Values(payload).GetValueOrDefault(NativeStatusField) is not { Length: > 0 } wanted)
        {
            return ProviderResult<ProviderItem>.Refused($"The transition for {key} names no status to move to.");
        }

        if (await StaleAsync(key, providerVersion, ct).ConfigureAwait(false) is { } stale)
        {
            return stale;
        }

        var url = $"{config.JiraBaseUrl}/issue/{Uri.EscapeDataString(key)}/transitions";
        var offered = await OfferedAsync(config, key, ct).ConfigureAwait(false);

        if (!offered.IsOk)
        {
            return offered.As<ProviderItem>();
        }

        var match = offered.Value!.FirstOrDefault(t => string.Equals(t.To, wanted, StringComparison.OrdinalIgnoreCase));
        if (match.Id is null)
        {
            // The site's workflow decides what can follow what. Saying which moves it DOES offer is what lets a
            // person see whether the status is misspelt or simply not reachable from here.
            var reachable = offered.Value!.Count == 0 ? "none" : string.Join(", ", offered.Value!.Select(t => t.To).Distinct());
            return ProviderResult<ProviderItem>.Refused(
                $"Jira's workflow does not lead from where {key} is to '{wanted}'. From here it offers: {reachable}.");
        }

        var moved = await SendAsync(
            config, HttpMethod.Post, url, new JsonObject { ["transition"] = new JsonObject { ["id"] = match.Id } },
            $"moving {key} to '{wanted}'", _ => true, ct).ConfigureAwait(false);

        return moved.IsOk
            ? await GetAsync(key, ct).ConfigureAwait(false)
            : moved.As<ProviderItem>();
    }

    /// <inheritdoc />
    public async Task<ProviderResult<IReadOnlyList<string>>> OfferedTransitionsAsync(string key, CancellationToken ct = default)
    {
        var offered = await OfferedAsync(_options.CurrentValue, key, ct).ConfigureAwait(false);
        return offered.IsOk
            ? ProviderResult<IReadOnlyList<string>>.Ok(
                [.. offered.Value!.Select(t => t.To).Distinct(StringComparer.OrdinalIgnoreCase)])
            : offered.As<IReadOnlyList<string>>();
    }

    /// <summary>
    /// What the site's workflow offers from where an issue stands: the transition ids and the states they
    /// lead to. One lookup, used both to CHOOSE a transition and to ASK a person which state they meant.
    /// </summary>
    private Task<ProviderResult<List<(string Id, string To)>>> OfferedAsync(
        AtlassianConfig config, string key, CancellationToken ct) =>
        SendAsync(
            config,
            HttpMethod.Get,
            $"{config.JiraBaseUrl}/issue/{Uri.EscapeDataString(key)}/transitions",
            null,
            $"the transitions of {key}",
            body => body.TryGetProperty("transitions", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray()
                    .Select(t => (Id: Text(t, "id"), To: t.TryGetProperty("to", out var to) ? Text(to, "name") : null))
                    .Where(t => t.Id is not null && t.To is not null)
                    .Select(t => (Id: t.Id!, To: t.To!))
                    .ToList()
                : [],
            ct);

    /// <inheritdoc />
    public async Task<ProviderResult<ProviderItem?>> FindByOperationIdAsync(string operationId, CancellationToken ct = default)
    {
        var config = _options.CurrentValue;
        var query = new JsonObject
        {
            // The former stamp too (the bridge release): a creation the studio left in flight before the internal rename
            // carries it, and must be found rather than made twice.
            ["jql"] = $"project = \"{config.JiraProject}\" AND labels in (\"{Stamp(operationId)}\", \"{Stamp(operationId, FormerNames.TrackerStampPrefix)}\")",
            ["fields"] = new JsonArray([.. ReadFields.Split(',').Select(f => (JsonNode)f)]),
            ["maxResults"] = 2,
        };

        return await SendAsync<ProviderItem?>(
            config, HttpMethod.Post, $"{config.JiraBaseUrl}/search/jql", query, "searching for a creation's stamp",
            body => body.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array
                ? issues.EnumerateArray().Select(ToItem).FirstOrDefault()
                : throw new JsonException("the search answer carries no issues array"),
            ct).ConfigureAwait(false);
    }

    /// <summary>Jira's own cap on one search page (backlog 182: the site's limit, not this product's).</summary>
    private const int ListPageSize = 100;

    /// <inheritdoc />
    /// <remarks>
    /// Newest first, so a project larger than one page brings the items somebody is most likely to be working
    /// on, first. <paramref name="cursor"/> rides straight through as the site's own <c>nextPageToken</c>
    /// (backlog 182); the caller keeps asking for the next page until the answer carries none, which is how
    /// every item arrives rather than only the newest page of them.
    /// </remarks>
    public Task<ProviderResult<ProviderPage>> ListAsync(string? cursor, CancellationToken ct = default)
    {
        var config = _options.CurrentValue;
        var query = new JsonObject
        {
            ["jql"] = $"project = \"{config.JiraProject}\" ORDER BY updated DESC",
            ["fields"] = new JsonArray([.. ReadFields.Split(',').Select(f => (JsonNode)f)]),
            ["maxResults"] = ListPageSize,
        };
        if (cursor is { Length: > 0 })
        {
            query["nextPageToken"] = cursor;
        }

        return SendAsync<ProviderPage>(
            config, HttpMethod.Post, $"{config.JiraBaseUrl}/search/jql", query, "asking what the project holds",
            body =>
            {
                var issues = body.TryGetProperty("issues", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(ToItem).ToList()
                    : throw new JsonException("the search answer carries no issues array");

                // A fixture with no isLast at all (every answer recorded before backlog 182) is the last page:
                // that is what keeps a one-page project reading as whole rather than as a promise of more.
                var isLast = !body.TryGetProperty("isLast", out var lastValue) || lastValue.ValueKind != JsonValueKind.False;
                var next = !isLast && body.TryGetProperty("nextPageToken", out var tokenValue)
                    && tokenValue.ValueKind == JsonValueKind.String
                        ? tokenValue.GetString()
                        : null;

                return new ProviderPage(issues, next);
            },
            ct);
    }

    /// <inheritdoc />
    public Task<ProviderResult<ProviderItem>> GetAsync(string key, CancellationToken ct = default)
    {
        var config = _options.CurrentValue;
        return SendAsync(
            config, HttpMethod.Get, $"{config.JiraBaseUrl}/issue/{Uri.EscapeDataString(key)}?fields={ReadFields}", null,
            $"the issue {key}", ToItem, ct);
    }

    /// <summary>A conflict when the item has moved on the site since the version we hold; null when it has not.</summary>
    private async Task<ProviderResult<ProviderItem>?> StaleAsync(string key, string? providerVersion, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(providerVersion))
        {
            return null;
        }

        var current = await GetAsync(key, ct).ConfigureAwait(false);
        if (!current.IsOk)
        {
            return current;
        }

        return string.Equals(current.Value!.ProviderVersion, providerVersion, StringComparison.Ordinal)
            ? null
            : ProviderResult<ProviderItem>.Conflict(
                $"{key} was changed on Jira since it was last read here (it is now at {current.Value.ProviderVersion}), so nothing was sent.");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Fields, both ways
    // ---------------------------------------------------------------------------------------------------------

    private static string Stamp(string operationId, string prefix = StampPrefix)
    {
        var safe = new StringBuilder(operationId.Length);
        foreach (var c in operationId)
        {
            // A Jira label holds no whitespace, and a quote or a slash would have to be escaped in the search.
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

    /// <summary>The board's provider-owned fields as the site's. A field the payload does not mention is not sent.</summary>
    private static JsonObject ToSiteFields(string payload)
    {
        var values = Values(payload);
        var fields = new JsonObject();

        if (values.TryGetValue(nameof(WorkItem.Title), out var title) && title is { Length: > 0 })
        {
            fields["summary"] = title;
        }

        if (values.TryGetValue(nameof(WorkItem.Description), out var description) && description is not null)
        {
            fields["description"] = ToDocument(description);
        }

        if (values.TryGetValue(nameof(WorkItem.Priority), out var priority) && priority is { Length: > 0 })
        {
            fields["priority"] = new JsonObject { ["name"] = SitePriority(priority) };
        }

        if (values.TryGetValue(nameof(WorkItem.Parent), out var parent))
        {
            fields["parent"] = parent is { Length: > 0 } ? new JsonObject { ["key"] = parent } : null;
        }

        return fields;
    }

    private static ProviderItem ToItem(JsonElement issue)
    {
        var key = Text(issue, "key") ?? throw new JsonException("the issue carries no key");
        var fields = issue.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object
            ? f
            : throw new JsonException("the issue carries no fields");

        var carried = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(WorkItem.Title)] = Text(fields, "summary"),
            [nameof(WorkItem.Description)] = fields.TryGetProperty("description", out var d) ? ToText(d) : null,
            [nameof(WorkItem.Priority)] = BoardPriority(Named(fields, "priority")),
            [nameof(WorkItem.Assignee)] = fields.TryGetProperty("assignee", out var a) && a.ValueKind == JsonValueKind.Object
                ? Text(a, "displayName")
                : null,
            [nameof(WorkItem.Parent)] = fields.TryGetProperty("parent", out var p) && p.ValueKind == JsonValueKind.Object
                ? Text(p, "key")
                : null,
        };

        // Jira's status carries its own coarse category (statusCategory), which is what lets a status
        // nobody mapped still be placed rather than ignored. The KEY is used, not the name: the key is
        // "new" / "indeterminate" / "done" on every site, while the name is translated per language.
        var category = fields.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("statusCategory", out var coarse) && coarse.ValueKind == JsonValueKind.Object
                ? Text(coarse, "key") ?? Text(coarse, "name")
                : null;

        return new ProviderItem(key, Text(fields, "updated"), carried, Named(fields, "status"), category);
    }

    private static string? Named(JsonElement fields, string property) =>
        fields.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object ? Text(value, "name") : null;

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The site's five priorities onto the board's four. An unknown name is left unsaid, never guessed.</summary>
    private static string? BoardPriority(string? site) => site?.Trim().ToUpperInvariant() switch
    {
        "HIGHEST" or "BLOCKER" or "CRITICAL" => StableNames.ToName(WorkItemPriority.Critical),
        "HIGH" or "MAJOR" => StableNames.ToName(WorkItemPriority.High),
        "MEDIUM" or "NORMAL" => StableNames.ToName(WorkItemPriority.Medium),
        "LOW" or "LOWEST" or "MINOR" or "TRIVIAL" => StableNames.ToName(WorkItemPriority.Low),
        _ => null,
    };

    private static string SitePriority(string board) =>
        board == StableNames.ToName(WorkItemPriority.Critical) ? "Highest"
        : board == StableNames.ToName(WorkItemPriority.High) ? "High"
        : board == StableNames.ToName(WorkItemPriority.Low) ? "Low"
        : "Medium";

    /// <summary>Plain text as the site's document format: one paragraph per block, a hard break per line inside it.</summary>
    private static JsonObject ToDocument(string text)
    {
        var paragraphs = new JsonArray();
        foreach (var block in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var runs = new JsonArray();
            var lines = block.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    runs.Add(new JsonObject { ["type"] = "hardBreak" });
                }

                if (lines[i].Length > 0)
                {
                    runs.Add(new JsonObject { ["type"] = "text", ["text"] = lines[i] });
                }
            }

            paragraphs.Add(new JsonObject { ["type"] = "paragraph", ["content"] = runs });
        }

        return new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = paragraphs };
    }

    /// <summary>The site's document as plain text: a blank line between blocks, the text runs of each joined.</summary>
    private static string? ToText(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object
            || !document.TryGetProperty("content", out var blocks)
            || blocks.ValueKind != JsonValueKind.Array)
        {
            return document.ValueKind == JsonValueKind.String ? document.GetString() : null;
        }

        var text = new List<string>();
        foreach (var block in blocks.EnumerateArray())
        {
            var builder = new StringBuilder();
            Append(block, builder);
            if (builder.Length > 0)
            {
                text.Add(builder.ToString());
            }
        }

        return string.Join("\n\n", text);
    }

    private static void Append(JsonElement node, StringBuilder into)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (Text(node, "type") == "hardBreak")
        {
            into.Append('\n');
        }

        if (Text(node, "text") is { } run)
        {
            into.Append(run);
        }

        if (node.TryGetProperty("content", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                Append(child, into);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // The wire, and how a call that did not succeed is classified
    // ---------------------------------------------------------------------------------------------------------

    private async Task<ProviderResult<TOut>> SendAsync<TOut>(
        AtlassianConfig config,
        HttpMethod method,
        string url,
        JsonNode? payload,
        string subject,
        Func<JsonElement, TOut> map,
        CancellationToken ct)
    {
        if (!config.IsJiraValid)
        {
            return ProviderResult<TOut>.Unreachable("No Jira connection with a project is configured, so nothing was sent.");
        }

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = config.AuthenticationHeader;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (payload is not null)
            {
                request.Content = JsonContent.Create(payload);
            }

            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ProviderCallFailure.Catches(ex, ct))
        {
            // Never left (requeued) or left with no answer (looked up by its stamp): the one shared rule decides.
            return ProviderCallFailure.Result<TOut>("Jira", ex, ct);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    // Several of Jira's writes answer 204 with no body at all, which is a yes.
                    using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                    return ProviderResult<TOut>.Ok(map(document.RootElement));
                }
                catch (JsonException)
                {
                    // It said yes and we cannot read what it did. That is not knowing, not a refusal, and for a
                    // search it is NOT "no such item": that reading is what makes a second issue.
                    return ProviderResult<TOut>.Indeterminate(ProviderFailure.UnreadableBody("Jira", subject, text));
                }
            }

            var reason = ProviderFailure.Describe("Jira", subject, response.StatusCode, text);
            return response.StatusCode switch
            {
                HttpStatusCode.NotFound => ProviderResult<TOut>.Gone(reason),
                HttpStatusCode.Conflict => ProviderResult<TOut>.Conflict(reason),

                // It answered, and it did nothing: asked to slow down. Safe to send again.
                HttpStatusCode.TooManyRequests => ProviderResult<TOut>.Unreachable(reason),

                // A server fault is an answer that does not say whether the write was applied.
                >= HttpStatusCode.InternalServerError => ProviderResult<TOut>.Indeterminate(reason),

                _ => ProviderResult<TOut>.Refused(reason),
            };
        }
    }
}
