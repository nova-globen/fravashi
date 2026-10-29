using System.Text.Json;
using Fravashi.Application.Sandbox;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Sandbox;

/// <summary>
/// Reads the container-hardening posture the <c>fravashi</c> launcher writes into the mounted data directory
/// (<c>sandbox-posture.json</c>) - the app side of the F4 / ADR 0007 contract. The launcher is the only actor
/// that runs the container and knows which flags the engine accepted, so it owns the write; the app merely reads
/// and displays it, never re-deriving or overstating what is enforced. A missing file means the app is running
/// outside a launcher-managed container (host dev), reported honestly as
/// <see cref="SandboxPosture.OutsideContainer"/>. Cached with a short TTL so a per-request read is cheap while
/// still reflecting a fresh <c>fravashi</c> run within seconds.
/// </summary>
public sealed class FileSandboxPosture : ISandboxPosture
{
    /// <summary>The file the launcher writes into the data directory.</summary>
    public const string FileName = "sandbox-posture.json";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly ILogger<FileSandboxPosture> _logger;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private SandboxPosture _cached = SandboxPosture.OutsideContainer;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public FileSandboxPosture(DataDirectory dataDirectory, ILogger<FileSandboxPosture> logger, TimeProvider? clock = null)
    {
        _path = Path.Combine(dataDirectory.Path, FileName);
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public SandboxPosture Current
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

    private SandboxPosture Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return SandboxPosture.OutsideContainer;
            }

            var dto = JsonSerializer.Deserialize<PostureDto>(File.ReadAllText(_path), Json);
            if (dto is null || string.IsNullOrWhiteSpace(dto.Runtime))
            {
                return SandboxPosture.OutsideContainer;
            }

            var controls = (dto.Controls ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.Id))
                .Select(c => new SandboxControl(
                    c.Id!,
                    c.Title ?? c.Id!,
                    c.Description ?? string.Empty,
                    ParseStatus(c.Status),
                    Clean(c.Detail)))
                .ToList();

            return new SandboxPosture(
                dto.Runtime!.Trim(),
                controls,
                dto.StrongerRuntimeRecommended,
                Clean(dto.Recommendation),
                dto.CheckedUtc);
        }
        catch (Exception ex)
        {
            // A malformed / half-written posture file must never crash the app: fall back to the honest default.
            _logger.LogDebug(ex, "Could not read the sandbox posture file at {Path}.", _path);
            return SandboxPosture.OutsideContainer;
        }
    }

    private static SandboxProtectionStatus ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "active" => SandboxProtectionStatus.Active,
        _ => SandboxProtectionStatus.Unavailable,
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PostureDto(
        string? Runtime,
        bool StrongerRuntimeRecommended,
        string? Recommendation,
        DateTimeOffset? CheckedUtc,
        IReadOnlyList<ControlDto>? Controls);

    private sealed record ControlDto(string? Id, string? Title, string? Description, string? Status, string? Detail);
}
