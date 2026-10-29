using Fravashi.Domain.Projects;

namespace Fravashi.Application.Projects;

/// <summary>A project: the scope work items, their links and their numbers belong to (plan section 2).</summary>
/// <param name="Id">The project's id; <c>default</c> for the seeded project.</param>
/// <param name="Name">Its display name.</param>
/// <param name="CreatedUtc">When it was created.</param>
/// <param name="UpdatedUtc">When it was last renamed.</param>
public sealed record ProjectRecord(ProjectId Id, string Name, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);
