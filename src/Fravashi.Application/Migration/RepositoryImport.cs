using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Application.Migration;

/// <summary>What an import would do, or did.</summary>
/// <param name="Refusal">Why nothing can be imported (the project is not empty, there is no backlog), or null.</param>
/// <param name="WorkItems">Items read from the backlog.</param>
/// <param name="Placeholders">Numbers the backlog closed and removed, which get a Cancelled placeholder.</param>
/// <param name="Links">Blocks links between items.</param>
/// <param name="PersonBlockers">Blockers that are a person rather than an item.</param>
/// <param name="Documents">Documents that become wiki items.</param>
/// <param name="Problems">Everything that did not go as planned, each said in words. Empty on a clean import.</param>
public sealed record ImportReport(
    string? Refusal,
    int WorkItems,
    int Placeholders,
    int Links,
    int PersonBlockers,
    int Documents,
    IReadOnlyList<string> Problems);

/// <summary>
/// Imports a repository's markdown backlog and documents into a Fravashi project (backlog 49; the decisions are in
/// <c>docs/plans/dogfood-migration.md</c>). It writes only through the tracker and wiki ports a person's own actions
/// use, so every rule of the board and the wiki holds for what it creates, and anything refused is reported rather
/// than forced.
/// <para>
/// It refuses a project that holds anything, because an empty project is what keeps the numbers: documents are
/// created first and take 1..n in a known order, so links between them are rewritten before any is written; work
/// items are then created in number order, with a Cancelled placeholder for every number the backlog removed, so
/// backlog item 87 IS tracker item #87 and no "item 87" in the prose needs rewriting. Every number handed back is
/// checked against the one planned, and the import stops at the first that differs rather than carry on misnumbered.
/// </para>
/// </summary>
public sealed class RepositoryImport(ITrackerResolver trackers, IWikiLibraries wikis, TimeProvider? clock = null)
{
    public const string BacklogPath = "docs/backlog.md";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>What <see cref="ImportAsync(ProjectId, IReadOnlyDictionary{string, string}, bool, CancellationToken)"/> would do, without writing anything.</summary>
    /// <param name="includeDocuments">
    /// False brings the work items only and leaves every document where it is, for a team that keeps its documents in
    /// git (this repository's own choice at its cut-over, 2026-09-30). A link from an item to a document then stays as
    /// written rather than becoming an <c>@N</c> that names nothing.
    /// </param>
    public async Task<ImportReport> PreviewAsync(
        ProjectId project, IReadOnlyDictionary<string, string> files, bool includeDocuments = true, CancellationToken ct = default)
    {
        var plan = LegacyPlan(files, includeDocuments);
        if (plan.Refusal is { } noBacklog)
        {
            return Refused(noBacklog);
        }

        if (await NotEmptyAsync(project, ct).ConfigureAwait(false) is { } notEmpty)
        {
            return Refused(notEmpty);
        }

        return Report(plan, [], stopped: false);
    }

    /// <param name="includeDocuments">As for <see cref="PreviewAsync"/>.</param>
    public async Task<ImportReport> ImportAsync(
        ProjectId project, IReadOnlyDictionary<string, string> files, bool includeDocuments = true, CancellationToken ct = default)
    {
        var plan = LegacyPlan(files, includeDocuments);
        if (plan.Refusal is { } noBacklog)
        {
            return Refused(noBacklog);
        }

        return await RunAsync(project, plan, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Imports exactly the files a person chose, each as what they chose it to become (item #373, the setup's import
    /// step). <paramref name="files"/> holds the text of the chosen files, read at import time. What differs from the
    /// <c>docs/backlog.md</c> import: the backlog may be any file, documents may come from outside <c>docs/</c> and take
    /// any type, a checklist gives one item per box, and having no backlog at all is not a refusal. What does not: it
    /// goes into an empty project only, documents are created first so links between them become references, the first
    /// backlog keeps its numbers (a Cancelled placeholder for each removed one), and the import stops at the first
    /// number that differs from the one planned. Work items from any further file follow, numbered in order.
    /// </summary>
    public async Task<ImportReport> ImportAsync(
        ProjectId project, ImportSelection selection, IReadOnlyDictionary<string, string> files, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(files);
        var plan = SelectionPlan(selection, files);
        if (plan.Refusal is { } refusal)
        {
            return Refused(refusal);
        }

        return await RunAsync(project, plan, ct).ConfigureAwait(false);
    }

    private async Task<ImportReport> RunAsync(ProjectId project, ImportPlan plan, CancellationToken ct)
    {
        if (await NotEmptyAsync(project, ct).ConfigureAwait(false) is { } notEmpty)
        {
            return Refused(notEmpty);
        }

        var problems = new List<string>(plan.Notes);
        var wiki = wikis.For(project);
        var folderIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in plan.Documents)
        {
            // Parents come first in the plan, so the folder a document sits in has already been created.
            var created = await wiki.CreateAsync(
                new NewWikiItem
                {
                    Type = document.Type,
                    Title = document.Title,
                    Body = document.Body,
                    ParentId = document.ParentPath is { } parent ? folderIds[parent] : null,
                }, ct).ConfigureAwait(false);
            if (!created.IsValid)
            {
                problems.Add($"{document.Path} was refused by the wiki: {string.Join("; ", created.Errors.Select(e => e.ToString()))}");
                return Report(plan, problems, stopped: true);
            }

            if (created.Value.Number != document.Number)
            {
                problems.Add($"{document.Path} became @{created.Value.Number}, not the planned @{document.Number}; the import stopped so no link points at the wrong document.");
                return Report(plan, problems, stopped: true);
            }

            folderIds[document.Path] = created.Value.Id;
        }

        var tracker = trackers.For(project);
        var ids = new Dictionary<int, WorkItemId>();
        foreach (var planned in plan.Items)
        {
            var created = await tracker.CreateAsync(planned.Build(plan.DocumentNumbers), MutationContext.User, ct).ConfigureAwait(false);
            if (!created.IsOk)
            {
                problems.Add($"Item {planned.Number} was refused by the tracker ({created.Outcome}): "
                    + string.Join("; ", created.Errors.Select(e => $"{e.Field}: {e.Message}")) + (created.Reason is { } r ? " " + r : string.Empty));
                return Report(plan, problems, stopped: true);
            }

            if (created.Value.Number != planned.Number)
            {
                problems.Add($"Item {planned.Number} became #{created.Value.Number}; the import stopped so no number means the wrong item.");
                return Report(plan, problems, stopped: true);
            }

            ids[planned.Number] = created.Value.Id;
        }

        foreach (var (blocker, blocked) in plan.Blocks)
        {
            if (!ids.TryGetValue(blocker, out var from))
            {
                problems.Add($"Item {blocked} names blocker {blocker}, which the backlog does not have.");
                continue;
            }

            var linked = await tracker.LinkAsync(from, WorkItemLinkType.Blocks, ids[blocked], MutationContext.User, ct)
                .ConfigureAwait(false);
            if (!linked.IsOk)
            {
                problems.Add($"{blocker} blocks {blocked} was refused ({linked.Outcome}): "
                    + string.Join("; ", linked.Errors.Select(e => e.Message)));
            }
        }

        return Report(plan, problems, stopped: false);
    }

    private NewWorkItem ToWorkItem(BacklogEntry entry, string sourcePath, IReadOnlyDictionary<string, int> documents)
    {
        var (level, kind) = entry.Type?.Trim() switch
        {
            "Feature" => (WorkItemLevel.Feature, WorkItemKind.Standard),
            "Increment" => (WorkItemLevel.Increment, WorkItemKind.Standard),
            "Bug" => (WorkItemLevel.Task, WorkItemKind.Bug),
            "Spike" => (WorkItemLevel.Task, WorkItemKind.Spike),
            _ => (WorkItemLevel.Task, WorkItemKind.Standard),
        };
        var (status, resolution) = StatusOf(entry.Status);
        var open = status is not (WorkItemStatus.Done or WorkItemStatus.Cancelled);
        var now = _clock.GetUtcNow();
        return new NewWorkItem
        {
            Level = level,
            Kind = kind,
            Title = entry.Title,
            // Links to imported documents become their @N, and the fields the item carries as data (status, blocking
            // links) are not repeated as text that would go stale (backlog 175); the rest is kept word for word.
            Description = DocumentTree.Rewrite(sourcePath, BacklogMarkdown.WithoutTrackedFields(entry.Body), documents),
            Status = status,
            Resolution = resolution,
            AcceptanceCriteria = entry.Acceptance is { } acceptance ? [new AcceptanceCriterion(string.Empty, acceptance)] : [],
            PersonBlockers =
            [
                .. entry.PersonBlockers.Select(p => new PersonBlocker(
                    string.Empty, p.Person, p.Reason ?? "Named in the backlog's Blocked by field.", now, open ? null : now)),
            ],
        };
    }

    private static NewWorkItem Placeholder(int number, string sourcePath) => new()
    {
        Level = WorkItemLevel.Task,
        Title = $"Number {number} was closed and removed before the import",
        Description = $"Item {number} was closed and removed from {sourcePath} before this project was imported, so its "
            + "history is in git rather than here. It exists only so every other number means what it meant in the backlog.",
        Status = WorkItemStatus.Cancelled,
        Resolution = WorkItemResolution.WontDo,
    };

    /// <summary>One box of a checklist as a work item: an open box an open item, a ticked one a Done item.</summary>
    private static NewWorkItem FromChecklist(ChecklistEntry entry, string sourcePath) => new()
    {
        Level = WorkItemLevel.Task,
        Title = entry.Text,
        Description = $"From {sourcePath}, line {entry.Line}.",
        Status = entry.Done ? WorkItemStatus.Done : WorkItemStatus.Backlog,
        Resolution = entry.Done ? WorkItemResolution.Completed : null,
    };

    public static (WorkItemStatus Status, WorkItemResolution? Resolution) StatusOf(string? status) =>
        (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "backlog" => (WorkItemStatus.Backlog, null),
            "ready" => (WorkItemStatus.Ready, null),
            "in progress" => (WorkItemStatus.InProgress, null),
            "in review" => (WorkItemStatus.InReview, null),
            "done" => (WorkItemStatus.Done, WorkItemResolution.Completed),
            "cancelled" or "canceled" => (WorkItemStatus.Cancelled, WorkItemResolution.WontDo),
            _ => (WorkItemStatus.Triage, null),
        };

    /// <summary>One work item to create, at its planned number; built once the documents' numbers are known.</summary>
    private sealed record PlannedItem(int Number, bool IsPlaceholder, Func<IReadOnlyDictionary<string, int>, NewWorkItem> Build);

    /// <param name="Refusal">Why nothing can be imported, or null.</param>
    /// <param name="Items">The work items in number order, placeholders included.</param>
    /// <param name="Blocks">Each (blocker, blocked) pair by number.</param>
    /// <param name="PersonBlockers">How many person blockers the items carry.</param>
    /// <param name="Documents">The documents, parents first.</param>
    /// <param name="DocumentNumbers">Every path that means a document, with its number.</param>
    /// <param name="Notes">What the plan already knows did not go as asked, said in words.</param>
    private sealed record ImportPlan(
        string? Refusal,
        IReadOnlyList<PlannedItem> Items,
        IReadOnlyList<(int Blocker, int Blocked)> Blocks,
        int PersonBlockers,
        IReadOnlyList<DocumentDraft> Documents,
        IReadOnlyDictionary<string, int> DocumentNumbers,
        IReadOnlyList<string> Notes)
    {
        public static ImportPlan Refuse(string why) => new(why, [], [], 0, [], new Dictionary<string, int>(), []);
    }

    private ImportPlan LegacyPlan(IReadOnlyDictionary<string, string> files, bool includeDocuments)
    {
        if (!files.TryGetValue(BacklogPath, out var backlog))
        {
            return ImportPlan.Refuse($"There is no {BacklogPath} in this folder.");
        }

        var entries = BacklogMarkdown.Parse(backlog);
        if (entries.Count == 0)
        {
            return ImportPlan.Refuse($"{BacklogPath} has no items in the form \"### N. Title\".");
        }

        var duplicate = entries.GroupBy(e => e.Number).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            return ImportPlan.Refuse($"{BacklogPath} has item {duplicate.Key} more than once.");
        }

        var documents = includeDocuments ? DocumentTree.Plan(files) : [];
        var items = new List<PlannedItem>();
        var blocks = new List<(int, int)>();
        KeepNumbers(entries, BacklogPath, items, blocks);
        return new ImportPlan(null, items, blocks, entries.Sum(e => e.PersonBlockers.Count), documents, NumbersOf(documents), []);
    }

    private ImportPlan SelectionPlan(ImportSelection selection, IReadOnlyDictionary<string, string> files)
    {
        var texts = files.ToDictionary(f => f.Key.Replace('\\', '/'), f => f.Value, StringComparer.Ordinal);
        var notes = new List<string>();
        var documents = DocumentTree.Plan(texts, selection.DocumentTypes);
        foreach (var missing in selection.DocumentTypes.Keys.Where(p => !texts.ContainsKey(p.Replace('\\', '/'))).Order(StringComparer.Ordinal))
        {
            notes.Add($"{missing} could not be read, so it was not imported.");
        }

        // The files that become work items: the first that reads as a backlog keeps its numbers; every other file's
        // items (a second backlog, a checklist) follow it, numbered in order.
        var sources = new List<(string Path, IReadOnlyList<BacklogEntry> Backlog, IReadOnlyList<ChecklistEntry> Checklist)>();
        foreach (var choice in selection.WorkItemFiles)
        {
            var path = choice.Path.Replace('\\', '/');
            if (!texts.TryGetValue(path, out var text))
            {
                notes.Add($"{path} could not be read, so no work items came from it.");
                continue;
            }

            var backlog = BacklogMarkdown.Parse(text);
            var checklist = backlog.Count == 0 ? ChecklistMarkdown.Parse(text) : [];
            if (backlog.Count == 0 && checklist.Count == 0)
            {
                notes.Add($"{path} has no backlog items (\"### N. Title\") and no checklist lines (\"- [ ] ...\"), so no work items came from it.");
                continue;
            }

            sources.Add((path, backlog, checklist));
        }

        var items = new List<PlannedItem>();
        var blocks = new List<(int, int)>();
        var personBlockers = 0;
        var keeper = sources.FirstOrDefault(s => s.Backlog.Count > 0);
        if (keeper.Path is not null)
        {
            var duplicate = keeper.Backlog.GroupBy(e => e.Number).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                return ImportPlan.Refuse($"{keeper.Path} has item {duplicate.Key} more than once, so its numbers cannot be kept. Change that row to a document, or fix the file and scan again.");
            }

            KeepNumbers(keeper.Backlog, keeper.Path, items, blocks);
            personBlockers += keeper.Backlog.Sum(e => e.PersonBlockers.Count);
        }

        var next = items.Count + 1;
        foreach (var source in sources.Where(s => s.Path != keeper.Path))
        {
            if (source.Backlog.Count > 0)
            {
                // A second backlog cannot keep its numbers (the first one did), so its items are numbered on, and its own
                // "Blocked by" numbers are read through the same renumbering.
                var renumbered = new Dictionary<int, int>();
                foreach (var entry in source.Backlog.OrderBy(e => e.Number))
                {
                    renumbered.TryAdd(entry.Number, next);
                    var path = source.Path;
                    var number = next++;
                    items.Add(new PlannedItem(number, false, docs => ToWorkItem(entry, path, docs)));
                    personBlockers += entry.PersonBlockers.Count;
                }

                foreach (var entry in source.Backlog)
                {
                    foreach (var blocker in entry.BlockedBy)
                    {
                        if (renumbered.TryGetValue(blocker, out var from) && renumbered.TryGetValue(entry.Number, out var to))
                        {
                            blocks.Add((from, to));
                        }
                    }
                }

                notes.Add($"{source.Path}'s items were numbered from #{renumbered.Values.Min()} on, because {keeper.Path} kept its own numbers.");
                continue;
            }

            foreach (var box in source.Checklist)
            {
                var path = source.Path;
                items.Add(new PlannedItem(next++, false, _ => FromChecklist(box, path)));
            }
        }

        if (documents.Count == 0 && items.Count == 0)
        {
            return ImportPlan.Refuse(notes.Count > 0 ? string.Join(" ", notes) : "Nothing was chosen to import.");
        }

        return new ImportPlan(null, items, blocks, personBlockers, documents, NumbersOf(documents), notes);
    }

    /// <summary>A backlog's items at their own numbers, with a Cancelled placeholder for every number it removed.</summary>
    private void KeepNumbers(IReadOnlyList<BacklogEntry> entries, string path, List<PlannedItem> items, List<(int, int)> blocks)
    {
        var byNumber = entries.ToDictionary(e => e.Number);
        var last = entries.Max(e => e.Number);
        for (var number = 1; number <= last; number++)
        {
            var n = number;
            items.Add(byNumber.TryGetValue(n, out var entry)
                ? new PlannedItem(n, false, docs => ToWorkItem(entry, path, docs))
                : new PlannedItem(n, true, _ => Placeholder(n, path)));
        }

        foreach (var entry in entries)
        {
            foreach (var blocker in entry.BlockedBy)
            {
                blocks.Add((blocker, entry.Number));
            }
        }
    }

    private static IReadOnlyDictionary<string, int> NumbersOf(IReadOnlyList<DocumentDraft> documents) =>
        documents.SelectMany(d => d.Paths.Select(path => (path, d.Number))).ToDictionary(x => x.path, x => x.Number, StringComparer.Ordinal);

    private async Task<string?> NotEmptyAsync(ProjectId project, CancellationToken ct)
    {
        var items = await trackers.For(project).QueryAsync(new WorkItemQuery { Limit = 1 }, ct).ConfigureAwait(false);
        var documents = await wikis.For(project).ListAsync(new WikiItemQuery { Limit = 1 }, ct).ConfigureAwait(false);
        return items.Items.Count > 0 || documents.Count > 0
            ? "This project already holds work items or documents. An import goes into an empty project only, which is what keeps every number meaning what it meant in the backlog: create a new project and move this folder to it first."
            : null;
    }

    private static ImportReport Refused(string why) => new(why, 0, 0, 0, 0, 0, []);

    private static ImportReport Report(ImportPlan plan, List<string> problems, bool stopped) => new(
        stopped ? "The import stopped part way; what it created is in the project and the reason is below." : null,
        plan.Items.Count(i => !i.IsPlaceholder), plan.Items.Count(i => i.IsPlaceholder), plan.Blocks.Count,
        plan.PersonBlockers, plan.Documents.Count, problems);
}
