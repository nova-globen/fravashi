namespace Fravashi.Domain.WorkItems;

/// <summary>
/// The outcome of a domain check that produces no value: valid, or refused with every
/// <see cref="ValidationError"/> that applies (not only the first). Also hosts the factories for
/// <see cref="DomainResult{T}"/>.
/// </summary>
public sealed class DomainResult
{
    private DomainResult(IReadOnlyList<ValidationError> errors) => Errors = errors;

    /// <summary>A check that found nothing to refuse.</summary>
    public static DomainResult Valid { get; } = new([]);

    /// <summary>Every refusal; empty when valid.</summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    /// <summary>True when nothing was refused.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>A refusal. At least one error is required, or it would not be a refusal.</summary>
    public static DomainResult Refused(IEnumerable<ValidationError> errors) => new(RequireErrors(errors));

    /// <summary>A successful result carrying its value.</summary>
    public static DomainResult<T> Ok<T>(T value) => new(value, []);

    /// <summary>A refusal of a value-producing check. At least one error is required.</summary>
    public static DomainResult<T> Refused<T>(IEnumerable<ValidationError> errors) => new(default, RequireErrors(errors));

    private static List<ValidationError> RequireErrors(IEnumerable<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var list = errors.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("A refusal needs at least one error.", nameof(errors));
        }

        return list;
    }
}

/// <summary>
/// The outcome of a domain operation that produces a value: the value, or every refusal. Reading
/// <see cref="Value"/> on a refusal is a programming fault and throws.
/// </summary>
/// <typeparam name="T">The value produced when valid.</typeparam>
public sealed class DomainResult<T>
{
    private readonly T? _value;

    internal DomainResult(T? value, IReadOnlyList<ValidationError> errors)
    {
        _value = value;
        Errors = errors;
    }

    /// <summary>Every refusal; empty when valid.</summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    /// <summary>True when nothing was refused.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>The value. Throws when the result is a refusal.</summary>
    public T Value => IsValid
        ? _value!
        : throw new InvalidOperationException(
            "A refused result has no value: " + string.Join("; ", Errors.Select(e => e.Code)));
}
