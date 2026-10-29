using System.Text.Json;
using Fravashi.Application.Updates;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Updates;

/// <summary>
/// Reads the product-update state the <c>fravashi</c> launcher writes into the mounted data directory
/// (<c>update-state.json</c>) - the app side of the ADR 0006 Type B contract. The launcher is the only actor that
/// can replace the image, so it owns the manifest fetch + signature verify against the release key (backlog 242) +
/// <c>UpdateCheck.Evaluate</c> and records the outcome here; the app merely reads it to drive the critical lock and the available banner. A
/// missing file means no release channel has evaluated yet - the app degrades honestly to
/// <see cref="ProductUpdateState.None"/> (never locks). The value is cached with a short TTL so the lock
/// middleware's per-request read is cheap while still picking up a fresh <c>fravashi</c> evaluation within seconds.
/// </summary>
public sealed class FileProductUpdateStatus : IProductUpdateStatus
{
    /// <summary>The file the launcher writes into the data directory.</summary>
    public const string FileName = "update-state.json";

    /// <summary>The launcher's record of failed checks of the update channel (backlog 230).</summary>
    public const string CheckFailureFileName = "update-check.json";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly string _failurePath;
    private readonly ILogger<FileProductUpdateStatus> _logger;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private ProductUpdateState _cached = ProductUpdateState.None;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public FileProductUpdateStatus(DataDirectory dataDirectory, ILogger<FileProductUpdateStatus> logger, TimeProvider? clock = null)
    {
        _path = Path.Combine(dataDirectory.Path, FileName);
        _failurePath = Path.Combine(dataDirectory.Path, CheckFailureFileName);
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public ProductUpdateState Current
    {
        get
        {
            var now = _clock.GetUtcNow();
            lock (_gate)
            {
                if (now - _readAt < CacheTtl)
                {
                    return _cached;
                }
            }

            var fresh = Read();
            lock (_gate)
            {
                _cached = fresh;
                _readAt = now;
                return _cached;
            }
        }
    }

    private ProductUpdateState Read()
    {
        var state = ReadState();
        var failure = ReadFailure();
        return failure is null
            ? state
            : state with
            {
                ChannelUnreachableSince = failure.FailingSinceUtc,
                ChannelUnreachableReason = Clean(failure.Reason),
                ManifestRefused = ParseRefusal(failure.Signature),
            };
    }

    // Backlog 242: the launcher's stable words for a manifest it did not use because of its signature.
    private static ManifestSignatureRefusal? ParseRefusal(string? signature) => signature?.Trim().ToLowerInvariant() switch
    {
        "unsigned" => ManifestSignatureRefusal.Unsigned,
        "bad" => ManifestSignatureRefusal.BadSignature,
        _ => null,
    };

    private ProductUpdateState ReadState()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return ProductUpdateState.None;
            }

            var dto = JsonSerializer.Deserialize<UpdateStateDto>(File.ReadAllText(_path), Json);
            if (dto is null)
            {
                return ProductUpdateState.None;
            }

            return new ProductUpdateState(
                ParseSeverity(dto.Kind),
                Clean(dto.CurrentVersion),
                Clean(dto.TargetVersion),
                Clean(dto.ReleaseNotesUrl),
                dto.CheckedUtc)
            {
                ManifestVerified = dto.ManifestVerified == true,
            };
        }
        catch (Exception ex)
        {
            // A malformed state file must never crash the app: treat it as "no update". The launcher writes it whole
            // (temp file and move, backlog 230), so a half-written one is not expected any more.
            _logger.LogDebug(ex, "Could not read the product update-state file at {Path}.", _path);
            return ProductUpdateState.None;
        }
    }

    // Backlog 230: the launcher's record of checks that could not reach the channel. Display only: it never touches
    // the severity, and an unreadable one is simply not shown.
    private UpdateCheckDto? ReadFailure()
    {
        try
        {
            return File.Exists(_failurePath)
                ? JsonSerializer.Deserialize<UpdateCheckDto>(File.ReadAllText(_failurePath), Json) is { FailingSinceUtc: not null } dto ? dto : null
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the update-check file at {Path}.", _failurePath);
            return null;
        }
    }

    private static ProductUpdateSeverity ParseSeverity(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "critical" => ProductUpdateSeverity.Critical,
        "available" => ProductUpdateSeverity.Available,
        _ => ProductUpdateSeverity.None,
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record UpdateStateDto(
        string? Kind,
        string? CurrentVersion,
        string? TargetVersion,
        string? ReleaseNotesUrl,
        DateTimeOffset? CheckedUtc,
        bool? ManifestVerified);

    private sealed record UpdateCheckDto(DateTimeOffset? FailingSinceUtc, DateTimeOffset? LastTriedUtc, string? Reason, string? Signature);
}
