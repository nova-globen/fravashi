using Fravashi.Application.Persistence;
using Fravashi.Application.Prototypes;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Application.Demo;

/// <summary>
/// The demo data set (backlog 71), written through the REAL stores into an empty install.
/// <para>
/// The shape of this is the item's whole design decision, argued in <c>docs/plans/demo-mode.md</c>: a demo
/// install is an ordinary install whose data is seeded and whose OUTWARD seams are simply not configured,
/// never a mode that every seam checks. The acceptance says it "cannot write to a real workspace, tracker or
/// wiki", and "cannot" is a containment word, which this repository has learned five times over not to make
/// a convention. Here it is not a check anybody has to remember: with no provider configured
/// <c>IProviderGateways.For</c> answers null, with no publish target the wiki publisher refuses, with no
/// registered workspace the bridge has nothing to reach, and with no credential no agent runtime starts.
/// Each of those is a path the product already has, already tests, and already explains on screen.
/// </para>
/// <para>
/// So this class writes data and nothing else. It holds no flag, refuses nothing, and has no opinion about
/// what the app may do afterwards. Everything goes through the same interfaces a user's own actions go
/// through, never SQL, so a schema change cannot leave the seed behind reporting success.
/// </para>
/// </summary>
public sealed class DemoSeed(
    ITrackerResolver trackers,
    IWikiLibraries wikis,
    IOnboardingState onboarding,
    TimeProvider clock)
{
    /// <summary>
    /// What a seeded install is stamped as having been made at. Fixed, and in the PAST relative to the demo's
    /// own clock, so that every relative time a surface renders ("3 hours ago") is a constant rather than a
    /// number that grows while the demo sits open.
    /// </summary>
    public static readonly DateTimeOffset Epoch = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Writes the demo graph into <paramref name="project"/>. Refuses rather than adding to an install that
    /// already holds work items: seeding twice would double every board column, and a demo whose data depends
    /// on how many times it was started is not deterministic, which is the acceptance's second promise.
    /// </summary>
    public async Task<DemoSeedResult> SeedAsync(ProjectId project, CancellationToken ct = default)
    {
        var tracker = trackers.For(project);
        var wiki = wikis.For(project);

        var existing = await tracker.QueryAsync(new WorkItemQuery { Limit = 1 }, ct).ConfigureAwait(false);
        if (existing.Items.Count > 0)
        {
            return DemoSeedResult.AlreadyPopulated;
        }

        var items = await SeedWorkItemsAsync(tracker, ct).ConfigureAwait(false);
        var documents = await SeedWikiAsync(wiki, items, ct).ConfigureAwait(false);

        // The only gate between a fresh install and the studio. Marked last, so a seed that failed part way
        // leaves an install that still goes to onboarding rather than one that opens on half a demo.
        if (!onboarding.IsComplete)
        {
            onboarding.MarkComplete();
        }

        return new DemoSeedResult(true, items.Count, documents);
    }

    /// <summary>
    /// One item in every status, at three levels, with a parent chain and a real dependency edge, because the
    /// board is the surface this exists to photograph and an empty column looks like a defect.
    /// </summary>
    private static async Task<IReadOnlyList<WorkItem>> SeedWorkItemsAsync(ITracker tracker, CancellationToken ct)
    {
        var created = new List<WorkItem>();

        var tier = await CreateAsync(tracker, created, new NewWorkItem
        {
            Level = WorkItemLevel.Tier,
            Title = "Customer onboarding",
            Description = "Everything a new customer touches between signing up and their first successful import.",
            Status = WorkItemStatus.InProgress,
        }, ct).ConfigureAwait(false);

        var feature = await CreateAsync(tracker, created, new NewWorkItem
        {
            Level = WorkItemLevel.Feature,
            Title = "Import a customer's existing data",
            Description = "Take a CSV export from the previous system and land it without anybody retyping it.",
            Parent = tier?.Id,
            Status = WorkItemStatus.InProgress,
        }, ct).ConfigureAwait(false);

        // One per status, so no column is empty. Ready ones carry acceptance criteria, because a Ready item
        // with no done-test is exactly what the board is meant to make visible.
        var slices = new (string Title, string Description, WorkItemStatus Status, WorkItemKind Kind, string[] Acceptance)[]
        {
            ("Accept a CSV with a byte order mark",
             "Excel writes one and the parser reads it as part of the first column name.",
             WorkItemStatus.Triage, WorkItemKind.Bug, []),
            ("Map columns the customer has renamed",
             "The importer should offer a mapping step rather than refusing a header it does not recognise.",
             WorkItemStatus.Backlog, WorkItemKind.Standard, []),
            ("Report a bad row without losing the good ones",
             "A single malformed row currently fails the whole import.",
             WorkItemStatus.Ready, WorkItemKind.Standard,
             ["A file with one bad row imports every other row.", "The bad row is reported with its line number."]),
            ("Stream a large file instead of buffering it",
             "A 200 MB export takes the process over its memory ceiling.",
             WorkItemStatus.InProgress, WorkItemKind.Standard,
             ["A 200 MB file imports without the working set exceeding 300 MB."]),
            ("Measure how long a realistic import takes",
             "Nobody knows whether the progress bar needs to be honest about minutes or seconds.",
             WorkItemStatus.InReview, WorkItemKind.Spike, []),
            ("Remember the last mapping a customer used",
             "Most customers import the same shape every month.",
             WorkItemStatus.Done, WorkItemKind.Standard,
             ["The second import of the same file offers the previous mapping."]),
            ("Import directly from the old vendor's API",
             "Dropped: the vendor withdrew the API before anybody started.",
             WorkItemStatus.Cancelled, WorkItemKind.Standard, []),
        };

        foreach (var slice in slices)
        {
            await CreateAsync(tracker, created, new NewWorkItem
            {
                Level = WorkItemLevel.Increment,
                Kind = slice.Kind,
                Title = slice.Title,
                Description = slice.Description,
                Parent = feature?.Id,
                Status = slice.Status,
                Resolution = slice.Status switch
                {
                    WorkItemStatus.Done => WorkItemResolution.Completed,
                    WorkItemStatus.Cancelled => WorkItemResolution.WontDo,
                    _ => null,
                },
                // The id is the store's to mint, which is what lets a caller author acceptance without a
                // second round trip; an empty string is how NewWorkItem says "you name it".
                AcceptanceCriteria = [.. slice.Acceptance.Select(text => new AcceptanceCriterion(string.Empty, text))],
            }, ct).ConfigureAwait(false);
        }

        // A real edge, so the board's dependency rendering has something to render and "what can start now"
        // has a reason to exclude something.
        var streaming = created.FirstOrDefault(i => i.Title.StartsWith("Stream a large file", StringComparison.Ordinal));
        var badRow = created.FirstOrDefault(i => i.Title.StartsWith("Report a bad row", StringComparison.Ordinal));
        if (streaming is not null && badRow is not null)
        {
            await tracker.LinkAsync(badRow.Id, WorkItemLinkType.BlockedBy, streaming.Id, MutationContext.User, ct)
                .ConfigureAwait(false);
        }

        return created;
    }

    /// <summary>
    /// A document of each type, and deliberately one BROKEN reference. A demo that only shows things working
    /// teaches the reader nothing about what the product does when something is missing, and this product's
    /// whole argument is that it says so rather than hiding it.
    /// </summary>
    private static async Task<int> SeedWikiAsync(IWikiLibrary wiki, IReadOnlyList<WorkItem> items, CancellationToken ct)
    {
        var streaming = items.FirstOrDefault(i => i.Title.StartsWith("Stream a large file", StringComparison.Ordinal));
        var reference = streaming is null ? "an increment" : $"#{streaming.Number}";

        var documents = new (WikiItemType Type, string Title, string Body)[]
        {
            (WikiItemType.Plan, "Plan: stream the import instead of buffering it",
             $"""
              ## Why

              {reference} fails on a 200 MB export because the whole file is read into memory before a single
              row is validated.

              ## Approach

              Read the file as a stream and validate row by row, so memory is bounded by the widest row rather
              than by the file. The mapping step already works a row at a time, so nothing above this changes.

              ## What is not in it

              Parallel parsing. One slow import is a smaller problem than a racy one.
              """),

            (WikiItemType.Adr, "Use the customer's own column names, not ours",
             """
             ## Status

             Accepted.

             ## Decision

             The importer maps the customer's headers onto our fields and keeps THEIR spelling in the mapping
             it remembers, rather than renaming their file to match us.

             ## Consequences

             A customer who renames a column sees a mapping step rather than a refusal. The cost is that the
             remembered mapping is per customer and cannot be shared between them.
             """),

            (WikiItemType.Report, "How long a realistic import actually takes",
             """
             ## What was measured

             Three real exports, on the smallest machine we support, timed end to end.

             | Rows | Size | Time |
             |---|---|---|
             | 12,000 | 4 MB | 1.8 s |
             | 210,000 | 68 MB | 31 s |
             | 640,000 | 205 MB | out of memory |

             ## What it means

             The progress bar has to be honest about MINUTES. The third row is the one that mattered: it did
             not get slower, it stopped, which is why this was worth measuring rather than estimating.
             """),

            (WikiItemType.Draft, "Should an import be undoable?",
             """
             Thinking, not a decision.

             An import that lands 200,000 rows and turns out to have used the wrong mapping is currently
             somebody's afternoon. An undo would need every imported row to carry which import made it, which
             is a column on the hottest table we have.

             A cheaper shape: refuse to import at all until a preview of the first twenty mapped rows has been
             looked at. That trades a rare expensive recovery for a small cost on every import, which may be
             the wrong way round.
             """),

            (WikiItemType.Evidence, "A 200 MB file imported, twice",
             """
             ## What was proven

             The same 205 MB export that ran out of memory in the report above imported twice in a row, in a
             single process, with the working set peaking at 240 MB both times.

             ## What this does NOT prove

             Nothing about concurrent imports: one ran at a time. And the file was well formed, so the bad-row
             path was never taken.

             See @99 for a reference that is deliberately broken, because a demo should show what a missing
             document looks like as well as a present one.
             """),

            (WikiItemType.Architecture, "How an import runs",
             """
             The importer is three stages with one buffer between each, so a slow stage cannot make an earlier
             one allocate without bound.

             1. **Read.** Streams bytes off disk and yields rows. Owns the encoding question, including the
                byte order mark Excel writes.
             2. **Map.** Turns a row into our fields using the customer's saved mapping. Pure: it makes no
                decision that depends on anything outside the row.
             3. **Land.** Writes in batches inside one transaction per batch, so a failure loses a batch and
                not an afternoon.
             """),

            (WikiItemType.Governance, "What an import may never do",
             """
             - It may not write outside the importing customer's own tenant, whatever a file claims.
             - It may not run unattended against a mapping nobody has looked at.
             - A row it cannot map is REPORTED. It is never dropped quietly, and never guessed at.

             The third is the one that gets argued about every time. Guessing is how an import finishes
             faster and how a customer finds out six months later.
             """),

            (WikiItemType.Feature, "Importing, for somebody who did not build it",
             """
             A customer arrives with a spreadsheet from whatever they used before. They upload it, we show
             them what we think each column means, they correct us, and the rows land.

             The part worth knowing: we keep THEIR column names in the mapping rather than renaming their
             file to match ours. It costs a mapping step and it means next month's export works unchanged.
             """),

            (WikiItemType.Legal, "What we may keep from an uploaded file",
             """
             An uploaded export is the customer's data held on their behalf. The file itself is deleted once
             the import finishes or fails; only the mapped rows are kept.

             A failed import keeps the REPORT (which rows failed and why) but not the rows themselves, which
             is the distinction that took the longest to settle: a support answer needs the reason, not the
             data.
             """),

            (WikiItemType.Reference, "The old vendor's CSV, as they actually write it",
             """
             Kept because it is needed every time somebody debugs an import and their documentation does not
             say any of this.

             - The header row is quoted, the data rows are not.
             - Dates are `DD/MM/YYYY` regardless of locale.
             - An empty cell is two quotes, except in the last column, where it is nothing at all.
             - The file ends without a newline.
             """),

            (WikiItemType.Branding, "Colours, type and the logo",
             """
             At most one of these per project, which is why it is its own type rather than a draft.

             | | |
             |---|---|
             | Ink | `#12303f` |
             | Paper | `#fbfaf7` |
             | Accent | `#c8553d` |
             | Type | Source Sans, then the system sans |

             The accent is for one thing on a screen at a time. Two accents mean neither is one.
             """),

            (WikiItemType.Design, "Check the mapping",
             """
             The step every import stops at: what we think each column means, before a single row lands.

             ```mockup
             {"mockup": 1, "title": "Check the mapping",
              "data": {"rows": [{"column": "Cust No", "field": "Customer number"}, {"column": "Dt", "field": "Order date"}]},
              "screens": [{"type": "window", "id": "mapping", "text": "Check the mapping", "layout": "column", "children": [
                {"type": "text", "text": "This is what we think each column means.", "a11y": {"heading": 2}},
                {"type": "table", "data": "rows", "columns": [{"key": "column", "text": "Your column"}, {"key": "field", "text": "Our field"}]},
                {"type": "toolbar", "children": [{"type": "button", "text": "Import"}, {"type": "button", "text": "Cancel"}]}]}]}
             ```

             Import stays the only accent on the screen, as the branding document asks.
             """),

            // Work item #368: a release's notes, the way saving a release writes them.
            (WikiItemType.Release, "Release v0.3.0",
             """
             # Release v0.3.0

             ## Features

             - Check the mapping before an import lands

             ## Fixes

             - Dates in the old vendor's CSV read day first
             """),
        };

        var written = 0;
        WikiItem? design = null;
        foreach (var document in documents)
        {
            var result = await wiki.CreateAsync(
                new NewWikiItem { Type = document.Type, Title = document.Title, Body = document.Body }, ct)
                .ConfigureAwait(false);
            if (result.IsValid)
            {
                written++;
                if (document.Type == WikiItemType.Design)
                {
                    design = result.Value;
                }
            }
        }

        // Backlog 211: a prototype is generated from a design, never written from nothing, so the demo's one
        // prototype is built from the design's REAL number and version once it exists, rather than a number
        // guessed from this list's position.
        if (design is not null)
        {
            var prototypeResult = await wiki.CreateAsync(
                new NewWikiItem
                {
                    Type = WikiItemType.Prototype,
                    Title = "Check the mapping (prototype)",
                    Body = DemoPrototypeBody(design),
                },
                ct).ConfigureAwait(false);
            if (prototypeResult.IsValid)
            {
                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// A prototype of the design above: pinned to the version it was just created at, with a couple of
    /// overlay operations so the page has something to list in words.
    /// </summary>
    private static string DemoPrototypeBody(WikiItem design)
    {
        var document = new PrototypeDocument(
            PrototypeVocabulary.Version,
            new PrototypeDesignRef(design.Number, checked((int)design.Version)),
            [
                new PrototypeAddClassOperation(new PrototypeAnchor("mapping", null), "demo-prototype"),
                new PrototypeSetAttributeOperation(
                    new PrototypeAnchor(null, "/screens/0/children/2/children/0"),
                    "aria-label",
                    "Import the mapped rows"),
                new PrototypePageCssOperation("body { font-family: sans-serif; }"),
            ]);

        return "A generated page for the mapping screen, with two direct edits kept on top of it.\n\n"
            + "```" + PrototypeBody.InfoString + "\n" + PrototypeWriter.Write(document) + "```\n";
    }

    private static async Task<WorkItem?> CreateAsync(
        ITracker tracker, List<WorkItem> created, NewWorkItem item, CancellationToken ct)
    {
        var result = await tracker.CreateAsync(item, MutationContext.User, ct).ConfigureAwait(false);
        if (result.Value is { } value)
        {
            created.Add(value);
            return value;
        }

        return null;
    }

    /// <summary>The clock a caller can read to place the demo's fixed "now"; kept so the field is not unused.</summary>
    public DateTimeOffset Now => clock.GetUtcNow();
}

/// <summary>
/// What a seed run did. <paramref name="Seeded"/> is false when the install already held work items, which is
/// not an error: it is what makes starting a seeded install twice safe.
/// </summary>
public sealed record DemoSeedResult(bool Seeded, int WorkItems, int Documents)
{
    /// <summary>The answer when there was already data, so nothing was written.</summary>
    public static DemoSeedResult AlreadyPopulated { get; } = new(false, 0, 0);
}
