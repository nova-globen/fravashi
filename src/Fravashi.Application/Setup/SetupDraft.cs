using System.Text.Json;
using System.Text.Json.Serialization;
using Fravashi.Application.Migration;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;

namespace Fravashi.Application.Setup;

/// <summary>
/// The setup's steps, in order (item #373, <c>docs/plans/first-open-setup.md</c> section 3). Each slug is its route,
/// <c>/setup/{slug}</c>; the recommended setup is <c>/setup</c> itself and is not a step.
/// </summary>
public static class SetupSteps
{
    public const string Project = "project";
    public const string Source = "source";
    public const string Board = "board";
    public const string Wiki = "wiki";
    public const string Import = "import";
    public const string Agents = "agents";
    public const string Done = "done";

    /// <summary>Every step, in the order the stepper shows them, each with its label.</summary>
    public static IReadOnlyList<(string Slug, string Label)> All { get; } =
    [
        (Project, "Project"), (Source, "Source"), (Board, "Board"), (Wiki, "Wiki"), (Import, "Import"), (Agents, "Agents"),
        (Done, "Done"),
    ];

    /// <summary>The steps a person answers, Done aside.</summary>
    public static IReadOnlyList<string> Questions { get; } = [Project, Source, Board, Wiki, Import, Agents];

    /// <summary>The position of a step, or -1 for a slug that is none.</summary>
    public static int IndexOf(string? slug) => All.Select(s => s.Slug).ToList().IndexOf(slug ?? string.Empty);
}

/// <summary>Whether the folder starts a project of its own or joins one that exists.</summary>
public enum SetupProjectMode
{
    /// <summary>A new project, named by the person.</summary>
    New,

    /// <summary>An existing project.</summary>
    Join,
}

/// <summary>What came of the import, for Done to say.</summary>
/// <param name="Skipped">True when the person skipped the step or chose nothing.</param>
/// <param name="WorkItems">The work items it made.</param>
/// <param name="Documents">The documents it made.</param>
/// <param name="Refusal">Why it did not run, or why it stopped part way; null when it ran clean.</param>
/// <param name="Problems">What did not go as planned, each in words.</param>
/// <param name="Folders">The documents among <paramref name="Documents"/> that stand for a folder the files sat in.</param>
public sealed record SetupImportOutcome(
    bool Skipped, int WorkItems, int Documents, string? Refusal, IReadOnlyList<string> Problems, int Folders = 0)
{
    /// <summary>Nothing was imported because nothing was asked for.</summary>
    public static SetupImportOutcome None { get; } = new(true, 0, 0, null, []);

    /// <summary>The documents that were files, folders aside: the number the recommended setup promised.</summary>
    public int Files => Math.Max(0, Documents - Folders);

    /// <summary>True when the import was asked for and did not finish clean.</summary>
    public bool HasProblem => !Skipped && (Refusal is not null || Problems.Count > 0);
}

/// <summary>What the setup did, recorded when it is applied, so Done can say it after a refresh too.</summary>
public sealed record SetupOutcome(
    string ProjectId,
    string ProjectName,
    bool ProjectIsNew,
    ProviderChoice Board,
    ProviderChoice Wiki,
    SetupImportOutcome Import,
    string? Model,
    string? Effort,
    bool TrustEdits);

/// <summary>
/// A folder's setup in progress (item #373, <c>docs/plans/first-open-setup.md</c> sections 4 and 5): every answer, the
/// step reached, and once it is applied what it did. Saved per folder after every change, so Back, a refresh, a closed
/// window and a restart all return to the same answers, and nothing is written anywhere else until the person presses
/// "Set up". A mutable bag on purpose: the steps bind to it, and the store writes it whole.
/// </summary>
public sealed class SetupDraft
{
    /// <summary>The step the person is on; <see cref="SetupSteps.Project"/> until they move.</summary>
    public string Step { get; set; } = SetupSteps.Project;

    /// <summary>True once the person chose "Choose step by step", so <c>/setup</c> resumes on <see cref="Step"/>.</summary>
    public bool StepByStep { get; set; }

    /// <summary>The steps the person has been on, which the stepper lets them go back to.</summary>
    public List<string> Seen { get; set; } = [];

    /// <summary>The steps the person skipped, which the stepper marks.</summary>
    public List<string> Skipped { get; set; } = [];

    public SetupProjectMode ProjectMode { get; set; } = SetupProjectMode.New;

    /// <summary>The new project's name; null until the person or the defaults set it.</summary>
    public string? NewName { get; set; }

    /// <summary>The project to join, by id.</summary>
    public string? JoinProjectId { get; set; }

    /// <summary>Where the board lives: <see cref="ProviderChoice.None"/> is "here", the default.</summary>
    public ProviderChoice Board { get; set; } = ProviderChoice.None;

    /// <summary>Where the wiki lives: <see cref="ProviderChoice.None"/> is "here", the default.</summary>
    public ProviderChoice Wiki { get; set; } = ProviderChoice.None;

    /// <summary>The person's changes to the import preview, by path; the scan's defaults hold for every other row.</summary>
    public Dictionary<string, ImportOverride> Import { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The project's default model for a new conversation; null is the install's default.</summary>
    public string? Model { get; set; }

    /// <summary>The project's default effort for a new conversation, as the CLI's word; null is the install's default.</summary>
    public string? Effort { get; set; }

    /// <summary>True for "let edits inside the copy run without asking"; false is Ask, the default.</summary>
    public bool TrustEdits { get; set; }

    /// <summary>True once the person pressed "Not now", so opening the folder again does not land on the setup.</summary>
    public bool NotNow { get; set; }

    /// <summary>The project this setup made, recorded the moment it exists, so a second press binds to it.</summary>
    public string? CreatedProjectId { get; set; }

    /// <summary>What the apply did; null until it ran.</summary>
    public SetupOutcome? Outcome { get; set; }

    /// <summary>True when the person has answered or moved at all, which is what "Continue setting up" means.</summary>
    [JsonIgnore]
    public bool IsStarted => StepByStep || Seen.Count > 0 || Import.Count > 0 || NewName is not null || JoinProjectId is not null;

    /// <summary>Marks a step as seen (and not skipped), keeping the order steps were first seen in.</summary>
    public void See(string step)
    {
        if (!Seen.Contains(step, StringComparer.Ordinal))
        {
            Seen.Add(step);
        }
    }

    /// <summary>Marks a step skipped or not.</summary>
    public void MarkSkipped(string step, bool skipped)
    {
        Skipped.RemoveAll(s => s == step);
        if (skipped)
        {
            Skipped.Add(step);
        }
    }

    /// <summary>True when the step was skipped.</summary>
    public bool IsSkipped(string step) => Skipped.Contains(step, StringComparer.Ordinal);
}

/// <summary>Where a folder's setup draft is kept between visits (item #373).</summary>
public interface ISetupDraftStore
{
    /// <summary>The folder's draft, or null when there is none (or what is stored cannot be read).</summary>
    SetupDraft? Get(string workspaceId);

    /// <summary>Stores the folder's draft whole.</summary>
    void Save(string workspaceId, SetupDraft draft);

    /// <summary>Forgets the folder's draft.</summary>
    void Remove(string workspaceId);
}

/// <summary>
/// The drafts as JSON rows of the settings table, one per folder under <c>FolderSetup:{workspaceId}</c> (section 5), so
/// a backup carries a half-finished setup like everything else. A row that does not read is treated as no draft: the
/// setup starts again from its defaults rather than failing.
/// </summary>
public sealed class SetupDraftStore(IConfigurationStore settings) : ISetupDraftStore
{
    /// <summary>The settings-table key prefix.</summary>
    public const string Prefix = "FolderSetup:";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc />
    public SetupDraft? Get(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || settings.Get(Prefix + workspaceId) is not { Length: > 0 } stored)
        {
            return null;
        }

        try
        {
            var draft = JsonSerializer.Deserialize<SetupDraft>(stored, Json);
            if (draft is not null && draft.Import.Comparer != StringComparer.Ordinal)
            {
                draft.Import = new Dictionary<string, ImportOverride>(draft.Import, StringComparer.Ordinal);
            }

            return draft;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Save(string workspaceId, SetupDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            settings.Set(Prefix + workspaceId, JsonSerializer.Serialize(draft, Json));
        }
    }

    /// <inheritdoc />
    public void Remove(string workspaceId)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            settings.Remove(Prefix + workspaceId);
        }
    }
}
