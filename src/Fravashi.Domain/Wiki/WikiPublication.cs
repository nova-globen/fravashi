namespace Fravashi.Domain.Wiki;

/// <summary>
/// Where one authored document stands against the page it was published as (backlog 83).
/// <para>
/// The split that matters is the one the board's outbox already makes: a write that is ABOUT to leave, a
/// write the provider answered, and a write nobody can account for are three states of knowledge, and
/// folding the last into either of the others is what makes a second page appear in somebody's wiki.
/// </para>
/// </summary>
public enum WikiPublicationState
{
    /// <summary>
    /// A write was recorded and committed and the call was about to leave. A row still saying this when
    /// nothing is running is what a crash, or a call that left and never answered, leaves behind. It is
    /// never sent again blindly: the site is asked first.
    /// </summary>
    InFlight,

    /// <summary>The site answered and the page holds what was sent.</summary>
    Published,

    /// <summary>
    /// The call provably never left (no route, no credential). Nothing happened on the site, so sending it
    /// again is safe, and the document simply is not published yet.
    /// </summary>
    NotSent,

    /// <summary>
    /// A write left and no answer came back, and asking the site did not settle it. A person decides.
    /// </summary>
    Unresolved,

    /// <summary>The page changed on the site after the last publish, and the document here did not.</summary>
    SiteChanged,

    /// <summary>Both sides changed since the last publish. Neither is overwritten until a person chooses.</summary>
    Conflict,

    /// <summary>The site answered and refused. Sending the same thing again will be refused again.</summary>
    Refused,

    /// <summary>
    /// The page is no longer on the site. The document here is kept, the fact is reported, and nothing is
    /// recreated until a person asks for it, which is the tombstone rule of ADR 0018 decision 5.
    /// </summary>
    Gone,
}

/// <summary>The provider facts kept BESIDE an authored document, never on it (the document has no provider).</summary>
/// <param name="ItemId">The authored document's id.</param>
/// <param name="Provider">The wiki provider's name, as the connection says it.</param>
/// <param name="Space">The space the page lives in, as the user named it.</param>
/// <param name="PageId">The page's id on the site, or null until a creation is answered.</param>
/// <param name="SiteVersion">The site's version number of the page as last seen, or null.</param>
/// <param name="PushedItemVersion">The document version whose content the page holds, or null when unknown.</param>
/// <param name="State">Where it stands.</param>
/// <param name="OperationId">The client-generated id of the write last recorded.</param>
/// <param name="SentTitle">The title that write carried, which is what a lost creation is looked up by.</param>
/// <param name="Detail">Why, in words, for any state a person has to read.</param>
/// <param name="WebUrl">The page's address for a person, when the site gave one.</param>
/// <param name="ReplacedBody">
/// The document's body as it stood before the site's version was taken over it, kept so that taking the
/// site's version is reversible in one step rather than a quiet loss. Null when nothing was replaced.
/// </param>
/// <param name="UpdatedUtc">When this row last changed.</param>
public sealed record WikiPublication(
    string ItemId,
    string Provider,
    string Space,
    string? PageId,
    int? SiteVersion,
    long? PushedItemVersion,
    WikiPublicationState State,
    string OperationId,
    string? SentTitle,
    string? Detail,
    string? WebUrl,
    string? ReplacedBody,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>True when the document here has moved on from what the page holds.</summary>
    public bool LocalMovedSince(WikiItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return PushedItemVersion is not { } pushed || item.Version != pushed;
    }
}

/// <summary>What a publish may do next. Decided here, purely, so the rule is testable without a site.</summary>
public enum WikiPublishStep
{
    /// <summary>Create the page.</summary>
    Create,

    /// <summary>Send a new version of the page.</summary>
    Update,

    /// <summary>The page already holds this version of the document.</summary>
    NothingToSend,

    /// <summary>A write is unaccounted for. Ask the site what happened before sending anything.</summary>
    SettleFirst,

    /// <summary>A person has to choose. Publishing is not the way out of this state.</summary>
    NeedsAPerson,
}

/// <summary>The rules a publish follows, kept apart from the machinery that follows them.</summary>
public static class WikiPublishDiscipline
{
    /// <summary>What an ordinary publish of <paramref name="item"/> should do, given what is recorded.</summary>
    public static WikiPublishStep Next(WikiPublication? publication, WikiItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (publication is null)
        {
            return WikiPublishStep.Create;
        }

        return publication.State switch
        {
            // Whatever was in flight is unaccounted for. For a creation, sending again is how a second
            // page appears; for an update it is how somebody's edit on the site is overwritten unseen.
            WikiPublicationState.InFlight => WikiPublishStep.SettleFirst,

            // The site moved, or could not say what happened. Publishing over it would silently resolve a
            // question that is a person's.
            WikiPublicationState.Unresolved or WikiPublicationState.SiteChanged
                or WikiPublicationState.Conflict or WikiPublicationState.Gone => WikiPublishStep.NeedsAPerson,

            // Never left, or was refused: the document may have been corrected since, so sending is the
            // point. Which write depends only on whether a page is known.
            WikiPublicationState.NotSent or WikiPublicationState.Refused
                => publication.PageId is null ? WikiPublishStep.Create : WikiPublishStep.Update,

            WikiPublicationState.Published when publication.PageId is null => WikiPublishStep.Create,
            WikiPublicationState.Published => publication.LocalMovedSince(item)
                ? WikiPublishStep.Update
                : WikiPublishStep.NothingToSend,

            _ => WikiPublishStep.NeedsAPerson,
        };
    }

    /// <summary>
    /// The detail to show after <see cref="Sentence"/>, or null when there is none or it only repeats it: a record
    /// stored by a build before backlog 149's fix carries a detail that IS the sentence, and the panel said it twice.
    /// </summary>
    public static string? DetailToShow(WikiPublication? publication, WikiItem item)
    {
        if (publication?.Detail is not { Length: > 0 } detail)
        {
            return null;
        }

        return Sentence(publication, item).Contains(detail.Trim(), StringComparison.Ordinal) ? null : detail;
    }

    /// <summary>The one sentence a person reads for a state, so every surface says the same thing.</summary>
    public static string Sentence(WikiPublication? publication, WikiItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (publication is null)
        {
            return $"Not published. This document exists only in {ProductName.Short}.";
        }

        return publication.State switch
        {
            WikiPublicationState.Published => publication.LocalMovedSince(item)
                ? "Published, and changed here since. The page on the site holds an older version."
                : "Published. The page on the site holds this version.",
            WikiPublicationState.InFlight =>
                "A publish was started and never finished. The site is asked what happened before anything is sent again.",
            WikiPublicationState.NotSent =>
                "Not sent: the site could not be reached, so nothing happened there. Publishing again is safe.",
            WikiPublicationState.Unresolved =>
                "A publish left and no answer came back, and the site could not say what happened. It is not sent again until that is settled.",
            WikiPublicationState.SiteChanged =>
                "The page was changed on the site after the last publish. This document was not changed here.",
            WikiPublicationState.Conflict =>
                "Changed on both sides since the last publish. Nothing is overwritten until one side is chosen.",
            WikiPublicationState.Refused => "The site refused the publish.",
            WikiPublicationState.Gone =>
                "The page is no longer on the site. The document here is kept, and nothing is recreated unasked.",
            _ => "Unknown publication state.",
        };
    }
}
