using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.WorkItems;

/// <summary>How a tracker call ended.</summary>
public enum TrackerOutcome
{
    /// <summary>It succeeded (or was a replay of one that did).</summary>
    Ok,

    /// <summary>The addressed item does not exist in this project.</summary>
    NotFound,

    /// <summary>The caller's version is stale.</summary>
    Conflict,

    /// <summary>A rule refused it; see the errors.</summary>
    Invalid,

    /// <summary>The tracker cannot perform the operation at all.</summary>
    Unsupported,
}

/// <summary>
/// The result of a tracker call (plan section 3.2): <c>Ok(value, replayed)</c>, <c>NotFound</c>,
/// <c>Conflict(currentVersion)</c>, <c>Invalid(errors)</c> or <c>Unsupported(reason)</c>. Reading
/// <see cref="Value"/> on anything but Ok is a programming fault and throws.
/// </summary>
/// <typeparam name="T">The value of a successful call.</typeparam>
public sealed class TrackerResult<T>
{
    private readonly T? _value;

    private TrackerResult(TrackerOutcome outcome, T? value, bool replayed, long? currentVersion, IReadOnlyList<ValidationError> errors, string? reason)
    {
        Outcome = outcome;
        _value = value;
        Replayed = replayed;
        CurrentVersion = currentVersion;
        Errors = errors;
        Reason = reason;
    }

    /// <summary>How the call ended.</summary>
    public TrackerOutcome Outcome { get; }

    /// <summary>True when the call succeeded.</summary>
    public bool IsOk => Outcome == TrackerOutcome.Ok;

    /// <summary>True when an Ok is the replay of an operation id already recorded, so nothing was written.</summary>
    public bool Replayed { get; }

    /// <summary>The current version on a Conflict; null otherwise.</summary>
    public long? CurrentVersion { get; }

    /// <summary>Every refusal on an Invalid; empty otherwise.</summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    /// <summary>Why the operation is unsupported; null otherwise.</summary>
    public string? Reason { get; }

    /// <summary>The value of an Ok. Throws on any other outcome.</summary>
    public T Value => IsOk
        ? _value!
        : throw new InvalidOperationException(
            $"A {Outcome} tracker result has no value." + (Errors.Count > 0 ? " " + string.Join("; ", Errors.Select(e => e.Code)) : string.Empty));

    /// <summary>A success.</summary>
    public static TrackerResult<T> Ok(T value, bool replayed = false) => new(TrackerOutcome.Ok, value, replayed, null, [], null);

    /// <summary>The addressed item does not exist.</summary>
    public static TrackerResult<T> NotFound() => new(TrackerOutcome.NotFound, default, false, null, [], null);

    /// <summary>A stale version, naming the current one.</summary>
    public static TrackerResult<T> Conflict(long currentVersion) => new(TrackerOutcome.Conflict, default, false, currentVersion, [], null);

    /// <summary>A refusal. At least one error is required.</summary>
    public static TrackerResult<T> Invalid(IEnumerable<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var list = errors.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("A refusal needs at least one error.", nameof(errors));
        }

        return new(TrackerOutcome.Invalid, default, false, null, list, null);
    }

    /// <summary>A refusal with one error.</summary>
    public static TrackerResult<T> Invalid(string code, string field, string message) => Invalid([new ValidationError(code, field, message)]);

    /// <summary>An operation the tracker cannot perform.</summary>
    public static TrackerResult<T> Unsupported(string reason) => new(TrackerOutcome.Unsupported, default, false, null, [], reason);
}
