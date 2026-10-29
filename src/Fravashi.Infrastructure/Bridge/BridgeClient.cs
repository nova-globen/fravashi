using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fravashi.Application.Sessions;

namespace Fravashi.Infrastructure.Bridge;

/// <summary>
/// The studio's HTTP client for the host bridge (plan 2a), scoped to the ACTIVE workspace from
/// <see cref="BridgeWorkspaceContext"/>. Thin by design: one method per endpoint, wire DTOs in,
/// wire DTOs out; the seam implementations (reader/writer/search/git/watch) own all semantics.
/// Construct only when the context is active.
/// </summary>
public class BridgeClient
{
    private const string TokenHeader = "X-Fravashi-Bridge";
    private const string ModifiedUtcHeader = "X-Fravashi-Modified-Utc";
    private const string ExpectedUtcHeader = "X-Fravashi-Expected-Utc";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _workspaceId;

    public BridgeClient(HttpClient http, BridgeWorkspaceContext context)
    {
        _http = http;
        _workspaceId = context.ActiveWorkspaceId ?? string.Empty;
        if (context.IsBridgeConfigured)
        {
            _http.BaseAddress = new Uri(context.BaseUrl!.TrimEnd('/') + "/");
            _http.DefaultRequestHeaders.Remove(TokenHeader);
            _http.DefaultRequestHeaders.Add(TokenHeader, context.Token);
        }
    }

    public string WorkspaceId => _workspaceId;

    public virtual async Task<IReadOnlyList<BridgeWorkspaceDto>> ListWorkspacesAsync(CancellationToken ct)
        => await GetJsonAsync<List<BridgeWorkspaceDto>>("bridge/workspaces", "listing the workspaces", ct)
            .ConfigureAwait(false) ?? [];

    /// <summary>
    /// What a person reads when the bridge answered with anything but success (backlog 177): the act and
    /// the path it was about, then the status, for example "reading .gitignore was refused (403)". The
    /// HTTP client's own message named only the status, so a copy that could not start said a 403 happened
    /// and not to what.
    /// </summary>
    public static string DescribeFailure(string act, HttpStatusCode status)
        => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? $"{act} was refused ({(int)status})"
            : $"{act} failed ({(int)status})";

    /// <summary>
    /// Throws when the bridge did not answer with success. Still an <see cref="HttpRequestException"/>
    /// carrying the status, so every caller that catches one or filters on its status is unchanged; only the
    /// message says what was asked. The full exception is what the logs keep.
    /// </summary>
    private static void EnsureAnswered(HttpResponseMessage response, string act)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                DescribeFailure(act, response.StatusCode), inner: null, response.StatusCode);
        }
    }

    private static string Named(string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath) ? "the workspace root" : relativePath;

    private async Task<T?> GetJsonAsync<T>(string url, string act, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        EnsureAnswered(response, act);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Grants the bridge access to a host folder, returning its workspace (the id is a hash of the
    /// canonical path, so the same folder always yields the same id). Only the HOST can decide any
    /// of this: the studio runs in a container where a host path never exists, so it must never
    /// pre-check one itself.
    /// <para>
    /// The answer is three-way on purpose. "Not there" (404) and "cannot be reached right now"
    /// (503, e.g. an unmapped drive or an unplugged disk) are different facts, and collapsing them
    /// is what let a perfectly good workspace be presented to the user as deleted.
    /// </para>
    /// </summary>
    public virtual async Task<BridgeRegistration> RegisterWorkspaceAsync(string path, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("bridge/workspaces", new { path }, Json, ct)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return BridgeRegistration.Missing;
        }

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            return BridgeRegistration.Unreachable;
        }

        EnsureAnswered(response, $"registering the folder {path}");
        var workspace = await response.Content.ReadFromJsonAsync<BridgeWorkspaceDto>(Json, ct).ConfigureAwait(false);
        // A success with no body says nothing about the folder, so it must not read as "gone".
        return workspace is null ? BridgeRegistration.Unreachable : BridgeRegistration.Registered(workspace);
    }

    /// <summary>Asks the HOST to show its folder picker; null when cancelled or unavailable.</summary>
    public virtual async Task<BridgeWorkspaceDto?> PickFolderAsync(CancellationToken ct)
    {
        using var response = await _http.PostAsync("bridge/pick-folder", content: null, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureAnswered(response, "asking the host for a folder");
        return await response.Content.ReadFromJsonAsync<BridgeWorkspaceDto>(Json, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Clones a repository HOST-SIDE (plan 2c): the bridge pops the host's folder picker for the
    /// target parent, so the studio only ever supplies the URL. Null when the picker was
    /// cancelled or the host offers none; throws <see cref="InvalidOperationException"/> with the
    /// bridge's message when the clone itself fails.
    /// </summary>
    public virtual async Task<BridgeWorkspaceDto?> CloneAsync(string url, CancellationToken ct)
    {
        // A dedicated single-use client: the shared typed client's timeout is right for every
        // other bridge call, but a clone legitimately runs for minutes (and the host may pop a
        // credential-manager window on top).
        using var http = new HttpClient { BaseAddress = _http.BaseAddress, Timeout = TimeSpan.FromMinutes(30) };
        if (_http.DefaultRequestHeaders.TryGetValues(TokenHeader, out var token))
        {
            http.DefaultRequestHeaders.Add(TokenHeader, token);
        }

        using var response = await http.PostAsJsonAsync("bridge/clone", new { url }, Json, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new InvalidOperationException(
                "That does not look like a cloneable URL (use https://, ssh://, or git@host:path).");
        }

        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadGateway)
        {
            throw new InvalidOperationException(await ReadErrorAsync(response, ct).ConfigureAwait(false));
        }

        // The URL is not repeated: it can carry a credential.
        EnsureAnswered(response, "cloning the repository");
        return await response.Content.ReadFromJsonAsync<BridgeWorkspaceDto>(Json, ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } message)
            {
                return message;
            }
        }
        catch (JsonException)
        {
        }

        return "The clone failed.";
    }

    public virtual async Task<IReadOnlyList<BridgeEntryDto>> ListAsync(string? relativePath, CancellationToken ct)
    {
        var url = $"bridge/ws/{_workspaceId}/tree?path={Uri.EscapeDataString(relativePath ?? string.Empty)}";
        return await GetJsonAsync<List<BridgeEntryDto>>(url, $"listing {Named(relativePath)}", ct)
            .ConfigureAwait(false) ?? [];
    }

    /// <summary>Stat via the parent listing (the bridge has no dedicated stat endpoint); null when absent.</summary>
    public virtual async Task<BridgeEntryDto?> StatAsync(string relativePath, CancellationToken ct)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        var slash = normalized.LastIndexOf('/');
        var parent = slash < 0 ? string.Empty : normalized[..slash];
        var name = slash < 0 ? normalized : normalized[(slash + 1)..];
        try
        {
            var entries = await ListAsync(parent, ct).ConfigureAwait(false);
            return entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public virtual async Task<BridgeFileContent?> ReadAsync(string relativePath, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            $"bridge/ws/{_workspaceId}/file?path={Uri.EscapeDataString(relativePath)}",
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureAnswered(response, $"reading {Named(relativePath)}");
        DateTimeOffset? stamp = null;
        if (response.Headers.TryGetValues(ModifiedUtcHeader, out var values)
            && DateTimeOffset.TryParse(values.FirstOrDefault(), null, DateTimeStyles.RoundtripKind, out var parsed))
        {
            stamp = parsed;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return new BridgeFileContent(bytes, stamp);
    }

    /// <summary>
    /// At most the first <paramref name="maxBytes"/> of a file (item #373: a title or a list's first lines). The bridge
    /// streams the file, so the read stops there and the rest is never fetched; null when the file is not there.
    /// </summary>
    public virtual async Task<byte[]?> ReadHeadAsync(string relativePath, int maxBytes, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            $"bridge/ws/{_workspaceId}/file?path={Uri.EscapeDataString(relativePath)}",
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureAnswered(response, $"reading {Named(relativePath)}");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[Math.Max(0, maxBytes)];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return total == buffer.Length ? buffer : buffer[..total];
    }

    public virtual async Task<DateTimeOffset> WriteAsync(
        string relativePath, Stream content, DateTimeOffset? expectedModifiedUtc, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"bridge/ws/{_workspaceId}/file?path={Uri.EscapeDataString(relativePath)}")
        {
            Content = new StreamContent(content),
        };
        if (expectedModifiedUtc is { } expected)
        {
            request.Headers.Add(ExpectedUtcHeader, expected.UtcDateTime.ToString("O"));
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new BridgeConflictException(missing: false);
        }

        if (response.StatusCode == HttpStatusCode.Gone)
        {
            throw new BridgeConflictException(missing: true);
        }

        EnsureAnswered(response, $"writing {Named(relativePath)}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return body.RootElement.GetProperty("modifiedUtc").GetDateTimeOffset();
    }

    public virtual async Task CreateDirectoryAsync(string relativePath, CancellationToken ct)
    {
        using var response = await _http.PostAsync(
                $"bridge/ws/{_workspaceId}/dir?path={Uri.EscapeDataString(relativePath)}", content: null, ct)
            .ConfigureAwait(false);
        EnsureAnswered(response, $"creating the folder {Named(relativePath)}");
    }

    public virtual async Task DeleteAsync(string relativePath, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync(
                $"bridge/ws/{_workspaceId}/entry?path={Uri.EscapeDataString(relativePath)}", ct)
            .ConfigureAwait(false);
        EnsureAnswered(response, $"deleting {Named(relativePath)}");
    }

    public virtual async Task RenameAsync(string from, string to, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
                $"bridge/ws/{_workspaceId}/rename", new { from, to }, Json, ct)
            .ConfigureAwait(false);
        EnsureAnswered(response, $"renaming {Named(from)} to {Named(to)}");
    }

    /// <summary>
    /// Creates (or finds already made) #306's own worktree for one work item, off the named base branch. The
    /// active workspace is the REPOSITORY: #308's own per-item worktree is a SEPARATE registered workspace the
    /// bridge hands back on <see cref="BridgeWorktreeDto.Workspace"/>, never this client's own
    /// <see cref="WorkspaceId"/>.
    /// </summary>
    public virtual async Task<BridgeWorktreeDto> CreateWorktreeAsync(int item, string baseBranch, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
                $"bridge/ws/{_workspaceId}/worktrees", new { item, baseBranch }, Json, ct)
            .ConfigureAwait(false);
        EnsureAnswered(response, $"creating a worktree for item {item.ToString(CultureInfo.InvariantCulture)}");
        return await response.Content.ReadFromJsonAsync<BridgeWorktreeDto>(Json, ct).ConfigureAwait(false)
            ?? throw new HttpRequestException($"The bridge answered with no worktree for item {item.ToString(CultureInfo.InvariantCulture)}.");
    }

    /// <summary>
    /// Prunes the item's own worktree. Never throws for an ordinary outcome: the status alone says whether it
    /// was removed (204), there was nothing to prune (404), or it was left in place, dirty (409, whose body
    /// names why).
    /// </summary>
    public virtual async Task<(HttpStatusCode Status, string? Detail)> PruneWorktreeAsync(int item, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"bridge/ws/{_workspaceId}/worktrees/{item.ToString(CultureInfo.InvariantCulture)}", ct)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return (response.StatusCode, null);
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement?>(Json, ct).ConfigureAwait(false);
        var detail = body is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty("error", out var error)
            ? error.GetString()
            : null;
        return (response.StatusCode, detail);
    }

    /// <summary>How long a git command that talks to a remote may run (backlog 231, plan @57).</summary>
    public static readonly TimeSpan RemoteGitTimeout = TimeSpan.FromMinutes(10);

    // One client for every remote git command of every workspace: the shared typed client's 100 seconds would stop a
    // push on a slow link, and stopping the request makes the bridge kill git on the host mid-transfer. Its own handler
    // pools connections like the typed client's; the token rides each request, not the client.
    private static readonly HttpClient RemoteGitHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = RemoteGitTimeout,
    };

    /// <summary>True for the git commands that talk to a remote and so run on the long-timeout client.</summary>
    public static bool IsRemoteGit(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] is "fetch" or "pull" or "push" or "ls-remote";

    public virtual async Task<BridgeGitResultDto> GitAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var url = $"bridge/ws/{_workspaceId}/git";
        using var response = IsRemoteGit(args) && _http.BaseAddress is { } baseAddress
            ? await SendRemoteGitAsync(new Uri(baseAddress, url), args, ct).ConfigureAwait(false)
            : await _http.PostAsJsonAsync(url, new { args }, Json, ct).ConfigureAwait(false);
        EnsureAnswered(response, args.Count > 0 ? $"running git {args[0]}" : "running git");
        return (await response.Content.ReadFromJsonAsync<BridgeGitResultDto>(Json, ct).ConfigureAwait(false))!;
    }

    private async Task<HttpResponseMessage> SendRemoteGitAsync(Uri uri, IReadOnlyList<string> args, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(new { args }, options: Json) };
        if (_http.DefaultRequestHeaders.TryGetValues(TokenHeader, out var token))
        {
            request.Headers.Add(TokenHeader, token);
        }

        return await RemoteGitHttp.SendAsync(request, ct).ConfigureAwait(false);
    }

    public virtual async Task<IReadOnlyList<BridgeSearchMatchDto>> SearchAsync(object request, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            $"bridge/ws/{_workspaceId}/search", request, Json, ct).ConfigureAwait(false);
        EnsureAnswered(response, "searching the workspace");
        return await response.Content.ReadFromJsonAsync<List<BridgeSearchMatchDto>>(Json, ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>
    /// The conversations the user ran with the CLI on the host for this workspace (backlog 43). The bridge
    /// derives the folder from the workspace's HOST root; the studio never names one.
    /// </summary>
    public virtual async Task<IReadOnlyList<BridgeCliConversationDto>> ListCliConversationsAsync(CancellationToken ct)
        => await GetJsonAsync<List<BridgeCliConversationDto>>(
            $"bridge/ws/{_workspaceId}/cli-conversations", "listing the host's CLI conversations", ct)
            .ConfigureAwait(false) ?? [];

    /// <summary>One conversation file's bytes; missing (404) and over the bridge's cap (413) are answers, not errors.</summary>
    public virtual async Task<CliConversationContent> ReadCliConversationAsync(
        string name, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            $"bridge/ws/{_workspaceId}/cli-conversations/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return CliConversationContent.Missing;
        }

        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            return CliConversationContent.TooLarge;
        }

        EnsureAnswered(response, $"reading the CLI conversation {name}");
        return new CliConversationContent(
            CliConversationReadStatus.Read,
            await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The event delivered each time the stream is (re)established, with an empty path (backlog 253). The bridge
    /// reports nothing for a client that is not connected, so whatever changed in between is known only to a consumer
    /// that looks again; this is its cue.
    /// </summary>
    public const string ConnectedEventType = "connected";

    /// <summary>
    /// The event the bridge sends, with an empty path, when it knows it lost events (its watcher overflowed, or this
    /// subscriber's queue dropped some). A consumer that keeps a copy looks again.
    /// </summary>
    public const string OverflowEventType = "overflow";

    /// <summary>
    /// How long a stream may go without a single line before it is taken as dropped (backlog 257): three of the
    /// bridge's 15 second heartbeats.
    /// </summary>
    public static readonly TimeSpan DefaultEventSilenceLimit = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long the event stream may go without a line before it is ended and reconnected (backlog 257). It applies to
    /// a connection only once that connection has delivered a heartbeat comment, because an older fravashi.exe sends none
    /// and its stream is legitimately silent for as long as nothing changes on the host.
    /// </summary>
    public TimeSpan EventSilenceLimit { get; init; } = DefaultEventSilenceLimit;

    /// <summary>
    /// Reads the workspace's SSE event stream, invoking <paramref name="onEvent"/> per event, and
    /// reconnecting with a 2s delay until <paramref name="ct"/> fires. Each connection starts with a
    /// <see cref="ConnectedEventType"/> event. Only <paramref name="ct"/> ends it: another cancellation (the HTTP
    /// client's own timeout, inside a handler) used to end it too, silently and for good (backlog 253). A connection that
    /// has shown it carries heartbeats and then says nothing for <see cref="EventSilenceLimit"/> is treated as dropped,
    /// because one that dies without a close (sleep and resume, a network switch) otherwise waits for ever (backlog 257).
    /// </summary>
    public virtual async Task SubscribeEventsAsync(Func<BridgeFileEventDto, Task> onEvent, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var response = await _http.GetAsync(
                    $"bridge/ws/{_workspaceId}/events", HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                EnsureAnswered(response, "following the workspace's changes");
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                await onEvent(new BridgeFileEventDto(ConnectedEventType, string.Empty, null)).ConfigureAwait(false);
                var heardHeartbeat = false;
                while (!ct.IsCancellationRequested)
                {
                    var line = await ReadLineAsync(reader, heardHeartbeat, ct).ConfigureAwait(false);
                    if (line is null)
                    {
                        break; // stream ended, or went silent past the limit: reconnect
                    }

                    if (line.StartsWith(':'))
                    {
                        heardHeartbeat = true; // an SSE comment: only the heartbeat sends one
                        continue;
                    }

                    if (line.StartsWith("data: ", StringComparison.Ordinal))
                    {
                        var payload = JsonSerializer.Deserialize<BridgeFileEventDto>(line["data: ".Length..], Json);
                        if (payload is not null)
                        {
                            await onEvent(payload).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
            {
                // Bridge briefly away (fravashi.exe restarting), or a timeout that was not ours to end on: retry below.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The next line, or null when the stream ended or, once <paramref name="limited"/>, stayed silent past
    /// <see cref="EventSilenceLimit"/>. Unlimited until the connection has delivered a heartbeat (backlog 257).
    /// </summary>
    private async Task<string?> ReadLineAsync(StreamReader reader, bool limited, CancellationToken ct)
    {
        if (!limited)
        {
            return await reader.ReadLineAsync(ct).ConfigureAwait(false);
        }

        using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
        silence.CancelAfter(EventSilenceLimit);
        try
        {
            return await reader.ReadLineAsync(silence.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // a dead connection: the reader is abandoned with it
        }
    }
}
