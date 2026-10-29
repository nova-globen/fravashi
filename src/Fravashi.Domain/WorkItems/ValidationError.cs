namespace Fravashi.Domain.WorkItems;

/// <summary>
/// One refusal from the domain: a machine-readable <paramref name="Code"/> an agent can act on, the
/// <paramref name="Field"/> it concerns so a form can place it under the right input, and a sentence for a
/// person (plan section 3.2). A refusal is a value, never an exception: exceptions stay for genuine faults.
/// </summary>
/// <param name="Code">A stable lower-snake code, registered in <see cref="WorkItemRules.Tiers"/>.</param>
/// <param name="Field">The field or argument the refusal concerns, e.g. <c>description</c>.</param>
/// <param name="Message">A sentence that says what was refused and why.</param>
public sealed record ValidationError(string Code, string Field, string Message);
