using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Fravashi.Application.Onboarding;
using Fravashi.Infrastructure.Providers;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Onboarding;

/// <summary>
/// <see cref="IWikiAccessProbe"/> for Confluence, Azure DevOps Wiki, and GitHub Wiki.
/// <para>
/// <b>Confluence</b> runs a real, user-consented round-trip that proves every operation the product needs:
/// a <b>Read</b> (list spaces), then - when a target space key is supplied - a <b>Write</b> (create a clearly
/// labelled probe page, then update it to prove modify + a version bump) and a <b>Delete</b> (remove the probe
/// page; v2 delete moves it to trash). The round-trip runs ONLY on explicit user action in the wizard and is a
/// deliberate, clearly-labelled write - the test-mode write guard exists to suppress <i>agent</i> writes, a
/// different concern, so it is not a prerequisite for a consented onboarding probe. Cleanup runs in a
/// <c>finally</c>; if the probe page cannot be deleted, the result names the page left behind so the user can
/// remove it.
/// </para>
/// <para>
/// <b>Azure DevOps Wiki</b> runs the same shape: a real Read (list the project's wikis) and - against the
/// first wiki found - a consented <b>create → edit → delete</b> page round-trip (<c>PUT
/// _apis/wiki/wikis/{wiki}/pages</c> to create, then <c>PUT</c> again with the page's <c>ETag</c> in the
/// <c>If-Match</c> header to edit, then <c>DELETE</c> in a <c>finally</c>). If the project has <i>no</i> wiki,
/// Write/Delete are honestly <see cref="AccessProbeOutcome.NotVerifiable"/> ("no wiki exists to test against -
/// create a wiki first"), not a failure. <b>GitHub Wiki</b> runs a real Read (<c>git ls-remote</c> of the
/// normalized <c>&lt;repo&gt;.wiki.git</c>) and - since GitHub wikis have no REST content API - a real,
/// user-consented <b>git push round-trip</b> for Write/Delete: shallow-clone the wiki repo, add a labelled
/// probe page and push (Write), then remove it and push (Delete). A GitHub wiki's git repo only exists once
/// its first page is created, so a genuine 404 after normalization is reported honestly ("wiki not found -
/// create the first page, then retest"), not as a raw fatal. git history is append-only, so the delete leaves
/// both probe commits in the wiki's history - reported plainly. The <c>Local</c> provider is the in-DB wiki
/// fallback (O10): recorded, not yet a working store.
/// </para>
/// </summary>
public sealed class WikiAccessProbe : IWikiAccessProbe
{
    private const string ProbeBodyHtml =
        $"<p>This page was created automatically by {ProductName.Short} to verify write access during onboarding. " +
        $"It is safe to delete - {ProductName.Short} removes it automatically as part of the same test.</p>";

    private readonly HttpClient _http;
    private readonly GitLsRemoteRunner _git;
    private readonly GitWikiRoundTripRunner _wikiGit;

    public WikiAccessProbe(HttpClient http, GitLsRemoteRunner git, GitWikiRoundTripRunner? wikiGit = null)
    {
        _http = http;
        _git = git;
        _wikiGit = wikiGit ?? new GitWikiRoundTripRunner();
    }

    public async Task<AccessProbeResult> ProbeAsync(WikiAccessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Provider switch
        {
            var p when string.Equals(p, OnboardingProviders.Local, StringComparison.OrdinalIgnoreCase) =>
                AccessProbeResult.Single("Read", AccessProbeOutcome.NotSupported, "in-app local wiki",
                    "The local in-app wiki needs no external access. It is recorded, not yet a working store (see backlog)."),
            var p when string.Equals(p, OnboardingProviders.Confluence, StringComparison.OrdinalIgnoreCase) =>
                await ProbeConfluenceAsync(request, cancellationToken),
            var p when string.Equals(p, OnboardingProviders.AzureDevOpsWiki, StringComparison.OrdinalIgnoreCase) =>
                await ProbeAzureDevOpsWikiAsync(request, cancellationToken),
            var p when string.Equals(p, OnboardingProviders.GitHubWiki, StringComparison.OrdinalIgnoreCase) =>
                await ProbeGitHubWikiAsync(request, cancellationToken),
            _ => AccessProbeResult.Single("Read", AccessProbeOutcome.NotSupported, "no adapter",
                    $"No wiki probe is implemented for provider '{request.Provider}'."),
        };
    }

    // ── Confluence: a real, user-consented create → update → delete round-trip ────────────────────────────

    private async Task<AccessProbeResult> ProbeConfluenceAsync(WikiAccessRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = (request.BaseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Token))
        {
            return AccessProbeResult.Single("Read", AccessProbeOutcome.Failed, "input validation",
                "Confluence needs a site URL, an account email, and an API token.");
        }

        var auth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{request.Email}:{request.Token}")));

        var read = await GetReadCheckAsync(auth, $"{baseUrl}/wiki/api/v2/spaces?limit=1",
            "Confluence GET /wiki/api/v2/spaces",
            "Confluence", "the space list", cancellationToken);

        // A failed read means the credential/site is wrong - do not attempt the write round-trip on top of it.
        if (read.Outcome != AccessProbeOutcome.Passed)
        {
            return new AccessProbeResult(
            [
                read,
                new AccessCheck("Write", AccessProbeOutcome.NotVerifiable, "Confluence page round-trip", "Not attempted - read access failed, so the write round-trip was skipped."),
                new AccessCheck("Delete", AccessProbeOutcome.NotVerifiable, "Confluence page round-trip", "Not attempted - read access failed, so the delete round-trip was skipped."),
            ]);
        }

        var spaceKey = (request.SpaceKey ?? string.Empty).Trim();
        if (spaceKey.Length == 0)
        {
            return new AccessProbeResult(
            [
                read,
                new AccessCheck("Write", AccessProbeOutcome.NotVerifiable, "Confluence page round-trip (needs a space key)", "Not verified - provide a space key to run the write test."),
                new AccessCheck("Delete", AccessProbeOutcome.NotVerifiable, "Confluence page round-trip (needs a space key)", "Not verified - provide a space key to run the delete test."),
            ]);
        }

        var (write, delete) = await RunConfluenceRoundTripAsync(auth, baseUrl, spaceKey, cancellationToken);
        return new AccessProbeResult([read, write, delete]);
    }

    private async Task<(AccessCheck Write, AccessCheck Delete)> RunConfluenceRoundTripAsync(
        AuthenticationHeaderValue auth, string baseUrl, string spaceKey, CancellationToken cancellationToken)
    {
        const string writeMechanism = "Confluence POST + PUT /wiki/api/v2/pages";
        var notAttemptedDelete = new AccessCheck("Delete", AccessProbeOutcome.NotVerifiable, "Confluence DELETE /wiki/api/v2/pages",
            "Not attempted - the probe page could not be created.");

        // 1. Resolve the space key → spaceId (GET /wiki/api/v2/spaces?keys=<key>).
        SpaceRef? space;
        AccessCheck? resolveFailure;
        try
        {
            (space, resolveFailure) = await ResolveSpaceIdAsync(auth, baseUrl, spaceKey, cancellationToken);
        }
        catch (Exception ex)
        {
            return (new AccessCheck("Write", AccessProbeOutcome.Failed, "Confluence GET /wiki/api/v2/spaces?keys", ex.Message),
                new AccessCheck("Delete", AccessProbeOutcome.NotVerifiable, "Confluence page round-trip",
                    "Not attempted - the space key could not be resolved."));
        }

        if (space is null)
        {
            return (resolveFailure!,
                new AccessCheck("Delete", AccessProbeOutcome.NotVerifiable, "Confluence page round-trip",
                    "Not attempted - the space key could not be resolved."));
        }

        // 2. CREATE the probe page (POST /wiki/api/v2/pages).
        var title = $"{ProductName.Short} access probe - safe to delete ({DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}Z)";
        CreatedPage? created;
        AccessCheck? createFailure;
        try
        {
            (created, createFailure) = await CreateProbePageAsync(auth, baseUrl, space.Id!, spaceKey, title, cancellationToken);
        }
        catch (Exception ex)
        {
            return (new AccessCheck("Write", AccessProbeOutcome.Failed, "Confluence POST /wiki/api/v2/pages", ex.Message), notAttemptedDelete);
        }

        if (created is null)
        {
            return (createFailure!, notAttemptedDelete);
        }

        // 3. UPDATE, then 4. DELETE - cleanup runs in the finally so it is ALWAYS attempted once a page exists.
        var pageId = created.Id!;
        var pageUrl = BuildPageUrl(baseUrl, created.Links);
        var currentVersion = created.Version?.Number ?? 1;
        var write = new AccessCheck("Write", AccessProbeOutcome.Failed, writeMechanism, "The update (modify) request did not run.");
        AccessCheck delete;
        try
        {
            write = await UpdateProbePageAsync(auth, baseUrl, pageId, title, currentVersion + 1, cancellationToken);
        }
        catch (Exception ex)
        {
            write = new AccessCheck("Write", AccessProbeOutcome.Failed, writeMechanism,
                $"The probe page was created but the update (modify) request failed: {ex.Message}");
        }
        finally
        {
            delete = await CleanupAsync(auth, baseUrl, pageId, title, pageUrl, cancellationToken);
        }

        return (write, delete);
    }

    private async Task<(SpaceRef? Space, AccessCheck? Failure)> ResolveSpaceIdAsync(
        AuthenticationHeaderValue auth, string baseUrl, string spaceKey, CancellationToken cancellationToken)
    {
        const string mechanism = "Confluence GET /wiki/api/v2/spaces?keys";
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/wiki/api/v2/spaces?keys={Uri.EscapeDataString(spaceKey)}&limit=1");
        message.Headers.Authorization = auth;
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return (null, new AccessCheck("Write", AccessProbeOutcome.Failed, mechanism,
                $"Could not resolve the space key '{spaceKey}' - the request returned {(int)response.StatusCode}."));
        }

        var payload = await response.Content.ReadFromJsonAsync<SpacesResponse>(cancellationToken);
        var space = payload?.Results?.FirstOrDefault(s => !string.IsNullOrEmpty(s.Id));
        if (space is null)
        {
            return (null, new AccessCheck("Write", AccessProbeOutcome.Failed, mechanism,
                $"Space key '{spaceKey}' was not found - no space with that key is visible to this token."));
        }

        return (space, null);
    }

    private async Task<(CreatedPage? Page, AccessCheck? Failure)> CreateProbePageAsync(
        AuthenticationHeaderValue auth, string baseUrl, string spaceId, string spaceKey, string title, CancellationToken cancellationToken)
    {
        const string mechanism = "Confluence POST /wiki/api/v2/pages";
        var body = new
        {
            spaceId,
            status = "current",
            title,
            body = new { representation = "storage", value = ProbeBodyHtml },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/wiki/api/v2/pages")
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Authorization = auth;
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return (null, new AccessCheck("Write", AccessProbeOutcome.Failed, mechanism,
                MapWriteFailure("create a page", spaceKey, response.StatusCode)));
        }

        var page = await response.Content.ReadFromJsonAsync<CreatedPage>(cancellationToken);
        if (page is null || string.IsNullOrEmpty(page.Id))
        {
            return (null, new AccessCheck("Write", AccessProbeOutcome.Failed, mechanism,
                "The create request succeeded but returned no page id, so the round-trip could not continue."));
        }

        return (page, null);
    }

    private async Task<AccessCheck> UpdateProbePageAsync(
        AuthenticationHeaderValue auth, string baseUrl, string pageId, string title, int newVersionNumber, CancellationToken cancellationToken)
    {
        const string mechanism = "Confluence POST + PUT /wiki/api/v2/pages";
        var body = new
        {
            id = pageId,
            status = "current",
            title,
            body = new { representation = "storage", value = ProbeBodyHtml + $"<p>Updated by the {ProductName.Short} access probe.</p>" },
            version = new { number = newVersionNumber, message = $"{ProductName.Short} access probe update" },
        };

        using var message = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/wiki/api/v2/pages/{pageId}")
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Authorization = auth;
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(message, cancellationToken);

        return response.IsSuccessStatusCode
            ? new AccessCheck("Write", AccessProbeOutcome.Passed, mechanism,
                $"Created and updated a probe page (a real create + modify round-trip, version bumped to {newVersionNumber}).")
            : new AccessCheck("Write", AccessProbeOutcome.Failed, mechanism,
                $"The probe page was created but the update (modify) request returned {(int)response.StatusCode}.");
    }

    /// <summary>
    /// Deletes the probe page (v2 moves it to trash) and reports honestly which mechanism succeeded. The v2
    /// endpoint on Confluence Cloud intermittently returns 500 - seen against personal spaces (<c>~key</c>) and
    /// freshly-modified pages - so a failed v2 delete falls back to the v1 content API
    /// (<c>DELETE /wiki/rest/api/content/{id}</c>), which is a reliable alternative; the fallback is named in the
    /// result. Always safe to call; if the page id is unknown or every delete fails, the returned Delete check
    /// names the leftover page so the user can remove it. A best-effort purge of the trashed page is attempted
    /// but never fails the check.
    /// </summary>
    private async Task<AccessCheck> CleanupAsync(
        AuthenticationHeaderValue auth, string baseUrl, string? pageId, string title, string? pageUrl, CancellationToken cancellationToken)
    {
        const string v2Mechanism = "Confluence DELETE /wiki/api/v2/pages/{id}";
        const string v1Mechanism = "Confluence DELETE /wiki/rest/api/content/{id} (v1 fallback)";
        if (string.IsNullOrEmpty(pageId))
        {
            return new AccessCheck("Delete", AccessProbeOutcome.NotVerifiable, v2Mechanism,
                "Not attempted - no probe page id was available to delete.");
        }

        // Primary: v2 DELETE (moves the page to trash).
        var v2 = await TryDeleteAsync(auth, $"{baseUrl}/wiki/api/v2/pages/{pageId}", cancellationToken);
        if (v2.Deleted)
        {
            var purged = await TryPurgeV2Async(auth, baseUrl, pageId, cancellationToken);
            return new AccessCheck("Delete", AccessProbeOutcome.Passed, v2Mechanism, purged
                ? "Deleted the probe page and purged it from trash (v2 delete)."
                : "Deleted the probe page (v2 delete moved it to trash - Confluence retains trashed pages; permanent purge needs space-admin rights).");
        }

        // Fallback: the v2 delete failed (a 500 here is a known Confluence Cloud fault). Try the v1 content API.
        var v1 = await TryDeleteAsync(auth, $"{baseUrl}/wiki/rest/api/content/{pageId}", cancellationToken);
        if (v1.Deleted)
        {
            var v2Why = v2.Reason;
            var purged = await TryPurgeV1Async(auth, baseUrl, pageId, cancellationToken);
            return new AccessCheck("Delete", AccessProbeOutcome.Passed, v1Mechanism, purged
                ? $"The v2 delete failed ({v2Why}); deleted the probe page via the v1 content API and purged it from trash."
                : $"The v2 delete failed ({v2Why}); deleted the probe page via the v1 content API (moved to trash - Confluence retains trashed pages; permanent purge needs space-admin rights).");
        }

        // Both mechanisms failed - name the leftover so the user can remove it.
        return new AccessCheck("Delete", AccessProbeOutcome.Failed, v2Mechanism, LeftoverMessage(
            $"The v2 delete failed ({v2.Reason}) and the v1 content-API fallback failed ({v1.Reason}).", title, pageUrl, pageId));
    }

    private readonly record struct DeleteAttempt(bool Deleted, string Reason);

    private async Task<DeleteAttempt> TryDeleteAsync(AuthenticationHeaderValue auth, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Delete, url);
            message.Headers.Authorization = auth;
            using var response = await _http.SendAsync(message, cancellationToken);
            return new DeleteAttempt(response.IsSuccessStatusCode, $"returned {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return new DeleteAttempt(false, ex.Message);
        }
    }

    private async Task<bool> TryPurgeV2Async(AuthenticationHeaderValue auth, string baseUrl, string pageId, CancellationToken cancellationToken) =>
        (await TryDeleteAsync(auth, $"{baseUrl}/wiki/api/v2/pages/{pageId}?purge=true", cancellationToken)).Deleted;

    private async Task<bool> TryPurgeV1Async(AuthenticationHeaderValue auth, string baseUrl, string pageId, CancellationToken cancellationToken) =>
        (await TryDeleteAsync(auth, $"{baseUrl}/wiki/rest/api/content/{pageId}?status=trashed", cancellationToken)).Deleted;

    private static string LeftoverMessage(string reason, string title, string? pageUrl, string pageId)
    {
        var where = string.IsNullOrEmpty(pageUrl) ? $"page id {pageId}" : pageUrl;
        return $"{reason} A probe page was left behind - \"{title}\" ({where}). Please delete it manually.";
    }

    private static string MapWriteFailure(string operation, string spaceKey, HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => $"The token was not accepted (401) when trying to {operation}.",
        HttpStatusCode.Forbidden => $"The token lacks permission to {operation} in space '{spaceKey}' (403).",
        HttpStatusCode.NotFound => $"Could not {operation} - space '{spaceKey}' was not found (404).",
        _ => $"Could not {operation} - the request returned {(int)statusCode}.",
    };

    private static string? BuildPageUrl(string baseUrl, PageLinks? links)
    {
        if (links is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(links.WebUi))
        {
            var prefix = !string.IsNullOrEmpty(links.Base) ? links.Base!.TrimEnd('/') : $"{baseUrl}/wiki";
            return links.WebUi!.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? links.WebUi : $"{prefix}{links.WebUi}";
        }

        return null;
    }

    // ── Azure DevOps Wiki: real Read, and a consented create → edit → delete page round-trip ──────────────

    private async Task<AccessProbeResult> ProbeAzureDevOpsWikiAsync(WikiAccessRequest request, CancellationToken cancellationToken)
    {
        const string readMechanism = "Azure DevOps GET _apis/wiki/wikis";
        const string noWikiMechanism = "ADO wiki page round-trip (no wiki exists)";
        var baseUrl = (request.BaseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrWhiteSpace(request.Token))
        {
            return AccessProbeResult.Single("Read", AccessProbeOutcome.Failed, "input validation",
                "Azure DevOps Wiki needs an organization/project URL and a token.");
        }

        var auth = request.Bearer
            ? new AuthenticationHeaderValue("Bearer", request.Token)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{request.Token}")));

        // Read: list the project's wikis (a real read that also selects the round-trip's target).
        WikiRef? wiki;
        AccessCheck read;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/_apis/wiki/wikis?api-version=7.1");
            message.Headers.Authorization = auth;
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new AccessProbeResult(
                [
                    new AccessCheck("Read", AccessProbeOutcome.Failed, readMechanism,
                        await DescribeAdoFailureAsync(response, "Could not list the project's wikis", cancellationToken)),
                    new AccessCheck("Write", AccessProbeOutcome.NotVerifiable, "ADO wiki page round-trip", "Not attempted - read access failed, so the write round-trip was skipped."),
                    new AccessCheck("Delete", AccessProbeOutcome.NotVerifiable, "ADO wiki page round-trip", "Not attempted - read access failed, so the delete round-trip was skipped."),
                ]);
            }

            var payload = await response.Content.ReadFromJsonAsync<WikisResponse>(cancellationToken);
            wiki = payload?.Value?.FirstOrDefault(w => !string.IsNullOrEmpty(w.Id));
            read = new AccessCheck("Read", AccessProbeOutcome.Passed, readMechanism,
                wiki is null ? "Request succeeded - the project has no wiki yet." : $"Request succeeded - using wiki '{wiki.Name}'.");
        }
        catch (Exception ex)
        {
            return new AccessProbeResult(
            [
                new AccessCheck("Read", AccessProbeOutcome.Failed, readMechanism, ex.Message),
                new AccessCheck("Write", AccessProbeOutcome.NotVerifiable, "ADO wiki page round-trip", "Not attempted - read access failed, so the write round-trip was skipped."),
                new AccessCheck("Delete", AccessProbeOutcome.NotVerifiable, "ADO wiki page round-trip", "Not attempted - read access failed, so the delete round-trip was skipped."),
            ]);
        }

        if (wiki is null)
        {
            return new AccessProbeResult(
            [
                read,
                new AccessCheck("Write", AccessProbeOutcome.NotVerifiable, noWikiMechanism, "No wiki exists to test against - create a wiki for this project first, then re-run the write test."),
                new AccessCheck("Delete", AccessProbeOutcome.NotVerifiable, noWikiMechanism, "No wiki exists to test against - create a wiki for this project first, then re-run the delete test."),
            ]);
        }

        var (write, delete) = await RunAzureDevOpsWikiRoundTripAsync(auth, baseUrl, wiki, cancellationToken);
        return new AccessProbeResult([read, write, delete]);
    }

    private async Task<(AccessCheck Write, AccessCheck Delete)> RunAzureDevOpsWikiRoundTripAsync(
        AuthenticationHeaderValue auth, string baseUrl, WikiRef wiki, CancellationToken cancellationToken)
    {
        const string writeMechanism = "Azure DevOps PUT _apis/wiki/wikis/{wiki}/pages (create + edit, If-Match)";
        const string deleteMechanism = "Azure DevOps DELETE _apis/wiki/wikis/{wiki}/pages";
        var path = $"/Fravashi-access-probe-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
        var pageApi = $"{baseUrl}/_apis/wiki/wikis/{Uri.EscapeDataString(wiki.Id!)}/pages?path={Uri.EscapeDataString(path)}&api-version=7.1";
        var pageUrl = $"{baseUrl}/_wiki/wikis/{Uri.EscapeDataString(wiki.Name ?? wiki.Id!)}?pagePath={Uri.EscapeDataString(path)}";
        var notAttemptedDelete = new AccessCheck("Delete", AccessProbeOutcome.NotVerifiable, deleteMechanism,
            "Not attempted - the probe page could not be created.");

        // 1. CREATE the probe page (PUT - returns the page's ETag, needed to edit it).
        string? etag;
        AccessCheck? createFailure;
        try
        {
            (etag, createFailure) = await CreateAdoWikiPageAsync(auth, pageApi, writeMechanism, cancellationToken);
        }
        catch (Exception ex)
        {
            return (new AccessCheck("Write", AccessProbeOutcome.Failed, writeMechanism, ex.Message), notAttemptedDelete);
        }

        if (etag is null)
        {
            return (createFailure!, notAttemptedDelete);
        }

        // 2. EDIT (If-Match), then 3. DELETE - cleanup runs in the finally so it is ALWAYS attempted.
        var write = new AccessCheck("Write", AccessProbeOutcome.Failed, writeMechanism, "The edit (modify) request did not run.");
        AccessCheck delete;
        try
        {
            write = await EditAdoWikiPageAsync(auth, pageApi, etag, writeMechanism, cancellationToken);
        }
        catch (Exception ex)
        {
            write = new AccessCheck("Write", AccessProbeOutcome.Failed, writeMechanism,
                $"The probe page was created but the edit (modify) request failed: {ex.Message}");
        }
        finally
        {
            delete = await CleanupAdoWikiPageAsync(auth, pageApi, deleteMechanism, path, pageUrl, cancellationToken);
        }

        return (write, delete);
    }

    private async Task<(string? ETag, AccessCheck? Failure)> CreateAdoWikiPageAsync(
        AuthenticationHeaderValue auth, string pageApi, string writeMechanism, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, pageApi)
        {
            Content = JsonContent.Create(new { content = $"This page was created automatically by {ProductName.Short} to verify write access during onboarding. It is safe to delete - {ProductName.Short} removes it automatically as part of the same test." }),
        };
        message.Headers.Authorization = auth;
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return (null, new AccessCheck("Write", AccessProbeOutcome.Failed, writeMechanism,
                await DescribeAdoFailureAsync(response, "Could not create a wiki page", cancellationToken)));
        }

        // ADO returns the page version as the ETag response header; it is required in If-Match to edit the page.
        var etag = response.Headers.ETag?.Tag;
        if (string.IsNullOrEmpty(etag))
        {
            return (null, new AccessCheck("Write", AccessProbeOutcome.Failed, writeMechanism,
                "The page was created but no ETag (version) was returned, so the edit (modify) step could not run."));
        }

        return (etag, null);
    }

    private async Task<AccessCheck> EditAdoWikiPageAsync(
        AuthenticationHeaderValue auth, string pageApi, string etag, string writeMechanism, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, pageApi)
        {
            Content = JsonContent.Create(new { content = $"This page was created automatically by {ProductName.Short}. It was edited by the {ProductName.Short} access probe to verify modify access, and is safe to delete." }),
        };
        message.Headers.Authorization = auth;
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await _http.SendAsync(message, cancellationToken);

        return response.IsSuccessStatusCode
            ? new AccessCheck("Write", AccessProbeOutcome.Passed, writeMechanism,
                "Created and edited a probe page (a real create + modify round-trip, edit gated by the page's If-Match version).")
            : new AccessCheck("Write", AccessProbeOutcome.Failed, writeMechanism,
                await DescribeAdoFailureAsync(response, "The probe page was created but the edit (modify) request failed", cancellationToken));
    }

    private async Task<AccessCheck> CleanupAdoWikiPageAsync(
        AuthenticationHeaderValue auth, string pageApi, string deleteMechanism, string path, string pageUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Delete, pageApi);
            message.Headers.Authorization = auth;
            using var response = await _http.SendAsync(message, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return new AccessCheck("Delete", AccessProbeOutcome.Passed, deleteMechanism, "Deleted the probe page (a real delete).");
            }

            var reason = response.StatusCode == HttpStatusCode.Forbidden
                ? "the token lacks permission to delete wiki pages (403)"
                : $"the delete request returned {(int)response.StatusCode}";
            return new AccessCheck("Delete", AccessProbeOutcome.Failed, deleteMechanism,
                $"The probe page could not be deleted ({reason}). It was left behind - \"{path}\" ({pageUrl}). Please delete it manually.");
        }
        catch (Exception ex)
        {
            return new AccessCheck("Delete", AccessProbeOutcome.Failed, deleteMechanism,
                $"The probe page could not be deleted: {ex.Message} It was left behind - \"{path}\" ({pageUrl}). Please delete it manually.");
        }
    }

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

    // ── GitHub Wiki: real Read (ls-remote), and a real, user-consented git push round-trip ────────────────

    private const string GitHubWikiReadMechanism = "git ls-remote of the wiki repository";
    private const string GitHubWikiRoundTripMechanism = "GitHub wiki git push round-trip";

    private async Task<AccessProbeResult> ProbeGitHubWikiAsync(WikiAccessRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return AccessProbeResult.Single("Read", AccessProbeOutcome.Failed, "input validation",
                "The GitHub Wiki needs the repository URL (a repo URL, its /wiki page, or <repo>.wiki.git).");
        }

        // Normalize whatever the user pasted to the one form the probe needs: <repo>.wiki.git. This accepts a
        // plain repo URL, a repo .git URL, a wiki web URL (…/wiki), or a wiki git URL (…/repo.wiki.git) - the
        // old probe blindly appended .wiki.git and mangled the last two into 404s.
        GitHubWikiTarget target;
        try
        {
            target = GitHubWikiUrl.Normalize(request.BaseUrl);
        }
        catch (FormatException ex)
        {
            return AccessProbeResult.Single("Read", AccessProbeOutcome.Failed, "input validation",
                $"Could not read a GitHub repository from the URL: {ex.Message}");
        }

        // Read: a GitHub wiki is itself a git repo at <repo>.wiki.git - ls-remote is a real, cheap read proof.
        var authUrl = GitLsRemoteRunner.BuildAuthenticatedUrl(target.WikiGitUrl, request.Token);
        var lsRemote = await _git.LsRemoteAsync(authUrl, cancellationToken);
        if (!lsRemote.Success)
        {
            return BuildGitHubWikiReadFailure(target, lsRemote);
        }

        var read = new AccessCheck("Read", AccessProbeOutcome.Passed, GitHubWikiReadMechanism,
            "The wiki repository was reachable and the credential was accepted.");

        // Without a token there is no credential to authenticate a push - keep Write/Delete honest.
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return new AccessProbeResult(
            [
                read,
                new AccessCheck("Write", AccessProbeOutcome.NotVerifiable, GitHubWikiRoundTripMechanism, "Not attempted - no token was provided to authenticate a push."),
                new AccessCheck("Delete", AccessProbeOutcome.NotVerifiable, GitHubWikiRoundTripMechanism, "Not attempted - no token was provided to authenticate a push."),
            ]);
        }

        var (write, delete) = await RunGitHubWikiRoundTripAsync(target, request.Token, cancellationToken);
        return new AccessProbeResult([read, write, delete]);
    }

    /// <summary>
    /// Turns an <c>ls-remote</c> failure into honest checks. A GitHub wiki's git repo exists only after the
    /// wiki's first page is created, so a genuine not-found after correct normalization is explained, not shown
    /// as raw fatal text; a rejected credential is distinguished from a not-found where git's stderr allows it.
    /// </summary>
    private static AccessProbeResult BuildGitHubWikiReadFailure(GitHubWikiTarget target, GitLsRemoteRunner.LsRemoteResult lsRemote)
    {
        var kind = GitRemoteFailure.Classify(lsRemote.Error);
        var (readDetail, roundTripDetail) = kind switch
        {
            GitRemoteFailureKind.NotFound => (
                $"The wiki was not found. A GitHub wiki's git repository exists only after its first page is created - "
                + $"open the repository's Wiki tab, create one page, then retest. (Looked for {target.WikiGitUrl}.)",
                "Not attempted - the wiki does not exist yet. Create the wiki's first page in the repository's Wiki tab, then retest."),
            GitRemoteFailureKind.BadCredential => (
                "The credential was not accepted - GitHub rejected the token. Check the PAT and that it grants access to this repository.",
                "Not attempted - read access failed (the credential was rejected)."),
            _ => (
                lsRemote.Error.Length == 0 ? $"git exited {lsRemote.ExitCode}." : lsRemote.Error,
                "Not attempted - read access failed, so the write round-trip was skipped."),
        };

        return new AccessProbeResult(
        [
            new AccessCheck("Read", AccessProbeOutcome.Failed, GitHubWikiReadMechanism, readDetail),
            new AccessCheck("Write", AccessProbeOutcome.NotVerifiable, GitHubWikiRoundTripMechanism, roundTripDetail),
            new AccessCheck("Delete", AccessProbeOutcome.NotVerifiable, GitHubWikiRoundTripMechanism, roundTripDetail),
        ]);
    }

    private async Task<(AccessCheck Write, AccessCheck Delete)> RunGitHubWikiRoundTripAsync(
        GitHubWikiTarget target, string token, CancellationToken cancellationToken)
    {
        const string writeMechanism = "GitHub wiki git clone + add + commit + push";
        const string deleteMechanism = "GitHub wiki git rm + commit + push";

        GitWikiRoundTripRunner.GitWikiRoundTripResult result;
        try
        {
            result = await _wikiGit.RunAsync(target.WikiGitUrl, token, cancellationToken);
        }
        catch (Exception ex)
        {
            return (new AccessCheck("Write", AccessProbeOutcome.Failed, writeMechanism, ex.Message),
                new AccessCheck("Delete", AccessProbeOutcome.NotVerifiable, deleteMechanism,
                    "Not attempted - the write round-trip threw before completing."));
        }

        if (!result.Cloned)
        {
            return (new AccessCheck("Write", AccessProbeOutcome.Failed, writeMechanism,
                    $"The wiki could not be cloned to run the write test: {result.CloneDetail}"),
                new AccessCheck("Delete", AccessProbeOutcome.NotVerifiable, deleteMechanism,
                    "Not attempted - the wiki repository could not be cloned."));
        }

        if (!result.WriteProven)
        {
            return (new AccessCheck("Write", AccessProbeOutcome.Failed, writeMechanism,
                    $"The probe page could not be pushed (typically branch protection or a token without push permission): {result.WriteDetail}"),
                new AccessCheck("Delete", AccessProbeOutcome.NotVerifiable, deleteMechanism,
                    result.DeleteDetail ?? "Not attempted - the write push failed, so nothing was left to delete."));
        }

        var write = new AccessCheck("Write", AccessProbeOutcome.Passed, writeMechanism,
            $"Cloned the wiki, added a clearly-labelled probe page, committed and pushed - a real write (author '{ProductName.Short} probe').");

        var pageUrl = $"{target.WikiWebUrl}/{Path.GetFileNameWithoutExtension(result.ProbeFileName)}";
        var delete = result.DeleteProven
            ? new AccessCheck("Delete", AccessProbeOutcome.Passed, deleteMechanism,
                "Removed the probe page, committed and pushed - a real delete. Note: git history is append-only, so both probe commits remain in the wiki's history.")
            : new AccessCheck("Delete", AccessProbeOutcome.Failed, deleteMechanism,
                $"The probe page was pushed but its removal could not be pushed ({result.DeleteDetail}). A probe page was left behind - \"{result.ProbeFileName}\" ({pageUrl}). Please delete it from the repository's Wiki tab.");
        return (write, delete);
    }

    /// <summary>
    /// A read check whose FAILURE names the provider and the condition (backlog 76), instead of the bare
    /// "Request returned 401." that made an expired token, a deleted space and a Confluence outage look alike
    /// (and look like ours).
    /// </summary>
    private async Task<AccessCheck> GetReadCheckAsync(
        AuthenticationHeaderValue auth, string url, string mechanism, string provider, string subject, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            message.Headers.Authorization = auth;
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(message, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new AccessCheck("Read", AccessProbeOutcome.Passed, mechanism, $"Request succeeded ({(int)response.StatusCode}).");
            }

            var said = await response.Content.ReadAsStringAsync(cancellationToken);
            return new AccessCheck("Read", AccessProbeOutcome.Failed, mechanism,
                ProviderFailure.Describe(provider, subject, response.StatusCode, said));
        }
        catch (Exception ex)
        {
            return new AccessCheck("Read", AccessProbeOutcome.Failed, mechanism, ex.Message);
        }
    }

    // ── Azure DevOps Wiki response shapes (only the fields the probe reads) ───────────────────────────────

    private sealed record WikisResponse([property: JsonPropertyName("value")] List<WikiRef>? Value);

    private sealed record WikiRef(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record AdoErrorResponse([property: JsonPropertyName("message")] string? Message);

    // ── Confluence v2 response shapes (only the fields the probe reads) ───────────────────────────────────

    private sealed record SpacesResponse([property: JsonPropertyName("results")] List<SpaceRef>? Results);

    private sealed record SpaceRef(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("key")] string? Key);

    private sealed record CreatedPage(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("version")] VersionRef? Version,
        [property: JsonPropertyName("_links")] PageLinks? Links);

    private sealed record VersionRef([property: JsonPropertyName("number")] int Number);

    private sealed record PageLinks(
        [property: JsonPropertyName("webui")] string? WebUi,
        [property: JsonPropertyName("base")] string? Base);
}
