using System.Collections.Concurrent;
using Fravashi.Application.Sessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// The imported conversations for a workspace (backlog 43), parsed once per file VERSION. A listing costs
/// one bridge call; a file is read and parsed again only when its modification time or size moved since the
/// last parse, so an unchanged history is never re-read. There is no timer: the list is asked for when the
/// Sessions area opens, which replaces the archived scanner's five-second loop over every file.
/// A singleton, so the cache outlives any one circuit.
/// </summary>
public sealed class ImportedConversationCatalog
{
    private readonly ConcurrentDictionary<(string WorkspaceId, string Name), ImportedConversation> _cache = new();

    /// <summary>Every conversation file the source lists, newest first, with its parse reused when unchanged.</summary>
    public async Task<IReadOnlyList<ImportedConversation>> ListAsync(
        string workspaceId, ICliConversationSource source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceId);
        ArgumentNullException.ThrowIfNull(source);

        var files = await source.ListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ImportedConversation>(files.Count);
        foreach (var file in files)
        {
            var key = (workspaceId, file.Name);
            if (_cache.TryGetValue(key, out var cached)
                && cached.ModifiedUtc == file.ModifiedUtc && cached.Size == file.Size)
            {
                result.Add(cached);
                continue;
            }

            var imported = await ImportAsync(workspaceId, file, source, cancellationToken).ConfigureAwait(false);
            _cache[key] = imported;
            result.Add(imported);
        }

        // A file that is no longer listed is no longer history; drop its parse rather than keep it forever.
        var listed = files.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _cache.Keys.Where(k => k.WorkspaceId == workspaceId && !listed.Contains(k.Name)).ToList())
        {
            _cache.TryRemove(stale, out _);
        }

        return [.. result.OrderByDescending(c => c.StartedUtc)];
    }

    private static async Task<ImportedConversation> ImportAsync(
        string workspaceId, CliConversationFile file, ICliConversationSource source, CancellationToken cancellationToken)
    {
        var content = await source.ReadAsync(file.Name, cancellationToken).ConfigureAwait(false);
        if (content.Status != CliConversationReadStatus.Read)
        {
            var problem = content.Status == CliConversationReadStatus.TooLarge
                ? ImportedConversationProblem.TooLarge
                : ImportedConversationProblem.Missing;
            return new ImportedConversation(
                workspaceId, file.Name, CliConversationTranscriptParser.UntitledTitle,
                file.ModifiedUtc, file.ModifiedUtc, file.Size, [], problem);
        }

        var parsed = CliConversationTranscriptParser.Parse(content.Bytes, file.ModifiedUtc);
        return new ImportedConversation(
            workspaceId, file.Name, parsed.Title, parsed.StartedUtc, file.ModifiedUtc, file.Size, parsed.Messages);
    }
}
