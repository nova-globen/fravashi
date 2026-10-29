namespace Fravashi.Domain.Wiki;

/// <summary>One refusal from the wiki model: a stable code, the field it concerns, and a sentence.</summary>
/// <param name="Code">A stable lower-snake code an agent can act on.</param>
/// <param name="Field">The field the refusal concerns.</param>
/// <param name="Message">A sentence that says what was refused and why.</param>
public sealed record WikiError(string Code, string Field, string Message);

/// <summary>The codes the wiki model refuses with. They are values, never exceptions (as in the tracker).</summary>
public static class WikiCodes
{
    /// <summary>A type no value defines.</summary>
    public const string TypeUndefined = "type_undefined";

    /// <summary>A blank title.</summary>
    public const string TitleRequired = "title_required";

    /// <summary>A number that is not positive.</summary>
    public const string NumberRequired = "number_required";

    /// <summary>A blank slug.</summary>
    public const string SlugRequired = "slug_required";

    /// <summary>A link kind no value defines.</summary>
    public const string LinkKindUndefined = "link_kind_undefined";

    /// <summary>A link with no target.</summary>
    public const string LinkTargetRequired = "link_target_required";

    /// <summary>A numbered link whose target is not a positive number.</summary>
    public const string LinkNumberRequired = "link_number_required";

    /// <summary>A file link whose path is rooted or climbs out of the repository.</summary>
    public const string LinkPathNotRelative = "link_path_not_relative";

    /// <summary>An item linked to itself.</summary>
    public const string LinkSelf = "link_self";

    /// <summary>A second branding item in a project that already has one.</summary>
    public const string BrandingNotRepeatable = "branding_not_repeatable";

    /// <summary>A slug another item in the project already uses.</summary>
    public const string SlugTaken = "slug_taken";

    /// <summary>The addressed item does not exist.</summary>
    public const string NotFound = "not_found";

    /// <summary>The caller's version is stale.</summary>
    public const string Conflict = "conflict";

    /// <summary>The parent named is not a document of the same project.</summary>
    public const string ParentNotFound = "parent_not_found";

    /// <summary>The parent named would put the document under itself.</summary>
    public const string ParentCycle = "parent_cycle";
}

/// <summary>
/// The result of a wiki operation: a value, or the reasons it was refused. A refusal is a value here for the
/// same reason it is in the tracker, and the two are separate types because the wiki has its own codes and
/// merging them would make one surface's vocabulary leak into the other's.
/// </summary>
/// <typeparam name="T">The value of a successful operation.</typeparam>
public sealed class WikiResult<T>
{
    private readonly T? _value;

    private WikiResult(T? value, IReadOnlyList<WikiError> errors)
    {
        _value = value;
        Errors = errors;
    }

    /// <summary>True when the operation succeeded.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Every refusal; empty on success.</summary>
    public IReadOnlyList<WikiError> Errors { get; }

    /// <summary>The value. Reading it on a refusal is a programming fault and throws.</summary>
    public T Value => IsValid
        ? _value!
        : throw new InvalidOperationException(
            "A refused wiki result has no value: " + string.Join("; ", Errors.Select(e => e.Code)));

    /// <summary>A success.</summary>
    public static WikiResult<T> Ok(T value) => new(value, []);

    /// <summary>A refusal. At least one error is required.</summary>
    public static WikiResult<T> Refused(IEnumerable<WikiError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var list = errors.ToList();
        return list.Count > 0
            ? new WikiResult<T>(default, list)
            : throw new ArgumentException("A refusal needs at least one error.", nameof(errors));
    }

    /// <summary>A refusal with one error.</summary>
    public static WikiResult<T> Refused(string code, string field, string message) =>
        Refused([new WikiError(code, field, message)]);
}
