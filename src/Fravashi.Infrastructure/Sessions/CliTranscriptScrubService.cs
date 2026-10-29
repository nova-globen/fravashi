using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// Backlog 116, the studio-start half: scrubs every agent CLI conversation file under every config home
/// once, before the studio serves anything, so a session that ended without its own scrub (the studio was
/// killed, the container restarted, a build before 116 wrote it) is redacted on the next start. It runs in
/// <see cref="StartAsync"/> on purpose: hosted services start before the server accepts requests, so no
/// session can be running and appending to a file while it is rewritten. A manifest keeps unchanged files
/// from being re-read on every start, so the cost after the first pass is a directory listing.
/// </summary>
public sealed class CliTranscriptScrubService(
    CliTranscriptScrubber scrubber,
    ILogger<CliTranscriptScrubService>? logger = null) : IHostedService
{
    private readonly CliTranscriptScrubber _scrubber = scrubber ?? throw new ArgumentNullException(nameof(scrubber));
    private readonly ILogger _logger = logger ?? NullLogger<CliTranscriptScrubService>.Instance;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _scrubber.ScrubAll();
        }
        catch (Exception ex)
        {
            // A scrub that cannot run must never keep the studio from starting; the next start tries again.
            _logger.LogWarning("The agent CLI transcript scrub at studio start failed: {Error}", ex.GetType().Name);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
