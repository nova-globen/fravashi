using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fravashi.Application.Onboarding;
using Fravashi.Infrastructure.Providers;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Onboarding;

/// <summary>
/// <see cref="IBoardAccessProbe"/> for Azure DevOps and Jira.
/// <para>
/// <b>Jira</b> runs a real, user-consented round-trip that proves every operation the product needs: a
/// <b>Read</b> (the authenticated user), a <b>Query</b> (enhanced-JQL search), and - when a project key is
/// supplied - a <b>Write</b> (create a clearly-labelled probe issue), a <b>Modify</b> (edit its summary and
/// description) and a <b>Status change</b> (apply the first available workflow transition). The round-trip runs
/// ONLY on explicit user action in the wizard and is a deliberate, clearly-labelled write - the test-mode write
/// guard exists to suppress <i>agent</i> writes, a different concern, so it is not a prerequisite for a
/// consented onboarding probe. Cleanup (DELETE the probe issue) runs in a <c>finally</c>; if it cannot be
/// deleted, every result names the issue left behind (key + URL) so the user can remove it.
/// </para>
/// <para>
/// <b>Azure DevOps</b> runs the same shape: real Read + Query, and - since the project is always supplied on
/// the ADO step - a consented <b>create → modify → status-change → delete</b> round-trip against a clearly
/// labelled probe work item (resolve a usable work-item type and its states, <c>POST _apis/wit/workitems</c>
/// with a JSON-Patch body, <c>PATCH</c> the title/description, <c>PATCH System.State</c> to a reachable state,
/// then <c>DELETE</c> in a <c>finally</c> - the delete moves the item to the project's recycle bin, with a
/// best-effort permanent destroy). The <c>Local</c> provider is the in-app board fallback (O11): recorded, not
/// yet a working store.
/// </para>
/// </summary>
public sealed class BoardAccessProbe : IBoardAccessProbe
{
    private const string CreateMechanism = "Jira POST /rest/api/3/issue";
    private const string ModifyMechanism = "Jira PUT /rest/api/3/issue/{key}";
    private const string StatusMechanism = "Jira GET + POST /rest/api/3/issue/{key}/transitions";

    private const string AdoCreateMechanism = "Azure DevOps POST _apis/wit/workitems/${type}";
    private const string AdoModifyMechanism = "Azure DevOps PATCH _apis/wit/workitems/{id}";
    private const string AdoStatusMechanism = "Azure DevOps PATCH _apis/wit/workitems/{id} (System.State)";

    private readonly HttpClient _http;

    public BoardAccessProbe(HttpClient http) => _http = http;

    public async Task<AccessProbeResult> ProbeAsync(BoardAccessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.Equals(request.Provider, OnboardingProviders.Local, StringComparison.OrdinalIgnoreCase))
        {
            return AccessProbeResult.Single(
                "Read", AccessProbeOutcome.NotSupported, "in-app local board",
                "The local in-app board needs no external access. It is recorded, not yet a working store (see backlog).");
        }

        return string.Equals(request.Provider, OnboardingProviders.Jira, StringComparison.OrdinalIgnoreCase)
            ? await ProbeJiraAsync(request, cancellationToken)
            : await ProbeAzureDevOpsAsync(request, cancellationToken);
    }

    private static List<AccessCheck> WriteFamilyNotVerifiable(string mechanism, string writeDetail, string modifyDetail, string statusDetail) =>
    [
        new("Write", AccessProbeOutcome.NotVerifiable, mechanism, writeDetail),
        new("Modify", AccessProbeOutcome.NotVerifiable, mechanism, modifyDetail),
        new("Status change", AccessProbeOutcome.NotVerifiable, mechanism, statusDetail),
    ];

    // ── Azure DevOps: real Read + Query, and a consented create → modify → state-change → delete round-trip ─

    private async Task<AccessProbeResult> ProbeAzureDevOpsAsync(BoardAccessRequest request, CancellationToken cancellationToken)
    {
        var auth = request.Bearer
            ? new AuthenticationHeaderValue("Bearer", request.Token)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{request.Token}")));
        var baseUrl = (request.BaseUrl ?? string.Empty).TrimEnd('/');
        var projectName = (request.Project ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(projectName))
        {
            return AccessProbeResult.Single("Read", AccessProbeOutcome.Failed, "input validation",
                "Azure DevOps needs an organization URL and a project.");
        }

        // Read: project metadata.
        var read = await GetCheckAsync("Read", auth,
            $"{baseUrl}/_apis/projects/{Uri.EscapeDataString(projectName)}?api-version=7.0",
            "Azure DevOps GET _apis/projects",
            "Azure DevOps", $"project '{projectName}'", cancellationToken);

        // Query: WIQL (a POST that is a read - it only selects ids).
        var query = await WiqlQueryCheckAsync(auth, baseUrl, projectName, cancellationToken);

        // A failed read means the credential/org/project is wrong - do not attempt the write round-trip on top.
        List<AccessCheck> writeFamily;
        if (read.Outcome != AccessProbeOutcome.Passed)
        {
            writeFamily = WriteFamilyNotVerifiable(
                "ADO work-item round-trip",
                "Not attempted - read access failed, so the write round-trip was skipped.",
                "Not attempted - read access failed, so the modify round-trip was skipped.",
                "Not attempted - read access failed, so the status-change round-trip was skipped.");
        }
        else if (string.IsNullOrWhiteSpace(request.Token))
        {
            writeFamily = WriteFamilyNotVerifiable(
                "ADO work-item round-trip (needs a personal access token)",
                "Not verified - provide a personal access token to run the write test.",
                "Not verified - provide a personal access token to run the modify test.",
                "Not verified - provide a personal access token to run the status-change test.");
        }
        else
        {
            writeFamily = await RunAzureDevOpsRoundTripAsync(auth, baseUrl, projectName, cancellationToken);
        }

        return new AccessProbeResult([read, query, .. writeFamily]);
    }

    /// <summary>
    /// A real create → modify → status-change → delete round-trip against a clearly-labelled probe work item.
    /// Returns the three write-family checks (Write, Modify, Status change). Cleanup runs in a <c>finally</c>,
    /// so a create that succeeds is always followed by a delete attempt; the delete moves the item to the
    /// project's recycle bin (a best-effort permanent destroy is then attempted but never fails the check). If
    /// the delete fails, the leftover work item (id + URL) is named on every check so the user can remove it.
    /// </summary>
    private async Task<List<AccessCheck>> RunAzureDevOpsRoundTripAsync(
        AuthenticationHeaderValue auth, string baseUrl, string projectName, CancellationToken cancellationToken)
    {
        // 1. Resolve a usable work-item type (Task/Issue/User Story/Bug preferred) and its states.
        AdoTypeMeta? type;
        AccessCheck? resolveFailure;
        try
        {
            (type, resolveFailure) = await ResolveAdoTypeAsync(auth, baseUrl, projectName, cancellationToken);
        }
        catch (Exception ex)
        {
            return
            [
                new AccessCheck("Write", AccessProbeOutcome.Failed, "Azure DevOps GET _apis/wit/workitemtypes", ex.Message),
                NotAttempted("Modify", AdoModifyMechanism, "the work-item type could not be resolved"),
                NotAttempted("Status change", AdoStatusMechanism, "the work-item type could not be resolved"),
            ];
        }

        if (type is null)
        {
            return
            [
                resolveFailure!,
                NotAttempted("Modify", AdoModifyMechanism, "the work-item type could not be resolved"),
                NotAttempted("Status change", AdoStatusMechanism, "the work-item type could not be resolved"),
            ];
        }

        // 2. CREATE the probe work item (POST _apis/wit/workitems/${type}).
        var title = $"{ProductName.Short} access probe - safe to delete ({DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}Z)";
        CreatedWorkItem? created;
        AccessCheck? createFailure;
        try
        {
            (created, createFailure) = await CreateAdoWorkItemAsync(auth, baseUrl, projectName, type.Name, title, cancellationToken);
        }
        catch (Exception ex)
        {
            return
            [
                new AccessCheck("Write", AccessProbeOutcome.Failed, AdoCreateMechanism, ex.Message),
                NotAttempted("Modify", AdoModifyMechanism, "the probe work item could not be created"),
                NotAttempted("Status change", AdoStatusMechanism, "the probe work item could not be created"),
            ];
        }

        if (created is null)
        {
            return
            [
                createFailure!,
                NotAttempted("Modify", AdoModifyMechanism, "the probe work item could not be created"),
                NotAttempted("Status change", AdoStatusMechanism, "the probe work item could not be created"),
            ];
        }

        // 3. MODIFY, 4. STATUS CHANGE, then 5. DELETE - cleanup runs in the finally so it is ALWAYS attempted
        //    once a work item exists.
        var id = created.Id;
        var itemUrl = $"{baseUrl}/{Uri.EscapeDataString(projectName)}/_workitems/edit/{id}";
        var write = new AccessCheck("Write", AccessProbeOutcome.Passed, AdoCreateMechanism,
            $"Created a probe work item #{id} of type '{type.Name}' in project '{projectName}' (a real create).");
        var modify = new AccessCheck("Modify", AccessProbeOutcome.Failed, AdoModifyMechanism, "The modify request did not run.");
        var status = new AccessCheck("Status change", AccessProbeOutcome.Failed, AdoStatusMechanism, "The status-change request did not run.");
        CleanupOutcome cleanup;
        try
        {
            modify = await ModifyAdoWorkItemAsync(auth, baseUrl, id, title, cancellationToken);
            status = await TransitionAdoWorkItemAsync(auth, baseUrl, id, type, created.State, cancellationToken);
        }
        finally
        {
            cleanup = await CleanupWorkItemAsync(auth, baseUrl, id, itemUrl, cancellationToken);
        }

        if (cleanup.Leftover is not null)
        {
            write = write with { Detail = $"{write.Detail} {cleanup.Leftover}" };
            modify = modify with { Detail = $"{modify.Detail} {cleanup.Leftover}" };
            status = status with { Detail = $"{status.Detail} {cleanup.Leftover}" };
        }
        else
        {
            write = write with { Detail = $"{write.Detail} {cleanup.Note}" };
        }

        return [write, modify, status];
    }

    private static readonly string[] PreferredAdoTypes = ["Task", "Issue", "User Story", "Bug"];

    private async Task<(AdoTypeMeta? Type, AccessCheck? Failure)> ResolveAdoTypeAsync(
        AuthenticationHeaderValue auth, string baseUrl, string projectName, CancellationToken cancellationToken)
    {
        const string mechanism = "Azure DevOps GET _apis/wit/workitemtypes";
        var project = Uri.EscapeDataString(projectName);
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{project}/_apis/wit/workitemtypes?api-version=7.1");
        message.Headers.Authorization = auth;
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return (null, new AccessCheck("Write", AccessProbeOutcome.Failed, mechanism,
                await DescribeAdoFailureAsync(response, "Could not list the project's work-item types", cancellationToken)));
        }

        var payload = await response.Content.ReadFromJsonAsync<WorkItemTypesResponse>(cancellationToken);
        var types = payload?.Value?.Where(t => !string.IsNullOrEmpty(t.Name) && !t.IsDisabled).ToList() ?? [];
        var chosen = PreferredAdoTypes
                .Select(name => types.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(t => t is not null)
            ?? types.FirstOrDefault();
        if (chosen is null)
        {
            return (null, new AccessCheck("Write", AccessProbeOutcome.Failed, mechanism,
                $"Project '{projectName}' was resolved but exposes no usable work-item type to create with."));
        }

        // Read the type's states so the status-change step can target a reachable state honestly.
        var states = await ReadAdoStatesAsync(auth, baseUrl, projectName, chosen.Name!, cancellationToken);
        return (new AdoTypeMeta(chosen.Name!, states), null);
    }

    private async Task<IReadOnlyList<AdoState>> ReadAdoStatesAsync(
        AuthenticationHeaderValue auth, string baseUrl, string projectName, string typeName, CancellationToken cancellationToken)
    {
        try
        {
            var project = Uri.EscapeDataString(projectName);
            using var message = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/{project}/_apis/wit/workitemtypes/{Uri.EscapeDataString(typeName)}/states?api-version=7.1");
            message.Headers.Authorization = auth;
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var payload = await response.Content.ReadFromJsonAsync<StatesResponse>(cancellationToken);
            return payload?.Value?.Where(s => !string.IsNullOrEmpty(s.Name)).ToList() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task<(CreatedWorkItem? Item, AccessCheck? Failure)> CreateAdoWorkItemAsync(
        AuthenticationHeaderValue auth, string baseUrl, string projectName, string typeName, string title, CancellationToken cancellationToken)
    {
        var project = Uri.EscapeDataString(projectName);
        var patch = new object[]
        {
            new { op = "add", path = "/fields/System.Title", value = title },
            new { op = "add", path = "/fields/System.Description", value = $"This work item was created automatically by {ProductName.Short} to verify write access during onboarding. It is safe to delete - {ProductName.Short} removes it automatically as part of the same test." },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post,
            $"{baseUrl}/{project}/_apis/wit/workitems/${Uri.EscapeDataString(typeName)}?api-version=7.1")
        {
            Content = JsonPatchContent(patch),
        };
        message.Headers.Authorization = auth;
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return (null, new AccessCheck("Write", AccessProbeOutcome.Failed, AdoCreateMechanism,
                await DescribeAdoFailureAsync(response, $"Could not create a '{typeName}' work item in project '{projectName}'", cancellationToken)));
        }

        var item = await response.Content.ReadFromJsonAsync<CreatedWorkItem>(cancellationToken);
        if (item is null || item.Id <= 0)
        {
            return (null, new AccessCheck("Write", AccessProbeOutcome.Failed, AdoCreateMechanism,
                "The create request succeeded but returned no work-item id, so the round-trip could not continue."));
        }

        return (item, null);
    }

    private async Task<AccessCheck> ModifyAdoWorkItemAsync(
        AuthenticationHeaderValue auth, string baseUrl, int id, string title, CancellationToken cancellationToken)
    {
        try
        {
            var patch = new object[]
            {
                new { op = "add", path = "/fields/System.Title", value = $"{title} (edited)" },
                new { op = "add", path = "/fields/System.Description", value = $"This work item was created automatically by {ProductName.Short}. It was edited by the {ProductName.Short} access probe to verify modify access, and is safe to delete." },
            };

            using var message = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/_apis/wit/workitems/{id}?api-version=7.1")
            {
                Content = JsonPatchContent(patch),
            };
            message.Headers.Authorization = auth;
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(message, cancellationToken);

            return response.IsSuccessStatusCode
                ? new AccessCheck("Modify", AccessProbeOutcome.Passed, AdoModifyMechanism,
                    $"Edited the probe work item #{id}'s title and description (a real modify).")
                : new AccessCheck("Modify", AccessProbeOutcome.Failed, AdoModifyMechanism,
                    await DescribeAdoFailureAsync(response, $"The probe work item #{id} was created but the edit (modify) request failed", cancellationToken));
        }
        catch (Exception ex)
        {
            return new AccessCheck("Modify", AccessProbeOutcome.Failed, AdoModifyMechanism,
                $"The probe work item #{id} was created but the edit (modify) request failed: {ex.Message}");
        }
    }

    private async Task<AccessCheck> TransitionAdoWorkItemAsync(
        AuthenticationHeaderValue auth, string baseUrl, int id, AdoTypeMeta type, string? currentState, CancellationToken cancellationToken)
    {
        // Pick a reachable target state: any state whose name differs from the created item's state and that is
        // not the "Removed" category. If the type exposes no other state, status change is honestly not verifiable.
        var target = type.States.FirstOrDefault(s =>
            !string.Equals(s.Name, currentState, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(s.Category, "Removed", StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return new AccessCheck("Status change", AccessProbeOutcome.NotVerifiable, AdoStatusMechanism,
                $"No other state is reachable for type '{type.Name}' from '{currentState}' - status change is not verifiable in this project's process.");
        }

        try
        {
            var patch = new object[] { new { op = "add", path = "/fields/System.State", value = target.Name } };
            using var message = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/_apis/wit/workitems/{id}?api-version=7.1")
            {
                Content = JsonPatchContent(patch),
            };
            message.Headers.Authorization = auth;
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(message, cancellationToken);

            return response.IsSuccessStatusCode
                ? new AccessCheck("Status change", AccessProbeOutcome.Passed, AdoStatusMechanism,
                    $"Transitioned #{id} from '{currentState}' to '{target.Name}' (a real status change).")
                : new AccessCheck("Status change", AccessProbeOutcome.Failed, AdoStatusMechanism,
                    await DescribeAdoFailureAsync(response, $"The status change of #{id} to '{target.Name}' failed", cancellationToken));
        }
        catch (Exception ex)
        {
            return new AccessCheck("Status change", AccessProbeOutcome.Failed, AdoStatusMechanism,
                $"The status change of #{id} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes the probe work item (DELETE _apis/wit/workitems/{id} - moves it to the project's recycle bin) and
    /// then best-effort permanently destroys it (DELETE _apis/wit/recyclebin/{id}), which never fails the check.
    /// Returns a success note when the item was deleted; otherwise a leftover message naming the work-item id +
    /// URL so the user can remove it. Deletion needs the "Delete work items" permission, so a 403 is a real
    /// (and honestly reported) possibility.
    /// </summary>
    private async Task<CleanupOutcome> CleanupWorkItemAsync(
        AuthenticationHeaderValue auth, string baseUrl, int id, string itemUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/_apis/wit/workitems/{id}?api-version=7.1");
            message.Headers.Authorization = auth;
            using var response = await _http.SendAsync(message, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var reason = response.StatusCode == HttpStatusCode.Forbidden
                    ? "The probe work item could not be deleted - the token lacks the \"Delete work items\" permission (403)."
                    : $"The probe work item could not be deleted - the delete request returned {(int)response.StatusCode}.";
                return new CleanupOutcome(null, $"{reason} It was left behind - #{id} ({itemUrl}). Please delete it manually.");
            }

            // Best-effort permanent destroy from the recycle bin - never fails the check.
            var destroyed = await TryDestroyFromRecycleBinAsync(auth, baseUrl, id, cancellationToken);
            return new CleanupOutcome(destroyed
                ? "The probe work item was then deleted (moved to the recycle bin and permanently destroyed)."
                : "The probe work item was then deleted (moved to the recycle bin; permanent destroy needs project-admin rights, so it may remain there).", null);
        }
        catch (Exception ex)
        {
            return new CleanupOutcome(null, $"The probe work item could not be deleted: {ex.Message} It was left behind - #{id} ({itemUrl}). Please delete it manually.");
        }
    }

    private async Task<bool> TryDestroyFromRecycleBinAsync(AuthenticationHeaderValue auth, string baseUrl, int id, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/_apis/wit/recyclebin/{id}?api-version=7.1-preview");
            message.Headers.Authorization = auth;
            using var response = await _http.SendAsync(message, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static StringContent JsonPatchContent(object[] patch) =>
        new(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json-patch+json");

    /// <summary>
    /// Builds a failure detail that carries Azure DevOps's own <c>message</c> ("<paramref name="subject"/>
    /// (status): {ADO's message}") rather than a bare status code, so the real cause is visible.
    /// </summary>
    private static async Task<string> DescribeAdoFailureAsync(HttpResponseMessage response, string subject, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        string? adoMessage;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<AdoErrorResponse>(cancellationToken);
            adoMessage = string.IsNullOrWhiteSpace(body?.Message) ? null : body!.Message;
        }
        catch (Exception)
        {
            adoMessage = null;
        }

        return adoMessage is null ? $"{subject} ({status})." : $"{subject} ({status}): {adoMessage}";
    }

    // ── Jira: real Read + Query, and a consented create → modify → transition → delete round-trip ──────────

    private async Task<AccessProbeResult> ProbeJiraAsync(BoardAccessRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = (request.BaseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Token))
        {
            return AccessProbeResult.Single("Read", AccessProbeOutcome.Failed, "input validation",
                "Jira needs a site URL, an account email, and an API token.");
        }

        var auth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{request.Email}:{request.Token}")));

        // Read: the authenticated user.
        var read = await GetCheckAsync("Read", auth, $"{baseUrl}/rest/api/3/myself",
            "Jira GET /rest/api/3/myself",
            "Jira", "the authenticated user", cancellationToken);

        // Query: the enhanced-JQL search (the legacy /rest/api/3/search was retired on Jira Cloud and now
        // returns 410 Gone - see CHANGE-2046). The replacement paginates by nextPageToken (no startAt) and
        // returns no issue fields unless asked, so the probe requests only "id" to prove query access cheaply.
        var query = await JqlQueryCheckAsync(auth, baseUrl, cancellationToken);

        // A failed read means the credential/site is wrong - do not attempt the write round-trip on top of it.
        List<AccessCheck> writeFamily;
        if (read.Outcome != AccessProbeOutcome.Passed)
        {
            writeFamily = WriteFamilyNotVerifiable(
                "Jira issue round-trip",
                "Not attempted - read access failed, so the write round-trip was skipped.",
                "Not attempted - read access failed, so the modify round-trip was skipped.",
                "Not attempted - read access failed, so the transition round-trip was skipped.");
        }
        else
        {
            var projectKey = (request.Project ?? string.Empty).Trim();
            writeFamily = projectKey.Length == 0
                ? WriteFamilyNotVerifiable(
                    "Jira issue round-trip (needs a project key)",
                    "Not verified - provide a project key to run the write test.",
                    "Not verified - provide a project key to run the modify test.",
                    "Not verified - provide a project key to run the status-change test.")
                : await RunJiraRoundTripAsync(auth, baseUrl, projectKey, cancellationToken);
        }

        return new AccessProbeResult([read, query, .. writeFamily]);
    }

    /// <summary>
    /// A real create → modify → transition → delete round-trip against a clearly-labelled probe issue. Returns
    /// the three write-family checks (Write, Modify, Status change). Cleanup runs in a <c>finally</c>, so a
    /// create that succeeds is always followed by a delete attempt; if the delete fails, the leftover issue
    /// (key + URL) is named on every check so the user can remove it manually.
    /// </summary>
    private async Task<List<AccessCheck>> RunJiraRoundTripAsync(
        AuthenticationHeaderValue auth, string baseUrl, string projectKey, CancellationToken cancellationToken)
    {
        // 1. Resolve the project and a usable (non-subtask) issue type (GET /rest/api/3/project/{key}).
        ProjectMeta? project;
        AccessCheck? resolveFailure;
        try
        {
            (project, resolveFailure) = await ResolveProjectAsync(auth, baseUrl, projectKey, cancellationToken);
        }
        catch (Exception ex)
        {
            return
            [
                new AccessCheck("Write", AccessProbeOutcome.Failed, "Jira GET /rest/api/3/project/{key}", ex.Message),
                NotAttempted("Modify", ModifyMechanism, "the project could not be resolved"),
                NotAttempted("Status change", StatusMechanism, "the project could not be resolved"),
            ];
        }

        if (project is null)
        {
            return
            [
                resolveFailure!,
                NotAttempted("Modify", ModifyMechanism, "the project could not be resolved"),
                NotAttempted("Status change", StatusMechanism, "the project could not be resolved"),
            ];
        }

        // 2. CREATE the probe issue (POST /rest/api/3/issue).
        var summary = $"{ProductName.Short} access probe - safe to delete ({DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}Z)";
        CreatedIssue? created;
        AccessCheck? createFailure;
        try
        {
            (created, createFailure) = await CreateProbeIssueAsync(auth, baseUrl, projectKey, project.IssueTypeId!, summary, cancellationToken);
        }
        catch (Exception ex)
        {
            return
            [
                new AccessCheck("Write", AccessProbeOutcome.Failed, CreateMechanism, ex.Message),
                NotAttempted("Modify", ModifyMechanism, "the probe issue could not be created"),
                NotAttempted("Status change", StatusMechanism, "the probe issue could not be created"),
            ];
        }

        if (created is null)
        {
            return
            [
                createFailure!,
                NotAttempted("Modify", ModifyMechanism, "the probe issue could not be created"),
                NotAttempted("Status change", StatusMechanism, "the probe issue could not be created"),
            ];
        }

        // 3. MODIFY, 4. STATUS CHANGE, then 5. DELETE - cleanup runs in the finally so it is ALWAYS attempted
        //    once an issue exists.
        var issueKey = created.Key!;
        var issueUrl = $"{baseUrl}/browse/{issueKey}";
        var write = new AccessCheck("Write", AccessProbeOutcome.Passed, CreateMechanism,
            $"Created a probe issue {issueKey} of type '{project.IssueTypeName}' in project '{projectKey}' (a real create).");
        var modify = new AccessCheck("Modify", AccessProbeOutcome.Failed, ModifyMechanism, "The modify request did not run.");
        var status = new AccessCheck("Status change", AccessProbeOutcome.Failed, StatusMechanism, "The transition request did not run.");
        string? leftover;
        try
        {
            modify = await ModifyProbeIssueAsync(auth, baseUrl, issueKey, summary, cancellationToken);
            status = await TransitionProbeIssueAsync(auth, baseUrl, issueKey, cancellationToken);
        }
        finally
        {
            leftover = await CleanupIssueAsync(auth, baseUrl, issueKey, issueUrl, cancellationToken);
        }

        if (leftover is not null)
        {
            write = write with { Detail = $"{write.Detail} {leftover}" };
            modify = modify with { Detail = $"{modify.Detail} {leftover}" };
            status = status with { Detail = $"{status.Detail} {leftover}" };
        }

        return [write, modify, status];
    }

    private static AccessCheck NotAttempted(string capability, string mechanism, string reason) =>
        new(capability, AccessProbeOutcome.NotVerifiable, mechanism, $"Not attempted - {reason}.");

    private async Task<(ProjectMeta? Project, AccessCheck? Failure)> ResolveProjectAsync(
        AuthenticationHeaderValue auth, string baseUrl, string projectKey, CancellationToken cancellationToken)
    {
        const string mechanism = "Jira GET /rest/api/3/project/{key}";
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/rest/api/3/project/{Uri.EscapeDataString(projectKey)}");
        message.Headers.Authorization = auth;
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(message, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, new AccessCheck("Write", AccessProbeOutcome.Failed, mechanism,
                $"Project key '{projectKey}' was not found (404) - no project with that key is visible to this token."));
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, new AccessCheck("Write", AccessProbeOutcome.Failed, mechanism,
                MapFailure("resolve the project", projectKey, response.StatusCode)));
        }

        var payload = await response.Content.ReadFromJsonAsync<ProjectResponse>(cancellationToken);
        var issueType = payload?.IssueTypes?.FirstOrDefault(t => !t.Subtask && !string.IsNullOrEmpty(t.Id))
            ?? payload?.IssueTypes?.FirstOrDefault(t => !string.IsNullOrEmpty(t.Id));
        if (issueType is null)
        {
            return (null, new AccessCheck("Write", AccessProbeOutcome.Failed, mechanism,
                $"Project '{projectKey}' was resolved but exposes no usable issue type to create with."));
        }

        return (new ProjectMeta(issueType.Id, issueType.Name), null);
    }

    private async Task<(CreatedIssue? Issue, AccessCheck? Failure)> CreateProbeIssueAsync(
        AuthenticationHeaderValue auth, string baseUrl, string projectKey, string issueTypeId, string summary, CancellationToken cancellationToken)
    {
        var body = new
        {
            fields = new
            {
                project = new { key = projectKey },
                summary,
                description = ProbeDescription($"This issue was created automatically by {ProductName.Short} to verify write access during onboarding. It is safe to delete - {ProductName.Short} removes it automatically as part of the same test."),
                issuetype = new { id = issueTypeId },
            },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/rest/api/3/issue")
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Authorization = auth;
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var detail = response.StatusCode == HttpStatusCode.BadRequest
                ? await MapCreateBadRequestAsync(response, cancellationToken)
                : MapFailure("create an issue", projectKey, response.StatusCode);
            return (null, new AccessCheck("Write", AccessProbeOutcome.Failed, CreateMechanism, detail));
        }

        var issue = await response.Content.ReadFromJsonAsync<CreatedIssue>(cancellationToken);
        if (issue is null || string.IsNullOrEmpty(issue.Key))
        {
            return (null, new AccessCheck("Write", AccessProbeOutcome.Failed, CreateMechanism,
                "The create request succeeded but returned no issue key, so the round-trip could not continue."));
        }

        return (issue, null);
    }

    private async Task<AccessCheck> ModifyProbeIssueAsync(
        AuthenticationHeaderValue auth, string baseUrl, string issueKey, string summary, CancellationToken cancellationToken)
    {
        try
        {
            var body = new
            {
                fields = new
                {
                    summary = $"{summary} (edited)",
                    description = ProbeDescription($"This issue was created automatically by {ProductName.Short}. It was edited by the {ProductName.Short} access probe to verify modify access, and is safe to delete."),
                },
            };

            using var message = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}")
            {
                Content = JsonContent.Create(body),
            };
            message.Headers.Authorization = auth;
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(message, cancellationToken);

            return response.IsSuccessStatusCode
                ? new AccessCheck("Modify", AccessProbeOutcome.Passed, ModifyMechanism,
                    $"Edited the probe issue {issueKey}'s summary and description (a real modify).")
                : new AccessCheck("Modify", AccessProbeOutcome.Failed, ModifyMechanism,
                    $"The probe issue {issueKey} was created but the edit (modify) request returned {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return new AccessCheck("Modify", AccessProbeOutcome.Failed, ModifyMechanism,
                $"The probe issue {issueKey} was created but the edit (modify) request failed: {ex.Message}");
        }
    }

    private async Task<AccessCheck> TransitionProbeIssueAsync(
        AuthenticationHeaderValue auth, string baseUrl, string issueKey, CancellationToken cancellationToken)
    {
        try
        {
            // List the transitions available from the issue's current (initial) status.
            using var listMessage = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/transitions");
            listMessage.Headers.Authorization = auth;
            listMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var listResponse = await _http.SendAsync(listMessage, cancellationToken);

            if (!listResponse.IsSuccessStatusCode)
            {
                return new AccessCheck("Status change", AccessProbeOutcome.Failed, StatusMechanism,
                    $"Could not list the transitions for {issueKey} - the request returned {(int)listResponse.StatusCode}.");
            }

            var payload = await listResponse.Content.ReadFromJsonAsync<TransitionsResponse>(cancellationToken);
            var transition = payload?.Transitions?.FirstOrDefault(t => !string.IsNullOrEmpty(t.Id));
            if (transition is null)
            {
                return new AccessCheck("Status change", AccessProbeOutcome.NotVerifiable, StatusMechanism,
                    $"No transition is available from {issueKey}'s initial status - status change is not verifiable in this project's workflow.");
            }

            using var postMessage = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/transitions")
            {
                Content = JsonContent.Create(new { transition = new { id = transition.Id } }),
            };
            postMessage.Headers.Authorization = auth;
            postMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var postResponse = await _http.SendAsync(postMessage, cancellationToken);

            return postResponse.IsSuccessStatusCode
                ? new AccessCheck("Status change", AccessProbeOutcome.Passed, StatusMechanism,
                    $"Transitioned {issueKey} to '{transition.Name ?? transition.Id}' (a real status change).")
                : new AccessCheck("Status change", AccessProbeOutcome.Failed, StatusMechanism,
                    $"The transition of {issueKey} to '{transition.Name ?? transition.Id}' returned {(int)postResponse.StatusCode}.");
        }
        catch (Exception ex)
        {
            return new AccessCheck("Status change", AccessProbeOutcome.Failed, StatusMechanism,
                $"The transition of {issueKey} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes the probe issue (DELETE /rest/api/3/issue/{key}). Always safe to call. Returns null when the
    /// issue was deleted; otherwise a leftover message naming the issue key + URL so the user can remove it.
    /// Deletion needs the "Delete issues" permission, so a 403 is a real (and honestly reported) possibility.
    /// </summary>
    private async Task<string?> CleanupIssueAsync(
        AuthenticationHeaderValue auth, string baseUrl, string issueKey, string issueUrl, CancellationToken cancellationToken)
    {
        try
        {
            // No subtasks are ever created, so deleteSubtasks is irrelevant.
            using var message = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}");
            message.Headers.Authorization = auth;
            using var response = await _http.SendAsync(message, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            var reason = response.StatusCode == HttpStatusCode.Forbidden
                ? $"The probe issue could not be deleted - the token lacks the \"Delete issues\" permission (403)."
                : $"The probe issue could not be deleted - the delete request returned {(int)response.StatusCode}.";
            return $"{reason} It was left behind - {issueKey} ({issueUrl}). Please delete it manually.";
        }
        catch (Exception ex)
        {
            return $"The probe issue could not be deleted: {ex.Message} It was left behind - {issueKey} ({issueUrl}). Please delete it manually.";
        }
    }

    private static async Task<string> MapCreateBadRequestAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var jiraMessage = await TryReadJiraErrorAsync(response, cancellationToken);
        return jiraMessage is null
            ? "Could not create an issue - Jira rejected the request (400)."
            : $"Could not create an issue - Jira rejected the request (400): {jiraMessage}.";
    }

    /// <summary>
    /// Builds a failure detail that carries Jira's own error text ("<paramref name="subject"/> returned
    /// {status}: {Jira's errorMessages}") rather than a bare status code, so the real cause is visible.
    /// </summary>
    private static async Task<string> DescribeJiraFailureAsync(HttpResponseMessage response, string subject, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var jiraMessage = await TryReadJiraErrorAsync(response, cancellationToken);
        return jiraMessage is null
            ? $"{subject} returned {status}."
            : $"{subject} returned {status}: {jiraMessage}";
    }

    /// <summary>
    /// Reads Jira's error envelope - <c>{ "errorMessages": [...], "errors": { "field": "message" } }</c> - into a
    /// single joined string, or null when the body carries no usable message.
    /// </summary>
    private static async Task<string?> TryReadJiraErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JiraErrorResponse>(cancellationToken);
            var parts = new List<string>();
            if (body?.Errors is { Count: > 0 } errors)
            {
                parts.AddRange(errors.Select(e => $"{e.Key}: {e.Value}"));
            }

            if (body?.ErrorMessages is { Count: > 0 } messages)
            {
                parts.AddRange(messages);
            }

            return parts.Count > 0 ? string.Join("; ", parts) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string MapFailure(string operation, string projectKey, HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => $"The token was not accepted (401) when trying to {operation}.",
        HttpStatusCode.Forbidden => $"The token lacks permission to {operation} in project '{projectKey}' (403).",
        HttpStatusCode.NotFound => $"Could not {operation} - project '{projectKey}' was not found (404).",
        _ => $"Could not {operation} - the request returned {(int)statusCode}.",
    };

    /// <summary>A minimal Atlassian Document Format (ADF) body - the v3 REST API requires ADF, not plain text.</summary>
    private static object ProbeDescription(string text) => new
    {
        type = "doc",
        version = 1,
        content = new object[]
        {
            new
            {
                type = "paragraph",
                content = new object[] { new { type = "text", text } },
            },
        },
    };

    /// <summary>
    /// A read check whose FAILURE names the provider and the condition (backlog 76). "Request returned 401."
    /// told the user nothing about whose fault it was; an expired token, a renamed project and a provider-side
    /// outage all looked identical, and all looked like ours.
    /// </summary>
    private async Task<AccessCheck> GetCheckAsync(
        string capability, AuthenticationHeaderValue auth, string url, string mechanism,
        string provider, string subject, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            message.Headers.Authorization = auth;
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(message, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new AccessCheck(capability, AccessProbeOutcome.Passed, mechanism, $"Request succeeded ({(int)response.StatusCode}).");
            }

            var said = await response.Content.ReadAsStringAsync(cancellationToken);
            return new AccessCheck(capability, AccessProbeOutcome.Failed, mechanism,
                ProviderFailure.Describe(provider, subject, response.StatusCode, said));
        }
        catch (Exception ex)
        {
            return new AccessCheck(capability, AccessProbeOutcome.Failed, mechanism, ex.Message);
        }
    }

    private async Task<AccessCheck> WiqlQueryCheckAsync(AuthenticationHeaderValue auth, string baseUrl, string project, CancellationToken cancellationToken)
    {
        const string mechanism = "Azure DevOps POST _apis/wit/wiql";
        try
        {
            var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{project.Replace("'", "''", StringComparison.Ordinal)}' ORDER BY [System.ChangedDate] DESC";
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/{Uri.EscapeDataString(project)}/_apis/wit/wiql?api-version=7.0")
            {
                Content = new StringContent($"{{\"query\":{JsonSerializer.Serialize(wiql)}}}", Encoding.UTF8, "application/json"),
            };
            message.Headers.Authorization = auth;
            using var response = await _http.SendAsync(message, cancellationToken);
            return response.IsSuccessStatusCode
                ? new AccessCheck("Query", AccessProbeOutcome.Passed, mechanism, "A WIQL query ran (read-only).")
                : new AccessCheck("Query", AccessProbeOutcome.Failed, mechanism,
                    await DescribeAdoFailureAsync(response, $"A WIQL query against project '{project}' failed", cancellationToken));
        }
        catch (Exception ex)
        {
            return new AccessCheck("Query", AccessProbeOutcome.Failed, mechanism, ex.Message);
        }
    }

    private async Task<AccessCheck> JqlQueryCheckAsync(AuthenticationHeaderValue auth, string baseUrl, CancellationToken cancellationToken)
    {
        const string mechanism = "Jira POST /rest/api/3/search/jql";
        try
        {
            // The enhanced JQL endpoint REJECTS unbounded queries with 400 ("Unbounded JQL queries are not
            // allowed here. Please add a search restriction to your query."). A bare "order by created DESC"
            // is unbounded, which is what produced the live 400. Bounding it with `created >= "1970-01-01"`
            // satisfies the restriction while still matching every real issue. No startAt (nextPageToken
            // paginates) and no fields returned unless requested, so "id" alone proves query access cheaply.
            // This replaces the retired POST /rest/api/3/search (410 Gone).
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/rest/api/3/search/jql")
            {
                Content = new StringContent(
                    "{\"jql\":\"created >= \\\"1970-01-01\\\" order by created DESC\",\"maxResults\":1,\"fields\":[\"id\"]}",
                    Encoding.UTF8, "application/json"),
            };
            message.Headers.Authorization = auth;
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(message, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new AccessCheck("Query", AccessProbeOutcome.Passed, mechanism, "An enhanced-JQL search ran (read-only).");
            }

            // Surface Jira's own errorMessages (a bare status code hides the real cause, e.g. the unbounded-JQL rule).
            var detail = await DescribeJiraFailureAsync(response, "The search", cancellationToken);
            return new AccessCheck("Query", AccessProbeOutcome.Failed, mechanism, detail);
        }
        catch (Exception ex)
        {
            return new AccessCheck("Query", AccessProbeOutcome.Failed, mechanism, ex.Message);
        }
    }

    // ── Azure DevOps response shapes (only the fields the probe reads) ────────────────────────────────────

    private sealed record AdoTypeMeta(string Name, IReadOnlyList<AdoState> States);

    private readonly record struct CleanupOutcome(string? Note, string? Leftover);

    private sealed record WorkItemTypesResponse([property: JsonPropertyName("value")] List<WorkItemTypeRef>? Value);

    private sealed record WorkItemTypeRef(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("isDisabled")] bool IsDisabled);

    private sealed record StatesResponse([property: JsonPropertyName("value")] List<AdoState>? Value);

    private sealed record AdoState(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("category")] string? Category);

    private sealed record CreatedWorkItem(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("fields")] WorkItemFields? Fields)
    {
        public string? State => Fields?.State;
    }

    private sealed record WorkItemFields([property: JsonPropertyName("System.State")] string? State);

    private sealed record AdoErrorResponse([property: JsonPropertyName("message")] string? Message);

    // ── Jira response shapes (only the fields the probe reads) ─────────────────────────────────────────────

    private sealed record ProjectMeta(string? IssueTypeId, string? IssueTypeName);

    private sealed record ProjectResponse(
        [property: JsonPropertyName("issueTypes")] List<IssueTypeRef>? IssueTypes);

    private sealed record IssueTypeRef(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("subtask")] bool Subtask);

    private sealed record CreatedIssue(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("key")] string? Key);

    private sealed record TransitionsResponse(
        [property: JsonPropertyName("transitions")] List<TransitionRef>? Transitions);

    private sealed record TransitionRef(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record JiraErrorResponse(
        [property: JsonPropertyName("errorMessages")] List<string>? ErrorMessages,
        [property: JsonPropertyName("errors")] Dictionary<string, string>? Errors);
}
