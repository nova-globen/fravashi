using Fravashi.Application.Mirroring;
using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Wiki;

/// <summary>
/// Sends an authored document to the user's own wiki and keeps the two honest with each other (backlog 83,
/// ADR 0018 decisions 2, 5, 7 and 8).
/// <para>
/// This is the first place the product writes to a provider outside an onboarding probe, so it follows the
/// rules the board's outbox was built on rather than softer ones of its own:
/// </para>
/// <list type="number">
/// <item>The row saying a write is in flight is committed BEFORE the call leaves. A crash can then only
/// leave a row that says "we were about to do this", and such a row is never resent: the site is asked
/// first. Mark it after the call and a crash looks exactly like a write that never happened, which is how a
/// second page appears in somebody's wiki.</item>
/// <item>A write carries the whole desired title and body, never a delta, so an update that is known not to
/// have landed is safe to send again. Only a creation can be made twice.</item>
/// <item>A call that never left and a call that left with no answer are different. The first is recorded as
/// not sent; the second is left in flight, which is what it is.</item>
/// <item>Nothing is silently resolved. A page edited on the site is never overwritten by a publish, a page
/// deleted there is never recreated unasked, and taking the site's version keeps the body it replaced.</item>
/// </list>
/// <para>
/// Publishing is an explicit act, never a side effect of saving: a write to a company wiki is outward
/// facing, and the only other writes this product makes to a provider (the access probes) are made on the
/// same condition.
/// </para>
/// </summary>
public sealed class WikiPublisher
{
    // One write at a time, process-wide. The in-flight row already stops a second click from creating a
    // second page, but only after it is committed, and two clicks can both read "no row" before either
    // writes one.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IWikiDocuments _library;
    private readonly IWikiPublicationStore _store;
    private readonly IWikiPageGateway _gateway;
    private readonly IWikiPublishTarget _target;
    private readonly TimeProvider _clock;
    private readonly Func<string> _newId;
    private readonly Fravashi.Application.Projects.IProjectProviders? _choices;
    private readonly Fravashi.Application.Network.ProviderOutages? _outages;

    /// <summary>
    /// The site's outage, or null while it answers (backlog 232): "Confluence unreachable since 14:02 (name not
    /// resolved)". Learned from the calls publishing makes and from the wiki mirror's pull, never from a probe.
    /// </summary>
    public Fravashi.Application.Network.ProviderOutage? Outage => _outages?.For(_gateway.Provider);

    public WikiPublisher(
        IWikiDocuments library,
        IWikiPublicationStore store,
        IWikiPageGateway gateway,
        IWikiPublishTarget target,
        TimeProvider clock,
        Func<string>? newId = null,
        Fravashi.Application.Projects.IProjectProviders? choices = null,
        Fravashi.Application.Network.ProviderOutages? outages = null)
    {
        _choices = choices;
        _outages = outages;
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(gateway);

        // Backlog 232: every answer the site gives (or does not) is reported, so the panel can say since when it has not.
        _gateway = outages is null ? gateway : new ObservedWikiPageGateway(gateway, outages);
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _newId = newId ?? (() => Guid.NewGuid().ToString("n"));
    }

    /// <summary>Whether a wiki that can be written to is configured right now.</summary>
    public bool CanPublish => _gateway.IsConfigured;

    /// <summary>
    /// Whether THIS project publishes: a wiki is configured and the project has not chosen to keep its documents here
    /// only (backlog 142).
    /// </summary>
    public bool CanPublishFor(Fravashi.Domain.Projects.ProjectId project) =>
        _gateway.IsConfigured && !KeepsItsOwn(project);

    private bool KeepsItsOwn(Fravashi.Domain.Projects.ProjectId project) =>
        _choices?.For(project).Wiki == Fravashi.Application.Projects.ProviderChoice.None;

    /// <summary>The provider a publish would go to.</summary>
    public string Provider => _gateway.Provider;

    /// <summary>
    /// What is recorded about a document on the wiki this install publishes to, or null when it was never published
    /// there. A record from another wiki (backlog 145: the project moved) is history, not this wiki's state.
    /// </summary>
    public async Task<WikiPublication?> GetAsync(string itemId, CancellationToken ct = default) =>
        await _store.GetAsync(itemId, ct).ConfigureAwait(false) is { } row && !Elsewhere(row) ? row : null;

    /// <summary>Where the document was published before, on wikis this install no longer publishes to.</summary>
    public async Task<IReadOnlyList<WikiPublication>> ElsewhereAsync(string itemId, CancellationToken ct = default)
    {
        var history = (await _store.HistoryAsync(itemId, ct).ConfigureAwait(false)).ToList();
        if (await _store.GetAsync(itemId, ct).ConfigureAwait(false) is { } row && Elsewhere(row))
        {
            history.Insert(0, row);
        }

        return history;
    }

    private bool Elsewhere(WikiPublication row) => !string.Equals(row.Provider, _gateway.Provider, StringComparison.Ordinal);

    /// <summary>Publishes the document: creates its page, or sends a new version of it.</summary>
    public Task<WikiPublishReport> PublishAsync(string itemId, CancellationToken ct = default) =>
        GatedAsync(itemId, async (item, publication) =>
        {
            // A record from the wiki the project published to before is kept as history, and this publish is a
            // creation on the wiki it publishes to now (backlog 145). Only a publish does this, because it is the act
            // a person chose; the other acts refuse a record that is not this wiki's.
            if (publication is not null && Elsewhere(publication))
            {
                await _store.ArchiveAsync(publication, _clock.GetUtcNow(), ct).ConfigureAwait(false);
                publication = null;
            }

            var step = WikiPublishDiscipline.Next(publication, item);
            if (step == WikiPublishStep.SettleFirst)
            {
                publication = await SettleAsync(publication!, item, ct).ConfigureAwait(false);
                step = WikiPublishDiscipline.Next(publication, item);
            }

            return step switch
            {
                WikiPublishStep.Create => await CreateAsync(item, publication, ct).ConfigureAwait(false),
                WikiPublishStep.Update => await UpdateAsync(item, publication!, publication!.SiteVersion, ct).ConfigureAwait(false),
                WikiPublishStep.NothingToSend => new WikiPublishReport(publication, [], null),
                _ => WikiPublishReport.Refused(publication, WikiPublishDiscipline.Sentence(publication, item)),
            };
        }, ct, publishing: true);

    /// <summary>
    /// Publishes every document given, parents before their children so each page lands under its parent's (backlog
    /// 49: a project's whole wiki, sent in one act). Each document goes through <see cref="PublishAsync"/>, so every
    /// rule of a single publish holds for each, and one that is refused is reported and does not stop the rest; a child
    /// of a refused parent is then refused in its turn, with the reason saying so.
    /// </summary>
    public async Task<WikiPublishAllReport> PublishAllAsync(
        IReadOnlyList<WikiItem> documents, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var byId = documents.ToDictionary(d => d.Id, StringComparer.Ordinal);
        int Depth(WikiItem item)
        {
            var depth = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var at = item.ParentId; at is not null && seen.Add(at) && byId.TryGetValue(at, out var parent); at = parent.ParentId)
            {
                depth++;
            }

            return depth;
        }

        var ordered = documents.OrderBy(Depth).ThenBy(d => d.Number).ToList();
        int created = 0, updated = 0, unchanged = 0;
        var refused = new List<string>();
        for (var i = 0; i < ordered.Count; i++)
        {
            progress?.Invoke(i, ordered.Count);
            var item = ordered[i];
            var before = await _store.GetAsync(item.Id, ct).ConfigureAwait(false);
            var report = await PublishAsync(item.Id, ct).ConfigureAwait(false);
            var after = report.Publication;
            if (report.Refusal is { } why || after?.State != WikiPublicationState.Published)
            {
                refused.Add($"@{item.Number} {item.Title}: {report.Refusal ?? after?.Detail ?? "it was not published."}");
            }
            else if (before?.State != WikiPublicationState.Published || before.PageId != after.PageId)
            {
                created++;
            }
            else if (before.SiteVersion != after.SiteVersion)
            {
                updated++;
            }
            else
            {
                unchanged++;
            }
        }

        progress?.Invoke(ordered.Count, ordered.Count);
        return new WikiPublishAllReport(created, updated, unchanged, refused);
    }

    /// <summary>
    /// Asks the site where the page stands: settles a write left in flight, and notices a page that was
    /// edited or deleted there. It never changes the document and never writes to the site.
    /// </summary>
    public Task<WikiPublishReport> CheckAsync(string itemId, CancellationToken ct = default) =>
        GatedAsync(itemId, async (item, publication) =>
        {
            if (publication is null)
            {
                return new WikiPublishReport(null, [], null);
            }

            if (publication.State == WikiPublicationState.InFlight)
            {
                return new WikiPublishReport(await SettleAsync(publication, item, ct).ConfigureAwait(false), [], null);
            }

            if (publication.State != WikiPublicationState.Published || publication.PageId is null)
            {
                return new WikiPublishReport(publication, [], null);
            }

            var page = await _gateway.GetAsync(publication.PageId, ct).ConfigureAwait(false);
            if (page.Outcome == ProviderOutcome.Gone)
            {
                return new WikiPublishReport(
                    await SaveAsync(publication with { State = WikiPublicationState.Gone, Detail = page.Reason }, ct).ConfigureAwait(false),
                    [],
                    null);
            }

            if (!page.IsOk)
            {
                // Not knowing is not a change of state. What is recorded stands, and the reason is said.
                return WikiPublishReport.Refused(publication, page.Reason ?? "The site could not be asked.");
            }

            if (page.Value!.Version == publication.SiteVersion)
            {
                return new WikiPublishReport(publication, [], null);
            }

            var moved = publication with
            {
                State = publication.LocalMovedSince(item) ? WikiPublicationState.Conflict : WikiPublicationState.SiteChanged,
                WebUrl = page.Value.WebUrl ?? publication.WebUrl,
                // Backlog 149: a number is quoted only where the site itself shows one; an Azure DevOps "version"
                // is a hashed ETag and would read as noise. Where there is no number there is no detail at all:
                // the state's own sentence already says the page changed, and repeating it was seen in the window.
                Detail = _gateway.VersionsAreReadable
                    ? $"The site has version {page.Value.Version}; the last publish made version {publication.SiteVersion}."
                    : null,
            };
            return new WikiPublishReport(await SaveAsync(moved, ct).ConfigureAwait(false), [], null);
        }, ct);

    /// <summary>
    /// Takes the site's version of the page over the document here, which is what ADR 0018 decision 2 means
    /// by site-authoritative. It is a person's act and not an automatic one because the conversion can lose
    /// things: the losses are reported, and the body that was replaced is kept.
    /// </summary>
    public Task<WikiPublishReport> TakeSiteVersionAsync(string itemId, CancellationToken ct = default) =>
        GatedAsync(itemId, async (item, publication) =>
        {
            if (publication?.PageId is null || !IsAPersonsChoice(publication.State))
            {
                return WikiPublishReport.Refused(publication, "There is no changed page on the site to take.");
            }

            var page = await _gateway.GetAsync(publication.PageId, ct).ConfigureAwait(false);
            if (page.Outcome == ProviderOutcome.Gone)
            {
                return new WikiPublishReport(
                    await SaveAsync(publication with { State = WikiPublicationState.Gone, Detail = page.Reason }, ct).ConfigureAwait(false),
                    [],
                    null);
            }

            if (!page.IsOk)
            {
                return WikiPublishReport.Refused(publication, page.Reason ?? "The site could not be read.");
            }

            var conversion = _gateway.FromSite(page.Value!.Body);
            var updated = await _library.UpdateAsync(
                item.Id,
                new WikiItemEdit { Title = page.Value.Title, Body = conversion.Text },
                item.Version,
                ct).ConfigureAwait(false);

            if (!updated.IsValid)
            {
                return WikiPublishReport.Refused(publication, string.Join(" ", updated.Errors.Select(e => e.Message)));
            }

            var taken = publication with
            {
                State = WikiPublicationState.Published,
                SiteVersion = page.Value.Version,
                PushedItemVersion = updated.Value.Version,
                WebUrl = page.Value.WebUrl ?? publication.WebUrl,
                ReplacedBody = item.Body,
                Detail = null,
            };
            return new WikiPublishReport(await SaveAsync(taken, ct).ConfigureAwait(false), conversion.Losses, null);
        }, ct);

    /// <summary>
    /// Sends the document here over whatever the site now holds. Only ever a person's explicit choice, and
    /// the site keeps its own page history, so the version it replaces is still there.
    /// </summary>
    public Task<WikiPublishReport> OverwriteSiteAsync(string itemId, CancellationToken ct = default) =>
        GatedAsync(itemId, async (item, publication) =>
        {
            if (publication?.PageId is null || !IsAPersonsChoice(publication.State))
            {
                return WikiPublishReport.Refused(publication, "There is no changed page on the site to overwrite.");
            }

            var page = await _gateway.GetAsync(publication.PageId, ct).ConfigureAwait(false);
            if (page.Outcome == ProviderOutcome.Gone)
            {
                return new WikiPublishReport(
                    await SaveAsync(publication with { State = WikiPublicationState.Gone, Detail = page.Reason }, ct).ConfigureAwait(false),
                    [],
                    null);
            }

            if (!page.IsOk)
            {
                return WikiPublishReport.Refused(publication, page.Reason ?? "The site could not be read.");
            }

            return await UpdateAsync(item, publication, page.Value!.Version, ct).ConfigureAwait(false);
        }, ct);

    /// <summary>Publishes the document as a NEW page after its old one was deleted on the site.</summary>
    public Task<WikiPublishReport> PublishAsNewAsync(string itemId, CancellationToken ct = default) =>
        GatedAsync(itemId, async (item, publication) =>
        {
            if (publication?.State != WikiPublicationState.Gone)
            {
                return WikiPublishReport.Refused(publication, "This only applies to a page that is gone from the site.");
            }

            return await CreateAsync(item, publication with { PageId = null, SiteVersion = null, WebUrl = null }, ct)
                .ConfigureAwait(false);
        }, ct);

    /// <summary>Puts back the body that taking the site's version replaced.</summary>
    public Task<WikiPublishReport> RestoreReplacedAsync(string itemId, CancellationToken ct = default) =>
        GatedAsync(itemId, async (item, publication) =>
        {
            if (publication?.ReplacedBody is not { } body)
            {
                return WikiPublishReport.Refused(publication, "There is no replaced body to put back.");
            }

            var restored = await _library.UpdateAsync(item.Id, new WikiItemEdit { Body = body }, item.Version, ct)
                .ConfigureAwait(false);
            if (!restored.IsValid)
            {
                return WikiPublishReport.Refused(publication, string.Join(" ", restored.Errors.Select(e => e.Message)));
            }

            // The page still holds the site's version, so the document now reads as changed here since,
            // and sending it is an ordinary publish somebody asks for.
            return new WikiPublishReport(await SaveAsync(publication with { ReplacedBody = null }, ct).ConfigureAwait(false), [], null);
        }, ct);

    private static bool IsAPersonsChoice(WikiPublicationState state) =>
        state is WikiPublicationState.SiteChanged or WikiPublicationState.Conflict or WikiPublicationState.Unresolved;

    private async Task<WikiPublishReport> CreateAsync(WikiItem item, WikiPublication? previous, CancellationToken ct)
    {
        var space = previous?.Space is { Length: > 0 } known ? known : _target.Space ?? _gateway.DefaultSpace;
        if (string.IsNullOrWhiteSpace(space))
        {
            return WikiPublishReport.Refused(
                previous,
                "No space is named for published pages. Name one on the wiki connection (Settings, Project, Wiki and publishing) and check its access.");
        }

        // A document that sits under another goes under that document's page, so the site's tree is the wiki's. A
        // parent that is not on the site yet is a refusal rather than a page at the top: this product cannot move a
        // page afterwards, so a page put in the wrong place would stay there.
        string? parentPageId = null;
        if (item.ParentId is { } parentId)
        {
            var parent = await _library.GetAsync(parentId, ct).ConfigureAwait(false);
            var parentPage = await _store.GetAsync(parentId, ct).ConfigureAwait(false);
            if (parent is not null)
            {
                if (parentPage is not { State: WikiPublicationState.Published, PageId: { } pageId }
                    || !string.Equals(parentPage.Space, space.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return WikiPublishReport.Refused(
                        previous,
                        $"This document sits under '{parent.Title}', which is not published to this space yet. Publish that "
                        + "one first (Publish everything does them in order), so this page lands under it.");
                }

                parentPageId = pageId;
            }
        }

        var conversion = _gateway.ToSite(item.Body);
        var inFlight = await SaveAsync(
            new WikiPublication(
                item.Id, _gateway.Provider, space.Trim(), null, null, previous?.PushedItemVersion,
                WikiPublicationState.InFlight, _newId(), item.Title, null, null, previous?.ReplacedBody, _clock.GetUtcNow()),
            ct).ConfigureAwait(false);

        // Committed above. Everything in this class depends on that having happened first.
        var result = await _gateway.CreateAsync(inFlight.Space, item.Title, conversion.Text, parentPageId, ct).ConfigureAwait(false);
        return new WikiPublishReport(await ApplyAsync(inFlight, item, result, ct).ConfigureAwait(false), conversion.Losses, null);
    }

    private async Task<WikiPublishReport> UpdateAsync(WikiItem item, WikiPublication publication, int? siteVersion, CancellationToken ct)
    {
        if (siteVersion is not { } current)
        {
            var page = await _gateway.GetAsync(publication.PageId!, ct).ConfigureAwait(false);
            if (!page.IsOk)
            {
                return WikiPublishReport.Refused(publication, page.Reason ?? "The site could not be read.");
            }

            current = page.Value!.Version;
        }

        var conversion = _gateway.ToSite(item.Body);
        var inFlight = await SaveAsync(
            publication with
            {
                State = WikiPublicationState.InFlight,
                SiteVersion = current,
                OperationId = _newId(),
                SentTitle = item.Title,
                Detail = null,
            },
            ct).ConfigureAwait(false);

        // Committed above, as for a creation.
        var result = await _gateway.UpdateAsync(inFlight.PageId!, item.Title, conversion.Text, current + 1, ct).ConfigureAwait(false);
        return new WikiPublishReport(await ApplyAsync(inFlight, item, result, ct).ConfigureAwait(false), conversion.Losses, null);
    }

    private Task<WikiPublication> ApplyAsync(
        WikiPublication inFlight, WikiItem item, ProviderResult<SitePage> result, CancellationToken ct)
    {
        var next = result.Outcome switch
        {
            ProviderOutcome.Ok => inFlight with
            {
                State = WikiPublicationState.Published,
                PageId = result.Value!.Id,
                SiteVersion = result.Value.Version,
                PushedItemVersion = item.Version,
                WebUrl = result.Value.WebUrl ?? inFlight.WebUrl,
                Detail = null,
            },

            // Nothing happened there, so this is safe to send again and says so.
            ProviderOutcome.Unreachable => inFlight with { State = WikiPublicationState.NotSent, Detail = result.Reason },

            // Whether the site acted is unknown. That is exactly what the in-flight row already says, so it
            // stays, and the next look asks the site instead of sending again.
            ProviderOutcome.Indeterminate => inFlight with { Detail = result.Reason },

            // For an update the site answered that somebody got there first: both sides have moved.
            ProviderOutcome.Conflict when inFlight.PageId is not null
                => inFlight with { State = WikiPublicationState.Conflict, Detail = result.Reason },

            ProviderOutcome.Gone when inFlight.PageId is not null
                => inFlight with { State = WikiPublicationState.Gone, Detail = result.Reason },

            _ => inFlight with { State = WikiPublicationState.Refused, Detail = result.Reason },
        };

        return SaveAsync(next, ct);
    }

    /// <summary>
    /// Works out what happened to a write left in flight by ASKING, never by sending it again. It ends in
    /// not-sent only on the site's own word that nothing landed, in published only when the page provably
    /// holds what this document holds, and otherwise with a person.
    /// </summary>
    private async Task<WikiPublication> SettleAsync(WikiPublication inFlight, WikiItem item, CancellationToken ct)
    {
        var expected = _gateway.ToSite(item.Body).Text;

        if (inFlight.PageId is null)
        {
            var title = inFlight.SentTitle ?? item.Title;
            var found = await _gateway.FindByTitleAsync(inFlight.Space, title, ct).ConfigureAwait(false);
            if (!found.IsOk)
            {
                return await SaveAsync(inFlight with { Detail = found.Reason }, ct).ConfigureAwait(false);
            }

            if (found.Value is not { } page)
            {
                return await SaveAsync(
                    inFlight with
                    {
                        State = WikiPublicationState.NotSent,
                        Detail = "The site has no page with that title, so the publish never landed.",
                    },
                    ct).ConfigureAwait(false);
            }

            return await SaveAsync(Judge(inFlight, item, page, expected, created: true), ct).ConfigureAwait(false);
        }

        var current = await _gateway.GetAsync(inFlight.PageId, ct).ConfigureAwait(false);
        if (current.Outcome == ProviderOutcome.Gone)
        {
            return await SaveAsync(inFlight with { State = WikiPublicationState.Gone, Detail = current.Reason }, ct).ConfigureAwait(false);
        }

        if (!current.IsOk)
        {
            return await SaveAsync(inFlight with { Detail = current.Reason }, ct).ConfigureAwait(false);
        }

        if (current.Value!.Version == inFlight.SiteVersion)
        {
            return await SaveAsync(
                inFlight with
                {
                    State = WikiPublicationState.NotSent,
                    Detail = "The page is still at the version it had before, so the publish never landed.",
                },
                ct).ConfigureAwait(false);
        }

        return await SaveAsync(Judge(inFlight, item, current.Value, expected, created: false), ct).ConfigureAwait(false);
    }

    private static WikiPublication Judge(WikiPublication inFlight, WikiItem item, SitePage page, string expected, bool created)
    {
        if (string.Equals(page.Title, item.Title, StringComparison.Ordinal) && SameText(page.Body, expected))
        {
            return inFlight with
            {
                State = WikiPublicationState.Published,
                PageId = page.Id,
                SiteVersion = page.Version,
                PushedItemVersion = item.Version,
                WebUrl = page.WebUrl ?? inFlight.WebUrl,
                Detail = null,
            };
        }

        // A page is there and it cannot be shown to be this write's. A site may rewrite markup on its way
        // in, so "not provably ours" is common and is not an accusation; it is simply a person's question.
        return inFlight with
        {
            State = WikiPublicationState.Unresolved,
            PageId = page.Id,
            SiteVersion = page.Version,
            WebUrl = page.WebUrl ?? inFlight.WebUrl,
            Detail = created
                ? $"A page titled '{page.Title}' is on the site and its content is not provably what this document holds. If this publish created it, choose which side stands."
                : $"The page moved to version {page.Version} and its content is not provably what this document holds. Choose which side stands.",
        };
    }

    private static bool SameText(string a, string b) => Squash(a) == Squash(b);

    private static string Squash(string text) =>
        string.Concat((text ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));

    private async Task<WikiPublication> SaveAsync(WikiPublication publication, CancellationToken ct)
    {
        var stamped = publication with { UpdatedUtc = _clock.GetUtcNow() };
        await _store.SaveAsync(stamped, ct).ConfigureAwait(false);
        return stamped;
    }

    private async Task<WikiPublishReport> GatedAsync(
        string itemId, Func<WikiItem, WikiPublication?, Task<WikiPublishReport>> act, CancellationToken ct, bool publishing = false)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var item = await _library.GetAsync(itemId, ct).ConfigureAwait(false);
            if (item is null)
            {
                return WikiPublishReport.Refused(null, "There is no such document.");
            }

            var publication = await _store.GetAsync(itemId, ct).ConfigureAwait(false);
            if (!_gateway.IsConfigured)
            {
                return WikiPublishReport.Refused(
                    publication,
                    "No wiki that can be written to is connected. Publishing needs a Confluence connection.");
            }

            if (await _library.ProjectOfAsync(itemId, ct).ConfigureAwait(false) is { } project && KeepsItsOwn(project))
            {
                return WikiPublishReport.Refused(
                    publication,
                    "This project keeps its documents here and publishes to no wiki (Settings, Project, Project and variables).");
            }

            if (publication is not null && Elsewhere(publication) && !publishing)
            {
                return WikiPublishReport.Refused(
                    null,
                    $"This document was published to {publication.Provider}, and this install now publishes to {_gateway.Provider}. "
                    + "Publish sends it there; the page on the old wiki is kept as history and is not changed.");
            }

            return await act(item, publication).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }
}
