using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Fravashi.Domain.AgentSessions;

/// <summary>What a tool input field held, as far as a security decision is allowed to care.</summary>
public enum AgentToolValueKind
{
    /// <summary>The tool did not send this field at all.</summary>
    Absent,

    /// <summary>The tool sent it as a JSON string, so it can be read as text the tool will act on.</summary>
    Text,

    /// <summary>
    /// The tool sent it as something else (an object, an array, a number, a boolean, a null). Its
    /// flattened form exists for the UI only: nothing may read it as a value the tool named.
    /// </summary>
    NotText,
}

/// <summary>
/// A tool call's arguments, flattened to strings for everything downstream, but keeping the one fact
/// the flattening otherwise destroys: whether a value ARRIVED as a JSON string.
/// <para>
/// That fact is load-bearing. A path-bearing field that is not a JSON string is not a path, and the
/// studio must not invent one out of its rendering: <c>GetRawText()</c> turns
/// <c>{"a/b/c/d/e/f/g/h":1}</c> into text whose JSON punctuation reads as eight directory segments,
/// which was enough of a manufactured base for a pattern's eight <c>..</c> to land back inside the
/// contained copy and be auto-allowed. <c>null</c>, <c>5</c>, <c>[]</c> and <c>true</c> all read the
/// same way: present, non-empty, relative directory names that were never any such thing.
/// </para>
/// <para>
/// So the type travels with the value and the containment check (<c>AgentReadScope</c>) refuses to
/// guess: a path-bearing field that is not <see cref="AgentToolValueKind.Text"/> is undeterminable, and
/// undeterminable ASKS. Fields that name nothing on disk are unaffected and keep whatever
/// representation the approval prompt needs to render them.
/// </para>
/// </summary>
public sealed class AgentToolInput : IReadOnlyDictionary<string, string>
{
    private readonly IReadOnlyDictionary<string, string> _values;

    // The keys whose JSON value was a string. Stored as the allowlist rather than its complement so a
    // key nobody classified reads as NotText, which is the direction that asks.
    private readonly IReadOnlySet<string> _text;

    private AgentToolInput(IReadOnlyDictionary<string, string> values, IReadOnlySet<string> text)
    {
        _values = values;
        _text = text;
    }

    /// <summary>A call with no arguments at all.</summary>
    public static AgentToolInput Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));

    /// <summary>
    /// Arguments that are all text, for an in-process caller holding real strings. Nothing here was
    /// flattened from JSON, so there is no type to lose.
    /// </summary>
    public static AgentToolInput FromStrings(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return new AgentToolInput(
            new Dictionary<string, string>(values, StringComparer.Ordinal),
            new HashSet<string>(values.Keys, StringComparer.Ordinal));
    }

    /// <summary>
    /// Arguments flattened from JSON: <paramref name="values"/> is what the UI renders, and
    /// <paramref name="textFields"/> names ONLY the keys whose JSON value was a string. A key left out
    /// of it is treated as not a string, which is the fail-closed direction.
    /// </summary>
    public static AgentToolInput Create(IReadOnlyDictionary<string, string> values, IEnumerable<string> textFields)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(textFields);

        return new AgentToolInput(
            new Dictionary<string, string>(values, StringComparer.Ordinal),
            new HashSet<string>(textFields, StringComparer.Ordinal));
    }

    /// <summary>
    /// A plain string dictionary is a call whose every value is text, so it converts without ceremony.
    /// Anything that came off the wire must go through <see cref="Create"/> instead, which is the only
    /// way a field can be marked as not-a-string.
    /// </summary>
    public static implicit operator AgentToolInput(Dictionary<string, string> values) => FromStrings(values);

    /// <summary>The same conversion, named, for callers that prefer not to lean on the operator.</summary>
    public static AgentToolInput ToAgentToolInput(Dictionary<string, string> values) => FromStrings(values);

    /// <summary>What this field held: absent, a JSON string, or something that is not one.</summary>
    public AgentToolValueKind KindOf(string field)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (!_values.ContainsKey(field))
        {
            return AgentToolValueKind.Absent;
        }

        return _text.Contains(field) ? AgentToolValueKind.Text : AgentToolValueKind.NotText;
    }

    /// <summary>
    /// The field's value when it arrived as a JSON string, or null in every other case (absent, or
    /// present as something that is not a string). A caller deciding containment must use THIS, never
    /// the indexer: the indexer answers with the display rendering of a non-string too.
    /// </summary>
    public string? Text(string field) =>
        KindOf(field) == AgentToolValueKind.Text ? _values[field] : null;

    public string this[string key] => _values[key];

    public IEnumerable<string> Keys => _values.Keys;

    public IEnumerable<string> Values => _values.Values;

    public int Count => _values.Count;

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) =>
        _values.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
