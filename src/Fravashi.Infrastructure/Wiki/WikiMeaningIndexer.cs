using Fravashi.Application.Wiki;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// Keeps the index by meaning up with the wiki in the background (backlog 40), so a search almost never has
/// anything to catch up on. A search does its own small catch-up as well, which is what makes a document
/// written a second ago findable; this is what makes a wiki of thousands, restored from a backup or met by
/// a new model, searchable without anyone waiting for it.
/// <para>
/// It polls rather than being told, on purpose: documents are written by the page, by four agent tools and
/// by a restore, and a poll of one indexed query every half minute is cheaper and harder to forget than a
/// notification every one of those has to remember to send.
/// </para>
/// </summary>
public sealed class WikiMeaningIndexer(IWikiMeaningSearch index, ILogger<WikiMeaningIndexer> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the startup path: loading the model takes a moment and nothing waits on it.
        await Task.Yield();

        if (!index.IsAvailable)
        {
            return; // said once, by the embedder, when it failed to load
        }

        // Said once, because "the native runtime loaded in THIS image" is exactly the thing that works on a
        // build host and fails in the shipped container, and the log is where that is looked for.
        logger.LogInformation("The wiki can be searched by meaning: the embedding model loaded.");

        var announced = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var waiting = await index.CatchUpAsync(Budget, stoppingToken).ConfigureAwait(false);
                if (waiting > 0 && !announced)
                {
                    logger.LogInformation("Indexing the wiki by meaning: {Waiting} document(s) still waiting.", waiting);
                    announced = true;
                }
                else if (waiting == 0)
                {
                    announced = false;
                }

                // While there is a backlog, keep going; otherwise look again in a while.
                if (waiting == 0)
                {
                    await Task.Delay(Every, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Derived data: a failed pass loses nothing and the next one tries again.
                logger.LogWarning(ex, "A pass of the wiki meaning index failed and will be tried again.");
                await Task.Delay(Every, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
