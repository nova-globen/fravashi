using System.Collections.Concurrent;
using Fravashi.Application.Sessions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Fravashi.Web.Components.Sessions;

/// <summary>
/// The titles a component shows for conversations (work item #351): <see cref="ConversationTitles.Display"/> with the
/// work item each conversation is linked to, read once per conversation and again when its links change. One per
/// component, made from the component's own service provider; every source is optional, so a wiring without the
/// tracker (most component tests) shows the prompt summary instead of failing.
/// </summary>
public sealed class ConversationTitleSource : IDisposable
{
    private readonly ISessionWorkItems? _work;
    private readonly ITrackerResolver? _trackers;

    // Null value: read, and linked to nothing. Absent: not read yet.
    private readonly ConcurrentDictionary<AgentSessionId, LinkedItem?> _linked = new();

    private sealed record LinkedItem(int Number, string Title);

    public ConversationTitleSource(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _work = services.GetService<ISessionWorkItems>();
        _trackers = services.GetService<ITrackerResolver>();
        if (_work is not null)
        {
            _work.Changed += OnLinksChanged;
        }
    }

    /// <summary>Raised (on any thread) when a conversation's links changed, after its cached item was dropped.</summary>
    public event Action<AgentSessionId>? Changed;

    /// <summary>The title to show: the rename, else the linked item, else the summary of the stored derived title.</summary>
    public string Title(AgentSessionId id, string? custom, string? derived) =>
        _linked.TryGetValue(id, out var linked) && linked is not null
            ? ConversationTitles.Display(custom, derived, linked.Number, linked.Title)
            : ConversationTitles.Display(custom, derived);

    /// <summary>Reads the linked work item of every conversation not read yet. True when anything new was learned.</summary>
    public async Task<bool> LoadAsync(IEnumerable<AgentSessionId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (_work is null || _trackers is null)
        {
            return false;
        }

        var learned = false;
        foreach (var id in ids.Distinct().ToList())
        {
            if (_linked.ContainsKey(id))
            {
                continue;
            }

            var item = await FirstLinkedAsync(id);
            _linked[id] = item;
            learned |= item is not null;
        }

        return learned;
    }

    private async Task<LinkedItem?> FirstLinkedAsync(AgentSessionId id)
    {
        foreach (var link in _work!.For(id))
        {
            try
            {
                if (await _trackers!.For(link.Project).GetAsync(link.Item) is { } item)
                {
                    return new LinkedItem(item.Number, item.Title);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                // A title is not worth failing a list over: the prompt summary stands in.
            }
        }

        return null;
    }

    private void OnLinksChanged(AgentSessionId id)
    {
        _linked.TryRemove(id, out _);
        Changed?.Invoke(id);
    }

    public void Dispose()
    {
        if (_work is not null)
        {
            _work.Changed -= OnLinksChanged;
        }
    }
}
