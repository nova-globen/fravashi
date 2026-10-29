using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Fravashi.Application.Onboarding;

namespace Fravashi.Infrastructure.Onboarding;

/// <summary>
/// <see cref="IBoardStatesProbe"/> for Jira and Azure DevOps. Reads the board's real status names - read-only,
/// no mutation - so the O15 mapping step can present the user's own states. Shares the credential-per-request
/// shape of <see cref="BoardAccessProbe"/> (onboarding has not reloaded configuration when this runs).
/// </summary>
public sealed class BoardStatesProbe : IBoardStatesProbe
{
    private readonly HttpClient _http;

    public BoardStatesProbe(HttpClient http) => _http = http;

    public async Task<BoardStatesResult> FetchAsync(BoardAccessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            if (string.Equals(request.Provider, OnboardingProviders.Jira, StringComparison.OrdinalIgnoreCase))
            {
                return await FetchJiraAsync(request, cancellationToken);
            }

            if (string.Equals(request.Provider, OnboardingProviders.AzureDevOps, StringComparison.OrdinalIgnoreCase))
            {
                return await FetchAzureDevOpsAsync(request, cancellationToken);
            }

            return BoardStatesResult.Failed($"Reading statuses is not supported for '{request.Provider}'.");
        }
        catch (Exception ex)
        {
            return BoardStatesResult.Failed(ex.Message);
        }
    }

    private async Task<BoardStatesResult> FetchJiraAsync(BoardAccessRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = (request.BaseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Token))
        {
            return BoardStatesResult.Failed("Jira needs a site URL, an account email, and an API token to read its statuses.");
        }

        var auth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{request.Email}:{request.Token}")));
        var projectKey = (request.Project ?? string.Empty).Trim();

        // With a project key, read that project's statuses (grouped by issue type); otherwise fall back to the
        // instance-wide status list. Both are read-only.
        if (projectKey.Length > 0)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/rest/api/3/project/{Uri.EscapeDataString(projectKey)}/statuses");
            message.Headers.Authorization = auth;
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return BoardStatesResult.Failed($"Could not read project '{projectKey}' statuses - the request returned {(int)response.StatusCode}.");
            }

            var byType = await response.Content.ReadFromJsonAsync<List<JiraTypeStatuses>>(cancellationToken);
            var names = (byType ?? [])
                .SelectMany(t => t.Statuses ?? [])
                .Select(s => s.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!.Trim());
            return Distinct(names);
        }

        using var globalMessage = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/rest/api/3/status");
        globalMessage.Headers.Authorization = auth;
        globalMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var globalResponse = await _http.SendAsync(globalMessage, cancellationToken);
        if (!globalResponse.IsSuccessStatusCode)
        {
            return BoardStatesResult.Failed($"Could not read the instance statuses - the request returned {(int)globalResponse.StatusCode}.");
        }

        var all = await globalResponse.Content.ReadFromJsonAsync<List<JiraStatus>>(cancellationToken);
        return Distinct((all ?? []).Select(s => s.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()));
    }

    private async Task<BoardStatesResult> FetchAzureDevOpsAsync(BoardAccessRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = (request.BaseUrl ?? string.Empty).TrimEnd('/');
        var projectName = (request.Project ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(projectName) || string.IsNullOrWhiteSpace(request.Token))
        {
            return BoardStatesResult.Failed("Azure DevOps needs an organization URL, a project, and a personal access token to read its states.");
        }

        var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{request.Token}")));
        var project = Uri.EscapeDataString(projectName);

        using var typesMessage = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{project}/_apis/wit/workitemtypes?api-version=7.1");
        typesMessage.Headers.Authorization = auth;
        typesMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var typesResponse = await _http.SendAsync(typesMessage, cancellationToken);
        if (!typesResponse.IsSuccessStatusCode)
        {
            return BoardStatesResult.Failed($"Could not list project '{projectName}' work-item types - the request returned {(int)typesResponse.StatusCode}.");
        }

        var types = await typesResponse.Content.ReadFromJsonAsync<AdoValue<AdoType>>(cancellationToken);
        var typeNames = (types?.Value ?? [])
            .Where(t => !string.IsNullOrEmpty(t.Name) && !t.IsDisabled)
            .Select(t => t.Name!)
            .ToList();

        var names = new List<string>();
        foreach (var typeName in typeNames)
        {
            using var statesMessage = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/{project}/_apis/wit/workitemtypes/{Uri.EscapeDataString(typeName)}/states?api-version=7.1");
            statesMessage.Headers.Authorization = auth;
            statesMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var statesResponse = await _http.SendAsync(statesMessage, cancellationToken);
            if (!statesResponse.IsSuccessStatusCode)
            {
                continue;
            }

            var states = await statesResponse.Content.ReadFromJsonAsync<AdoValue<AdoStateName>>(cancellationToken);
            names.AddRange((states?.Value ?? []).Select(s => s.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()));
        }

        return names.Count == 0
            ? BoardStatesResult.Failed($"Project '{projectName}' resolved but exposed no work-item states to read.")
            : Distinct(names);
    }

    private static BoardStatesResult Distinct(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var name in names)
        {
            if (seen.Add(name))
            {
                ordered.Add(name);
            }
        }

        return ordered.Count == 0
            ? BoardStatesResult.Failed("No statuses were returned by the board.")
            : BoardStatesResult.Ok(ordered);
    }

    // ── Response shapes (only the fields read) ──────────────────────────────────────────────────────────────
    private sealed record JiraTypeStatuses([property: JsonPropertyName("statuses")] List<JiraStatus>? Statuses);

    private sealed record JiraStatus([property: JsonPropertyName("name")] string? Name);

    private sealed record AdoValue<T>([property: JsonPropertyName("value")] List<T>? Value);

    private sealed record AdoType(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("isDisabled")] bool IsDisabled);

    private sealed record AdoStateName([property: JsonPropertyName("name")] string? Name);
}
