namespace Fravashi.Domain.WorkItems;

/// <summary>
/// Where a mirrored item lives in a provider: the connection, the provider's key, and its native status
/// verbatim. Null on every local-board item; item 47 fills it.
/// </summary>
/// <param name="Connection">The provider connection the item was mirrored through.</param>
/// <param name="Key">The provider's key for the item, e.g. <c>PROJ-12</c>.</param>
/// <param name="NativeStatus">The provider's own status name, kept as it was said.</param>
public sealed record ExternalReference(string Connection, string Key, string? NativeStatus);
