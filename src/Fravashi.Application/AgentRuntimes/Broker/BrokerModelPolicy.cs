namespace Fravashi.Application.AgentRuntimes.Broker;

/// <summary>
/// The model a run actually uses, and why: the one the person or the role asked for, unless the credential broker
/// does not cover it.
/// </summary>
/// <param name="Model">The id handed to the CLI's <c>--model</c>, or null for the CLI's own default.</param>
/// <param name="Requested">What was asked for (null or <c>default</c> when nothing was).</param>
/// <param name="Notice">
/// The words that tell the person a stored choice was not used (never a silent swap), or null when what was
/// asked for runs, or nothing was asked for.
/// </param>
public sealed record AgentModelChoice(string? Model, string? Requested, string? Notice)
{
    /// <summary>The run uses what was asked for: the posture without a broker, and a brokered run on a covered model.</summary>
    public static AgentModelChoice AsAsked(string? requested) => new(requested, requested, null);
}

/// <summary>
/// Which models a brokered run may use (the broker's free tier serves Claude Haiku 5.5 only; a person's own
/// key or subscription is never limited). Every role ends at one CLI invocation, so applying this where the
/// invocation is built covers them all: a new conversation, the studio assistant, the autopilot's builder and
/// operator, the per-project default and the commit reviewer.
/// <para>
/// The broker advertises what a token covers in its mint answer (<c>allowedModels</c>), as patterns: an entry
/// ending in <c>*</c> covers every id it starts, anything else exactly that id. With no list (an older broker), the
/// free tier's one model is assumed. Aliases (<c>haiku</c>, <c>opus</c>, <c>opusplan</c>) are never covered: the CLI
/// resolves them to whatever is newest, which is not the model the pattern names.
/// </para>
/// </summary>
public static class BrokerModelPolicy
{
    /// <summary>The free tier's model, served when the broker names no list.</summary>
    public const string FreeTierModel = "claude-haiku-5-5";

    /// <summary>The patterns assumed when the broker names none.</summary>
    public static readonly IReadOnlyList<string> FallbackPatterns = [FreeTierModel + "*"];

    /// <summary>The broker's patterns, or <see cref="FallbackPatterns"/> when it named none usable.</summary>
    public static IReadOnlyList<string> PatternsOrFallback(IReadOnlyList<string>? advertised)
    {
        var usable = advertised?
            .Where(p => !string.IsNullOrWhiteSpace(p) && p.Trim().TrimEnd('*').Length > 0)
            .Select(p => p.Trim())
            .ToArray();
        return usable is { Length: > 0 } ? usable : FallbackPatterns;
    }

    /// <summary>True when <paramref name="model"/> is covered by one of <paramref name="patterns"/>.</summary>
    public static bool Covers(IReadOnlyList<string> patterns, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var id = model.Trim();
        return patterns.Any(p => p.EndsWith('*')
            ? id.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(id, p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The id a brokered run sends: the first pattern with its wildcard taken off (<c>claude-haiku-5-5*</c> is
    /// <c>claude-haiku-5-5</c>), the bare id the provider answers to.
    /// </summary>
    public static string ServedModel(IReadOnlyList<string> patterns) => PatternsOrFallback(patterns)[0].TrimEnd('*');

    /// <summary>
    /// The model a brokered run uses for <paramref name="requested"/>. A covered id runs as asked. Nothing asked for
    /// (null, empty or <c>default</c>, which lets the CLI pick a model the broker refuses) runs on the served model
    /// quietly. Anything else runs on the served model and says so; the stored choice itself is left alone, for the
    /// day the person runs on their own key.
    /// </summary>
    public static AgentModelChoice Choose(IReadOnlyList<string>? advertised, string? requested)
    {
        var patterns = PatternsOrFallback(advertised);
        if (Covers(patterns, requested))
        {
            return AgentModelChoice.AsAsked(requested!.Trim());
        }

        var served = ServedModel(patterns);
        var nothingAsked = string.IsNullOrWhiteSpace(requested)
            || string.Equals(requested.Trim(), "default", StringComparison.OrdinalIgnoreCase);
        return new AgentModelChoice(served, requested, nothingAsked ? null : SubstitutionNotice(requested!.Trim(), served));
    }

    /// <summary>The words for a stored choice the broker does not cover.</summary>
    public static string SubstitutionNotice(string requested, string served) =>
        $"This conversation runs on {served}, not {requested}: the free tier through the credential broker serves only "
        + $"{served}. Your choice of {requested} is kept and is used when you run on your own key or subscription.";

    /// <summary>
    /// The CLI's own model variables, each set to the served model, so nothing the CLI picks by itself asks for a model
    /// the broker refuses: the session model of a run given no <c>--model</c> (the studio's one-shot calls: wiki
    /// summaries, board mapping), the side model it uses for titles and summaries, the aliases a subagent definition can
    /// name, and the background classifiers. Each name was found in the pinned 2.1.280 binary on 2026-10-29.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CliModelEnvironment(string served) =>
        CliModelVariables.ToDictionary(name => name, _ => served, StringComparer.Ordinal);

    /// <summary>The variables <see cref="CliModelEnvironment"/> sets.</summary>
    public static readonly IReadOnlyList<string> CliModelVariables =
    [
        "ANTHROPIC_MODEL",
        "ANTHROPIC_DEFAULT_HAIKU_MODEL",
        "ANTHROPIC_DEFAULT_SONNET_MODEL",
        "ANTHROPIC_DEFAULT_OPUS_MODEL",
        "ANTHROPIC_DEFAULT_FABLE_MODEL",
        "ANTHROPIC_SMALL_FAST_MODEL",
        "CLAUDE_CODE_SUBAGENT_MODEL",
        "CLAUDE_CODE_BG_CLASSIFIER_MODEL",
        "CLAUDE_CODE_AUTO_MODE_MODEL",
    ];
}
