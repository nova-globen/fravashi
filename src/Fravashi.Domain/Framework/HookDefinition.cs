using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fravashi.Domain.Framework;

/// <summary>
/// A Claude Code hook event. This is the curated, commonly-authored subset of the full event set (the runtime
/// recognises ~30); the values here cover session lifecycle, the per-turn loop, the agentic tool loop, subagents
/// and compaction. Serialized as its exact PascalCase name into the <c>hooks</c> settings map. Verified against
/// <c>code.claude.com/docs/en/hooks</c>.
/// </summary>
public enum HookEvent
{
    PreToolUse,
    PostToolUse,
    PostToolUseFailure,
    UserPromptSubmit,
    Notification,
    Stop,
    SubagentStart,
    SubagentStop,
    PreCompact,
    PostCompact,
    SessionStart,
    SessionEnd,
}

/// <summary>
/// The authoring shape of a Hook (UI11) - one <c>type: "command"</c> handler bound to a <see cref="HookEvent"/>,
/// the primary and most common hook case. The optional <see cref="Matcher"/> filters when the hook fires (its
/// meaning depends on the event: a tool name for the tool events, a session source for <c>SessionStart</c>, an
/// agent type for the subagent events - surfaced in the UI); <see cref="Command"/> is the shell command to run;
/// <see cref="TimeoutSeconds"/> optionally overrides the cancel timeout. <see cref="Name"/> is a Fravashi label
/// for the artifact, not part of the emitted config. Claude-only (ADR 0005).
/// </summary>
public sealed record HookDefinition
{
    /// <summary>Kebab-case label, unique among hooks (Fravashi's handle for the artifact; not emitted).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The event that triggers the hook.</summary>
    public HookEvent Event { get; init; } = HookEvent.PreToolUse;

    /// <summary>
    /// Optional matcher. Empty / <c>"*"</c> matches everything. A plain name is an exact match (or a
    /// <c>|</c>-separated list); anything else is treated as an unanchored regex by the runtime.
    /// </summary>
    public string Matcher { get; init; } = string.Empty;

    /// <summary>The shell command or executable path the hook runs (<c>type: "command"</c>).</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>Optional cancel timeout in seconds. Null uses the runtime default.</summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>Field validation for a <see cref="HookDefinition"/>. Kebab name, a command, and a non-negative timeout.</summary>
public static class HookValidation
{
    /// <summary>Every validation failure for <paramref name="hook"/>, in field order. Empty when valid.</summary>
    public static IReadOnlyList<SkillValidationError> Validate(HookDefinition hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var errors = new List<SkillValidationError>();

        if (!SkillValidation.IsKebabCase(hook.Name))
        {
            errors.Add(new SkillValidationError(
                "name", "Name must be kebab-case (lowercase letters, digits and single hyphens), 1-64 characters."));
        }

        if (string.IsNullOrWhiteSpace(hook.Command))
        {
            errors.Add(new SkillValidationError("command", "A command to run is required."));
        }

        if (hook.TimeoutSeconds is { } t && t <= 0)
        {
            errors.Add(new SkillValidationError("timeout", "Timeout must be a positive number of seconds."));
        }

        return errors;
    }

    /// <summary>True when <paramref name="hook"/> has no validation errors.</summary>
    public static bool IsValid(HookDefinition hook) => Validate(hook).Count == 0;
}

/// <summary>
/// Serializes a <see cref="HookDefinition"/> to its canonical <c>settings.json</c> fragment - the
/// <c>{ "hooks": { "&lt;Event&gt;": [ { "matcher", "hooks": [ { "type": "command", "command", "timeout" } ] } ] } }</c>
/// shape. JSON is intrinsically quoted; the output is indented and stable for the read-only preview (no file sync).
/// </summary>
public static class HookDocument
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Renders the hooks settings fragment as indented JSON.</summary>
    public static string ToSettingsJson(HookDefinition hook)
    {
        ArgumentNullException.ThrowIfNull(hook);

        var handler = new JsonObject
        {
            ["type"] = "command",
            ["command"] = hook.Command,
        };
        if (hook.TimeoutSeconds is { } timeout)
        {
            handler["timeout"] = timeout;
        }

        var group = new JsonObject();
        if (!string.IsNullOrWhiteSpace(hook.Matcher))
        {
            group["matcher"] = hook.Matcher;
        }

        group["hooks"] = new JsonArray(handler);

        var root = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                [hook.Event.ToString()] = new JsonArray(group),
            },
        };

        return root.ToJsonString(Options);
    }
}
