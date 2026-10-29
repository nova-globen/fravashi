using Fravashi.Application.Network;

namespace Fravashi.Launcher.Releases;

/// <summary>
/// Fetches the release manifest over anonymous HTTPS (ADR 0006 / D03-1). The URL is the public releases repo's
/// manifest asset - parameterized (launcher.json <c>manifestUrl</c>, else <see cref="LauncherDefaults.ManifestUrl"/>)
/// so the owner points it at the real repo once it exists and tests can point it at a local file/server. Any
/// network, HTTP or parse failure is answered as "could not check" with its reason, never an exception at the caller
/// and never "no manifest" (backlog 230: that answer was recorded as "nothing newer" and lifted a critical lock).
/// <para>
/// Backlog 242: the manifest is read as BYTES and its signature (<see cref="ReleaseManifestSignature.SidecarName"/>)
/// is fetched from beside the channel address that was ASKED, never from where its redirect landed:
/// the channel redirects each file to a GitHub release asset, whose final address names no file, so a name resolved
/// against it is not the signature. The bytes are verified against the release key before a single field is parsed. No signature (404 or 410) or a wrong one is a refusal, and a refused manifest is
/// handed to nobody. Any other failure to fetch the signature is "could not check", like the manifest's own.
/// </para>
/// </summary>
public sealed class HttpReleaseManifestSource : IReleaseManifestSource
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly Action<string>? _log;
    private readonly string? _publicKeyPem;

    /// <param name="publicKeyPem">The key to verify with; null is the release key embedded in this build. Tests only.</param>
    public HttpReleaseManifestSource(string url, HttpClient? http = null, Action<string>? log = null, string? publicKeyPem = null)
    {
        _url = url;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _log = log;
        _publicKeyPem = publicKeyPem;
    }

    public async Task<ManifestFetch> FetchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] bytes;
            using (var response = await _http.GetAsync(_url, cancellationToken))
            {
                if (!response.IsSuccessStatusCode)
                {
                    _log?.Invoke($"Update check: the manifest returned HTTP {(int)response.StatusCode}.");
                    return ManifestFetch.Failed($"it answered HTTP {(int)response.StatusCode}");
                }

                bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }

            var sidecarUrl = new Uri(new Uri(_url), ReleaseManifestSignature.SidecarName);
            string? sidecar;
            using (var response = await _http.GetAsync(sidecarUrl, cancellationToken))
            {
                var status = (int)response.StatusCode;
                if (status is 404 or 410)
                {
                    sidecar = null;
                }
                else if (!response.IsSuccessStatusCode)
                {
                    _log?.Invoke($"Update check: the manifest's signature returned HTTP {status}.");
                    return ManifestFetch.Failed($"its signature answered HTTP {status}");
                }
                else
                {
                    sidecar = await response.Content.ReadAsStringAsync(cancellationToken);
                }
            }

            var signature = ReleaseManifestSignature.Verify(bytes, sidecar, _publicKeyPem);
            if (signature != ManifestSignature.Verified)
            {
                var refused = ManifestFetch.Refused(signature);
                _log?.Invoke($"Update check: {refused.CouldNotCheck}.");
                return refused;
            }

            // The bytes are verified as they came; only the text handed to the parser leaves a byte order mark out.
            var text = bytes.AsSpan();
            var json = System.Text.Encoding.UTF8.GetString(text.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? text[3..] : text);
            if (ReleaseManifestParser.TryParse(json, out var manifest))
            {
                return ManifestFetch.Read(manifest);
            }

            _log?.Invoke("Update check: the manifest could not be read.");
            return ManifestFetch.Failed("its manifest could not be read");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            var reason = ReasonFor(ex, cancellationToken);
            _log?.Invoke($"Update check: the release channel could not be reached ({reason}).");
            return ManifestFetch.Failed(reason);
        }
    }

    /// <summary>The words for a failed call: slice A's classifier when it is the network, else what it was.</summary>
    internal static string ReasonFor(Exception ex, CancellationToken cancellationToken)
    {
        var failure = NetworkFailure.Classify(ex, cancellationToken);
        if (failure.IsNetwork)
        {
            return failure.Words;
        }

        return ex is OperationCanceledException ? "the check was cancelled" : ex.Message;
    }
}

/// <summary>
/// Streams a release asset to disk over HTTPS. It verifies only that the body is as long as the server said
/// (a short body is a retryable failure, backlog 230); the caller checks the whole file's sha256 after.
/// </summary>
public sealed class HttpReleaseAssetDownloader : IReleaseAssetDownloader
{
    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    public HttpReleaseAssetDownloader(HttpClient? http = null, Action<string>? log = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _log = log;
    }

    public async Task<AssetDownload> DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                _log?.Invoke($"Download failed: HTTP {status}.");
                return AssetDownload.Failed($"it answered HTTP {status}", retryable: status >= 500);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            long written;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = File.Create(destinationPath))
            {
                await source.CopyToAsync(target, cancellationToken);
                written = target.Length;
            }

            if (response.Content.Headers.ContentLength is { } expected && written != expected)
            {
                TryDelete(destinationPath);
                _log?.Invoke($"Download failed: only {written:N0} of {expected:N0} bytes arrived.");
                return AssetDownload.Failed($"only {written:N0} of {expected:N0} bytes arrived", retryable: true);
            }

            return AssetDownload.Done(destinationPath);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            TryDelete(destinationPath);
            var failure = NetworkFailure.Classify(ex, cancellationToken);
            var reason = HttpReleaseManifestSource.ReasonFor(ex, cancellationToken);
            _log?.Invoke($"Download failed: {reason}");
            return AssetDownload.Failed(reason, retryable: failure.IsNetwork);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The caller deletes its temp folder too.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }
}
