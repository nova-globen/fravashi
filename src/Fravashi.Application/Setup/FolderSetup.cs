using Fravashi.Application.Migration;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;

namespace Fravashi.Application.Setup;

/// <summary>What pressing "Set up" came to: what it did, or why it did nothing.</summary>
/// <param name="Outcome">What was done; null when it was refused.</param>
/// <param name="Refusal">Why nothing was done, for a person to read; null when it was applied.</param>
/// <param name="Field">The field the refusal is about ("name"), so the page can put it there; else null.</param>
public sealed record SetupResult(SetupOutcome? Outcome, string? Refusal, string? Field = null)
{
    /// <summary>Nothing was done, and this is why.</summary>
    public static SetupResult Refused(string why, string? field = null) => new(null, why, field);
}

/// <summary>The files to import and a way to read their text, which only happens when the setup is applied.</summary>
/// <param name="Selection">The chosen files and what each becomes.</param>
/// <param name="ReadAsync">Reads the chosen files' full text.</param>
public sealed record SetupImportSource(
    ImportSelection Selection, Func<ImportSelection, CancellationToken, Task<IReadOnlyDictionary<string, string>>> ReadAsync)
{
    /// <summary>Nothing to import.</summary>
    public static SetupImportSource None { get; } = new(ImportSelection.None, (_, _) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));
}

/// <summary>
/// Applies a folder's setup (item #373, <c>docs/plans/first-open-setup.md</c> section 4): the ONE write the setup makes,
/// run when the person presses "Set up {Folder}" (or "Start empty"). Steps, in order, each recorded so Done can say it:
/// make the project (or take the chosen one) and put the folder in it; record where the board and the wiki live; run the
/// import; record the agent defaults and the trust level; record the outcome in place of the answers.
/// <para>
/// Pressing it again is harmless. The project is the only thing ever created, and the draft records its id the moment it
/// exists, so a second press binds to that project rather than making another; everything after it is a value, not a
/// delta; and a draft that already has an outcome is answered with that outcome and nothing is done twice.
/// </para>
/// </summary>
public sealed class FolderSetup(
    IProjectStore projects,
    IWorkspaceStore workspaces,
    WorkspaceProjectBinding binding,
    IProjectProviders providers,
    IWorkspaceTrust trust,
    IConfigurationStore settings,
    ISetupDraftStore drafts,
    RepositoryImport import)
{
    /// <summary>The longest project name the setup takes.</summary>
    public const int MaxNameLength = 80;

    /// <summary>Applies <paramref name="draft"/> to the folder; see the class summary for the order and the guarantees.</summary>
    public async Task<SetupResult> ApplyAsync(
        string workspaceId, SetupDraft draft, SetupImportSource? source = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Outcome is { } done)
        {
            return new SetupResult(done, null);
        }

        if (string.IsNullOrWhiteSpace(workspaceId) || workspaces.Get(workspaceId) is not { } folder)
        {
            return SetupResult.Refused("No folder is open in this window, so there is nothing to set up.");
        }

        // 1. The project, and the folder in it.
        ProjectRecord project;
        bool isNew;
        if (draft.CreatedProjectId is { Length: > 0 } made && projects.Get(new ProjectId(made)) is { } existing)
        {
            project = existing;
            isNew = true;
        }
        else if (draft.ProjectMode == SetupProjectMode.Join)
        {
            if (draft.JoinProjectId is not { Length: > 0 } join || projects.Get(new ProjectId(join)) is not { } joined)
            {
                return SetupResult.Refused("That project is not there any more. Pick another one, or start a new project.", "join");
            }

            project = joined;
            isNew = false;
        }
        else
        {
            if (NameProblem(draft.NewName) is { } problem)
            {
                return SetupResult.Refused(problem, "name");
            }

            // Asked BEFORE the project is made, so a refused setup never leaves an empty project behind.
            if (WorkspaceProjects.Of(folder) is null && binding.Check(workspaceId) is { } blocked)
            {
                return SetupResult.Refused(blocked.Refusal!);
            }

            project = projects.Create(draft.NewName!.Trim());
            isNew = true;
            draft.CreatedProjectId = project.Id.Value;
            drafts.Save(workspaceId, draft);
        }

        if (WorkspaceProjects.Of(workspaces.Get(workspaceId)) != project.Id)
        {
            var moved = binding.Move(workspaceId, project.Id);
            if (!moved.Moved)
            {
                return SetupResult.Refused(moved.Refusal ?? "The folder could not be put in the project. Nothing else was changed.");
            }
        }

        // 2. Where the board and the wiki live. A joined project keeps its own choices: they are every one of its
        // folders', and Settings, Project is where they change.
        var choice = isNew ? new ProjectProviderChoice(draft.Board, draft.Wiki) : providers.For(project.Id);
        if (isNew)
        {
            providers.Set(project.Id, choice);
        }

        // 3. The import, into an empty project only (RepositoryImport refuses anything else in words).
        var importOutcome = SetupImportOutcome.None;
        var selection = source?.Selection ?? ImportSelection.None;
        if (!draft.IsSkipped(SetupSteps.Import) && !selection.IsEmpty && source is not null)
        {
            try
            {
                var texts = await source.ReadAsync(selection, ct).ConfigureAwait(false);
                var report = await import.ImportAsync(project.Id, selection, texts, ct).ConfigureAwait(false);
                // A refusal made nothing; a stop part way made what the report counts up to the problem it names.
                importOutcome = report.Refusal is not null && report.Problems.Count == 0
                    ? new SetupImportOutcome(false, 0, 0, report.Refusal, [])
                    : new SetupImportOutcome(false, report.WorkItems, report.Documents, report.Refusal, report.Problems,
                        Folders: Math.Max(0, report.Documents - selection.DocumentTypes.Count(d => texts.ContainsKey(d.Key))));
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException)
            {
                importOutcome = new SetupImportOutcome(false, 0, 0, "The files could not be read from the folder just now: " + ex.Message, []);
            }
        }

        // 4. The agents: the project's own defaults (a joined project's only when the person chose one) and the folder's
        // trust level.
        var agentsSkipped = draft.IsSkipped(SetupSteps.Agents);
        var model = agentsSkipped ? null : AgentDefaultsSetting.ValidModel(draft.Model);
        var effort = agentsSkipped ? null : AgentEfforts.Parse(draft.Effort);
        if (isNew || model is not null || effort is not null)
        {
            ProjectAgentDefaults.Write(settings, project.Id, new AgentDefault(model, effort));
        }

        var edits = !agentsSkipped && draft.TrustEdits;
        if (WorkspaceTrustScope.For(workspaces, workspaceId) is { } trustId)
        {
            trust.Set(trustId, edits ? WorkspaceTrust.Edits : WorkspaceTrust.Ask);
        }

        // 5. The outcome replaces the answers.
        var outcome = new SetupOutcome(
            project.Id.Value, project.Name, isNew, choice.Board, choice.Wiki, importOutcome, model, effort?.CliValue(), edits);
        drafts.Save(workspaceId, new SetupDraft { Outcome = outcome, CreatedProjectId = draft.CreatedProjectId, Step = SetupSteps.Done });
        return new SetupResult(outcome, null);
    }

    /// <summary>
    /// "Start empty" (section 7): a new project named after the folder (or the next free "{Folder} 2"), a local board
    /// and wiki, nothing imported, the install's agent defaults and the Ask trust level.
    /// </summary>
    public Task<SetupResult> StartEmptyAsync(string workspaceId, CancellationToken ct = default)
    {
        var folder = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaces.Get(workspaceId);
        var previous = folder is null ? null : drafts.Get(workspaceId);
        var draft = new SetupDraft
        {
            ProjectMode = SetupProjectMode.New,
            NewName = SuggestName(folder?.Name ?? "Project"),
            Board = Projects.ProviderChoice.None,
            Wiki = Projects.ProviderChoice.None,
            CreatedProjectId = previous?.CreatedProjectId,
            Outcome = previous?.Outcome,
        };
        draft.MarkSkipped(SetupSteps.Import, true);
        draft.MarkSkipped(SetupSteps.Agents, true);
        return ApplyAsync(workspaceId, draft, SetupImportSource.None, ct);
    }

    /// <summary>
    /// The name a new project for <paramref name="folderName"/> gets by default: the folder's own name, or, when a project
    /// already has it, the first free "{name} 2", "{name} 3", and so on.
    /// </summary>
    public string SuggestName(string folderName)
    {
        var name = string.IsNullOrWhiteSpace(folderName) ? "Project" : folderName.Trim();
        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength].TrimEnd();
        }

        var taken = projects.List().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
        {
            return name;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{name} {n}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>What is wrong with a project name, in the words the Project step shows, or null when nothing is.</summary>
    public static string? NameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Give the project a name."
        : name.Trim().Length > MaxNameLength ? $"Keep the name under {MaxNameLength} characters."
        : null;
}
