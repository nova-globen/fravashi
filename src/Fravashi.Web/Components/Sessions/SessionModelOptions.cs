using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Framework;
using Fravashi.Application.Updates;
using Fravashi.Domain.AgentSessions;
using Fravashi.Web.Components.Shared;

namespace Fravashi.Web.Components.Sessions;

/// <summary>
/// Turns the curated model catalog into house-dropdown options (backlog 24). The CLI id stays the label,
/// because it is the thing the user is actually choosing and the thing that appears in the session stats;
/// the catalog's one-line note becomes the subtitle, which is where it belongs and where a native
/// <c>&lt;option&gt;</c> could never put it (it was hidden in a title attribute before this).
/// </summary>
public static class SessionModelOptions
{
    /// <summary>
    /// The option that passes no <c>--model</c> at all. It is the empty string rather than the catalog's
    /// own <c>default</c> id on purpose: the composer's contract with the session runner is that a null
    /// model means "do not pass the flag", and decision 7 keeps that the default.
    /// </summary>
    public const string CliDefaultValue = "";

    /// <summary>
    /// The model families the icon badge can name, longest-lived first. This is deliberately DESCRIPTIVE,
    /// not a ranking: the badge groups a long flat list by family so the eye can find "the Sonnet one",
    /// and it claims nothing about which model is better. An id naming no known family gets no icon at
    /// all rather than a made-up one.
    /// </summary>
    private static readonly (string Family, string Icon)[] Families =
    [
        ("opus", "O"),
        ("sonnet", "S"),
        ("haiku", "H"),
        ("gpt", "G"),
        ("codex", "C"),
        ("fable", "F"),
    ];

    /// <summary>
    /// The selectable models for a runtime, the CLI default first. Its label is one word because it sits
    /// beside the composer's "Model" caption in a pane whose narrowest real width is 280px; the longer
    /// "CLI default" truncated to "CLI defa..." there (measured), and the subtitle says the rest anyway.
    /// </summary>
    /// <para>
    /// While the credential broker carries the runtime (<paramref name="credentials"/>)
    /// a model it does not cover is shown and cannot be chosen, with the reason, and Default names the model it runs on.
    /// A choice stored before stays stored: it runs on the covered model, and is used again on the person's own
    /// key or subscription.
    /// </para>
    public static IReadOnlyList<StudioDropdownOption> For(AgentTool tool, IAgentRuntimeCredentialStore? credentials = null)
    {
        var brokered = credentials?.BrokerCarries(tool) == true;
        var served = brokered ? credentials!.ModelFor(tool, null).Model : null;
        var pinned = AgentRuntimeCatalog.For(tool).PinnedVersion;
        return
        [
            new(CliDefaultValue, "Default", served is null
                ? "Passes no model flag, so the agent CLI picks its own."
                : $"Runs on {served}, the model the free tier serves."),
            .. AgentModelCatalog.For(tool)
                .Where(model => model.Id != "default")
                .Select(model => brokered && credentials!.ModelFor(tool, model.Id).Model != model.Id
                    ? NotCovered(model, served!)
                    : Option(model, pinned)),
        ];
    }

    /// <summary>A model the broker does not cover, shown and not choosable, with why and how to use it.</summary>
    internal static StudioDropdownOption NotCovered(ModelOption model, string served) =>
        new(model.Id, model.Id,
            $"The free tier serves only {served}. Add your own API key or subscription to use {model.Id}.",
            FamilyIcon(model.Id), Unavailable: true, UnavailableWhy: "not on the free tier");

    /// <summary>
    /// One model as an option. A model the pinned CLI is too old for is shown and cannot be chosen, with the reason
    /// in its subtitle (backlog 168): offered, it died on its first turn with an error from the provider.
    /// </summary>
    internal static StudioDropdownOption Option(ModelOption model, string pinnedCli)
    {
        var tooOld = model.MinimumCli is { } minimum
            && SemVer.TryParse(minimum, out var needed)
            && SemVer.TryParse(pinnedCli, out var pinned)
            && pinned.CompareTo(needed) < 0;
        var note = tooOld
            ? $"{model.Note} Needs Claude Code {model.MinimumCli} or newer; this version pins {pinnedCli}."
            : model.Note;
        return new StudioDropdownOption(
            model.Id, model.Id, note?.Trim(), FamilyIcon(model.Id), Unavailable: tooOld,
            UnavailableWhy: tooOld ? "needs a newer agent CLI" : null);
    }

    /// <summary>
    /// The family badge for a model id, or null when the id names no family we recognise. Earliest match in
    /// the id wins, so an id naming two families reads as the first one it names.
    /// </summary>
    public static string? FamilyIcon(string id)
    {
        var best = -1;
        string? icon = null;

        foreach (var (family, badge) in Families)
        {
            var at = id.IndexOf(family, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (best < 0 || at < best))
            {
                best = at;
                icon = badge;
            }
        }

        return icon;
    }
}
