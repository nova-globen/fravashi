using System.Globalization;
using System.Text.Json.Nodes;
using Fravashi.Application.Releases;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Releases;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// Work item #368: what this project has released, for an agent (the studio assistant answering "what shipped in
/// v0.9.97"). Read-only: without a version it lists the releases, newest first; with one it reads that release whole,
/// its notes and every item it carried. Saving a release is a person's act on the board and has no tool.
/// </summary>
public sealed class WorkItemReleasesTool(ITrackerResolver trackers, IWorkItemChanges changes, IReleaseStores releases)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "work_item_releases";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Reads this project's releases. Without `version`, lists every release newest first (version, date, tag, commit, "
        + "how many work items, the Release wiki document's number). With `version` (such as v0.9.97), reads that release "
        + "whole: its notes and every work item it carried. Changes nothing.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "version": { "type": "string", "description": "A release's version, such as v0.9.97. Leave out to list them all." }
          },
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.ReadOnly;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(StudioToolCall call, CancellationToken cancellationToken)
    {
        var store = releases.For(call.Project);
        if (Text(call, "version") is not { } version)
        {
            var all = await store.ListAsync(cancellationToken).ConfigureAwait(false);
            return Retrieved(new JsonObject
            {
                ["releases"] = new JsonArray([.. all.Select(r => (JsonNode)Line(r))]),
            });
        }

        if (version.Length > ReleaseVersion.MaxLength)
        {
            return Rejected("bad_version", "version", ReleaseVersion.Rule);
        }

        var release = await store.GetAsync(version, cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return Rejected("release_not_found", "version", $"This project has no release {version}. Call without a version to list them.");
        }

        var payload = Line(release);
        payload["notes"] = release.Notes;
        var items = new JsonArray();
        var tracker = TrackerFor(call);
        foreach (var number in release.ItemNumbers)
        {
            var item = await tracker.GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
            items.Add(item is null
                ? new JsonObject { ["number"] = number }
                : new JsonObject
                {
                    ["number"] = item.Number,
                    ["title"] = item.Title,
                    ["level"] = StableNames.ToName(item.Level),
                    ["kind"] = StableNames.ToName(item.Kind),
                    ["group"] = ReleaseNotes.Classify(item).ToString().ToLowerInvariant(),
                });
        }

        payload["items"] = items;
        return Retrieved(payload);
    }

    private static JsonObject Line(Release release) => new()
    {
        ["version"] = release.Version,
        ["released"] = release.ReleasedUtc.ToString("O", CultureInfo.InvariantCulture),
        ["tag"] = release.Tag,
        ["commit"] = release.Commit,
        ["tagCreatedHere"] = release.TagCreated,
        ["itemCount"] = release.ItemNumbers.Count,
        ["wikiNumber"] = release.WikiNumber,
    };
}
