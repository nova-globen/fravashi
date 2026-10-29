namespace Fravashi.Application.Updates;

/// <summary>
/// Reads the latest published version of a package from its registry (Type A update check, ADR 0006). The only
/// implementation talks to the npm registry over a single anonymous HTTPS GET; the base URL is configurable so a
/// test / scenario can point it at a fake endpoint (the SSO/probe test seam). Every returned version is a real
/// registry fact - the checker never invents one.
/// </summary>
public interface IPackageRegistry
{
    /// <summary>
    /// The latest published version of <paramref name="package"/>, or <c>null</c> when the registry cannot be
    /// reached or does not name one (offline, 404, malformed response). A null is honest "unknown", never a
    /// silent "up to date".
    /// </summary>
    Task<string?> GetLatestVersionAsync(string package, CancellationToken cancellationToken = default);
}
