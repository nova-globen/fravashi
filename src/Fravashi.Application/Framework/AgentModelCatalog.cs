using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Framework;

/// <summary>One selectable model: its CLI id and a short human note.</summary>
/// <param name="Id">The exact id passed to the CLI's <c>--model</c> flag.</param>
/// <param name="Note">A one-line description, or null.</param>
/// <param name="MinimumCli">
/// The oldest agent CLI the provider accepts this model from, or null when any is accepted (backlog 168). The
/// provider checks the CLI's version itself: Fable 5.1 answered "Claude Code 2.1.222 does not support this model;
/// version 2.1.251 or newer is required" on 2026-09-29, so a model the pin is too old for cannot be offered.
/// </param>
/// <param name="MostCapable">
/// True on the one model of a runtime's list that is its most capable (backlog 243): the commit reviewer role's
/// shipped default, by the decision. A recorded fact on the entry rather than a position in the list, so
/// reordering the picker cannot quietly move the default.
/// </param>
public sealed record ModelOption(string Id, string? Note = null, string? MinimumCli = null, bool MostCapable = false);

/// <summary>Where a resolved model list came from - the live probe, the curated catalog, or nothing.</summary>
public enum ModelListSource
{
    /// <summary>No target/runtime, or nothing available.</summary>
    None,

    /// <summary>A live CLI probe listed the models (authoritative and current).</summary>
    Probe,

    /// <summary>The curated per-runtime catalog (the dated fallback).</summary>
    Catalog,
}

/// <summary>The resolved model list for one runtime, plus where it came from (for honest UI attribution).</summary>
/// <param name="Tool">The runtime.</param>
/// <param name="Models">The available models.</param>
/// <param name="Source">Probe, Catalog, or None.</param>
/// <param name="Provenance">A short human note on the source (date, or "live probe").</param>
public sealed record ModelListResult(
    AgentTool Tool,
    IReadOnlyList<ModelOption> Models,
    ModelListSource Source,
    string Provenance);

/// <summary>
/// The curated per-runtime model catalog (D03-12): a code-maintained list of current model ids with provenance
/// and a date, so a stale entry is obvious. It is the honest fallback when a runtime cannot list its models
/// live (see <see cref="IModelListResolver"/>, resolution order probe-then-catalog). Values were researched
/// from official CLI docs on the date below; a code review at that cadence keeps them current.
/// </summary>
public static class AgentModelCatalog
{
    /// <summary>
    /// The date a curated entry was last verified against official docs, for any runtime whose list is not
    /// driven through its own CLI. Shown in the UI. Nothing uses it while Claude is the only runtime, and it
    /// stays because the retired lists in docs/v2.md section 1 carry this date.
    /// </summary>
    public const string VerifiedOn = "2026-07-16";

    /// <summary>The date the Claude list was last driven through the real CLI (backlog 111). Shown in the UI.</summary>
    public const string ClaudeVerifiedOn = "2026-09-30";

    /// <summary>
    /// The Claude Code pin (<c>AgentRuntimeCatalog</c>, ADR 0023) the Claude list below is tied to. Claude Code
    /// has no list-models command, so this list can only be PINNED, and a pinned list drifts silently as the
    /// vendor ships models (backlog 111: Fable 5 was invisible for weeks). The drift is made a build-time fact
    /// the same way the CLI protocol is: <c>ModelListProbeAndResolverTests</c> asserts this equals the runtime
    /// pin, so promoting the CLI (ADR 0023's procedure) forces someone to re-drive the list below and move this
    /// literal with the pin. Never move it on its own: that is the claim rotting, written down.
    /// </summary>
    public const string ClaudeVerifiedAgainstCliPin = "2.1.280";

    /// <summary>
    /// The Claude Code binary that actually ANSWERED the live check: the owner's host install, not the pinned
    /// one. ADR 0023 keeps "pinned" and "verified" as two recorded facts, and this is the second: a reader of the
    /// provenance line must not take the pin above for the binary that ran. Move it whenever the list is
    /// re-driven, to whatever answered that time.
    /// </summary>
    public const string ClaudeVerifiedWithCli = "2.1.280";

    // ---- Claude Code: no scriptable list-models command (anthropics/claude-code#12612); this catalog is the
    // source. Provenance: https://code.claude.com/docs/en/model-config (alias table + "any claude-* name
    // accepted"), cross-referenced at https://platform.claude.com/docs/en/about-claude/models/overview.
    //
    // LIVE CHECK 2026-09-13 (the acceptance for backlog 111): every id below, aliases included, was run ONCE
    // from PowerShell through the owner's authenticated Claude Code CLI on the host, with CLAUDECODE removed
    // from the environment:
    //   claude -p "Reply with the single word ok." --model <id> --max-turns 1 --output-format json
    // and judged on the result line's is_error and its modelUsage keys (which show what an alias resolved to).
    // Every call answered "ok" with is_error=false and subtype=success. The claude-haiku-4-5-20251001 key that
    // appeared on every call is the CLI's own side model (title/summary work), not the answer's model.
    //
    //   id                 accepted   modelUsage key besides the side model (i.e. what it resolved to)
    //   opus               yes        claude-opus-5
    //   sonnet             yes        claude-sonnet-5
    //   haiku              yes        (none: the answer ran on claude-haiku-4-5-20251001 itself)
    //   opusplan           yes        claude-sonnet-5 (Sonnet on an executing turn, Opus only while planning)
    //   claude-fable-5-1   yes        claude-fable-5-1
    //   claude-opus-5      yes        claude-opus-5
    //   claude-sonnet-5    yes        claude-sonnet-5
    //   claude-opus-4-8    yes        claude-opus-4-8
    //   claude-sonnet-4-5  yes        claude-sonnet-4-5
    //   claude-haiku-4-5   yes        claude-haiku-4-5
    //
    // Honest about the binary: the host install that answered was Claude Code 2.1.270, not the 2.1.222 the
    // studio pins and bakes, because the pinned binary was not on this PC and the container must stay down
    // for a check like this. The list is tied to the pin (ClaudeVerifiedAgainstCliPin) so that the NEXT
    // promotion re-drives it against the promoted binary.
    //
    // LIVE CHECK 2026-09-30, when the pin was promoted to 2.1.280, and this time through the PINNED binary itself (a
    // throwaway install of 2.1.280 in the studio's own data volume, the owner's subscription sign-in), the same
    // command for every id:
    //
    //   id                 answer                     what it resolved to (besides the side model)
    //   opus               ok                         claude-opus-5-5   (the alias moved: it was Opus 5 on 2026-09-13)
    //   sonnet             ok                         claude-sonnet-5
    //   haiku              ok                         (none: it ran on claude-haiku-4-5-20251001 itself)
    //   opusplan           ok                         claude-sonnet-5
    //   claude-fable-5-1   429 "You've reached your   (a usage limit on the account, not the version refusal
    //                      Fable limit"               2.1.222 got: the CLI gate is cleared, the account was not)
    //   claude-opus-5-5    ok                         claude-opus-5-5   (new here)
    //   claude-opus-5      ok                         claude-opus-5
    //   claude-sonnet-5    ok                         claude-sonnet-5
    //   claude-opus-4-8    ok                         claude-opus-4-8
    //   claude-sonnet-4-5  ok                         claude-sonnet-4-5
    //   claude-haiku-4-5   ok                         claude-haiku-4-5
    //
    // WRONG, measured 2026-09-29 (backlog 168): this comment used to end "a model id is passed through to the API,
    // so the acceptance above is an API-side fact that a CLI version does not change on its own". The API checks
    // the CLI's version: the pinned 2.1.222 asked for claude-fable-5-1 in the self-hosting trial and got "API Error:
    // 400 Claude Code 2.1.222 does not support this model; version 2.1.251 or newer is required". So a model the
    // pin is too old for carries its MinimumCli, and the picker shows it as unavailable with that reason.
    //
    // LIVE CHECK 2026-10-29, one id only: claude-haiku-5-5, through the PINNED 2.1.280 against a test
    // credential broker: "say hi" answered with is_error=false, its
    // modelUsage key claude-haiku-5-5, and one stderr diagnostic, [claude-code:unrecognized_model]: the CLI does not
    // know the id and passes it through, and the API accepted it from 2.1.280. The rest of the list was not re-driven,
    // so ClaudeVerifiedOn stays the date the whole list was. It is the model the broker's free tier serves.
    private static readonly IReadOnlyList<ModelOption> Claude =
    [
        new("default", "Account/organisation default (clears any override)."),
        new("opus", "Latest Opus (resolved to Opus 5.5 on 2026-09-30)."),
        new("sonnet", "Latest Sonnet (resolved to Sonnet 5 on 2026-09-30)."),
        new("haiku", "Latest Haiku, fastest and cheapest (resolved to Haiku 4.5 on 2026-09-30)."),
        new("opusplan", "Opus while planning, Sonnet while executing."),
        new("claude-fable-5-1", "Pinned Fable 5.1, the most capable model.", MinimumCli: "2.1.251", MostCapable: true),
        new("claude-opus-5-5", "Pinned Opus 5.5."),
        new("claude-opus-5", "Pinned Opus 5."),
        new("claude-sonnet-5", "Pinned Sonnet 5."),
        new("claude-haiku-5-5", "Pinned Haiku 5.5, the model the free tier serves."),
        new("claude-opus-4-8", "Pinned Opus 4.8 (previous Opus generation)."),
        new("claude-sonnet-4-5", "Pinned Sonnet 4.5 (previous Sonnet generation)."),
        new("claude-haiku-4-5", "Pinned Haiku 4.5."),
    ];

    // RETIRED to v2, decided 2026-09-22. See docs/v2.md section 1. The lists below are kept rather than
    // deleted because a live Codex turn measured something the next version has to cope with and that these
    // entries would send it straight back into: the first five ids an API-key project was offered all answered
    // 403 or 404, so the shipped default would have failed on its first turn (docs/v2.md section 1.4).
    //
    // // ── OpenAI Codex - a real scriptable probe exists (`codex debug models`); this is the dated fallback. ──
    // // Provenance: https://learn.chatgpt.com/docs/models (Codex models reference). Model set shifts release to
    // // release and the doc is authoritative. Verified 2026-07-16.
    // private static readonly IReadOnlyList<ModelOption> Codex =
    // [
    //     new("gpt-5.6-sol", "Current top Codex coding model."),
    //     new("gpt-5.5", "Default for ChatGPT-authenticated Codex sessions."),
    //     new("gpt-5.4", "Prior default / fallback."),
    //     new("gpt-5.4-mini", "Lighter tasks, subagents, interactive edits."),
    //     new("codex-mini-latest", "Lightweight non-interactive model."),
    // ];
    //
    // // ── GitHub Copilot CLI - no scriptable list-models command (github/copilot-cli#236); catalog is the source.
    // // Provenance: https://docs.github.com/en/copilot/reference/ai-models/supported-models (official display
    // // names). The exact CLI --model slugs below are corroborated by community references, not a single
    // // first-party page enumerating slugs - treat as high-confidence-but-not-first-party. Verified 2026-07-16.
    // private static readonly IReadOnlyList<ModelOption> Copilot =
    // [
    //     new("claude-sonnet-4.5", "Default model for the Copilot CLI."),
    //     new("claude-haiku-4.5", "Faster, cheaper Claude."),
    //     new("gpt-5.1", "OpenAI GPT-5.1."),
    //     new("gpt-5.1-codex", "GPT-5.1 Codex variant."),
    //     new("gpt-5", "OpenAI GPT-5."),
    // ];

    /// <summary>The curated catalog for a runtime.</summary>
    public static IReadOnlyList<ModelOption> For(AgentTool tool) => tool switch
    {
        AgentTool.Claude => Claude,
        _ => [],
    };

    /// <summary>
    /// The runtime's most capable model (the entry marked <see cref="ModelOption.MostCapable"/>), or null when its
    /// list marks none. The commit reviewer's default model (backlog 243).
    /// </summary>
    public static ModelOption? MostCapable(AgentTool tool) => For(tool).FirstOrDefault(m => m.MostCapable);

    /// <summary>
    /// A short provenance note for the catalog source, shown in the UI. The Claude line names BOTH the binary
    /// that answered and the pin the list is tied to, because they differ and a line naming only the pin would
    /// read as if the pinned binary had been driven (backlog 111, the ADR 0023 distinction).
    /// </summary>
    public static string ProvenanceFor(AgentTool tool) => tool switch
    {
        AgentTool.Claude =>
            $"Curated catalog, driven through Claude Code {ClaudeVerifiedWithCli} on {ClaudeVerifiedOn}; "
            + $"tied to the shipped pin {ClaudeVerifiedAgainstCliPin}.",
        _ => $"Curated catalog, verified {VerifiedOn}.",
    };
}
