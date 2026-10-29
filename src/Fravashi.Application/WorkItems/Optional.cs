namespace Fravashi.Application.WorkItems;

/// <summary>
/// A patch value that tells "not mentioned" apart from "set to this, null included" (plan section 3.2).
/// <c>default</c> is not mentioned.
/// </summary>
/// <typeparam name="T">The field's type.</typeparam>
public readonly record struct Optional<T>
{
    private Optional(T value)
    {
        IsSet = true;
        Value = value;
    }

    /// <summary>True when the patch sets the field.</summary>
    public bool IsSet { get; }

    /// <summary>The value to set; meaningful only when <see cref="IsSet"/>.</summary>
    public T Value { get; }

    /// <summary>A value to set.</summary>
    public static Optional<T> Of(T value) => new(value);

    /// <summary>Sets the field.</summary>
    public static implicit operator Optional<T>(T value) => new(value);

    /// <summary>The value when set, otherwise <paramref name="current"/>.</summary>
    public T Or(T current) => IsSet ? Value : current;
}
