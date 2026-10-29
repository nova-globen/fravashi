using System.Reflection;

namespace Fravashi.Domain;

/// <summary>
/// The host this build's services live on: the identity provider, the credential broker, the update
/// channel and the product's website are all on ONE host, production by default. A test build names its host at build
/// time with <c>-p:FravashiServiceHost=&lt;host&gt;</c> (<c>Directory.Build.props</c> turns it into assembly metadata;
/// <c>build-release-local.ps1 -ServiceHost</c> and the Dockerfile's <c>SERVICE_HOST</c> pass it), so no test host is
/// ever written in the repository. A plain build, a test run and a dev studio get production.
/// </summary>
/// <remarks>
/// Linked as source, like <see cref="ProductName"/>, into the projects that take no project references by design (the
/// launcher, the web setup core): each assembly reads its OWN metadata, which every project of one publish carries
/// because the property is global to it. Linked, it is compiled <c>internal</c> (the <c>FORMER_NAMES_LINKED</c> symbol
/// those projects define, as for <see cref="FormerNames"/>), so a project that sees both copies has one to choose.
/// </remarks>
#if FORMER_NAMES_LINKED
internal
#else
public
#endif
static class ServiceHost
{
    /// <summary>The production host.</summary>
    public const string Production = "nova-globen.se";

    /// <summary>The assembly metadata key <c>Directory.Build.props</c> writes the build's host under.</summary>
    public const string MetadataKey = "Fravashi.ServiceHost";

    /// <summary>This build's host: the one it was built for, or <see cref="Production"/>.</summary>
    public static string Current { get; } = Resolve(
        typeof(ServiceHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == MetadataKey)?.Value);

    /// <summary>The identity provider's issuer.</summary>
    public static string Issuer => $"https://{Current}";

    /// <summary>The credential broker's base: the studio mints at <c>{base}/broker/token</c> and the agent CLI's
    /// <c>ANTHROPIC_BASE_URL</c> is the base itself.</summary>
    public static string BrokerBaseUrl => $"https://{Current}/fravashi/api";

    /// <summary>
    /// The update channel: one per host. The launcher asks <c>{channel}/release-manifest.json</c> and its
    /// <c>.sig</c>; the web setup asks <c>{channel}/latest/...</c> or <c>{channel}/&lt;version&gt;/...</c>. The host answers
    /// each with a redirect to the public repository's GitHub release asset.
    /// </summary>
    public static string UpdateChannelUrl => $"https://{Current}/fravashi/updates";

    /// <summary>The product's website, which the tray and <c>fravashi website</c> open.</summary>
    public static string WebsiteUrl => $"https://{Current}/fravashi";

    /// <summary>
    /// A host as a build names it, or production when none is named. Only a bare host name is accepted (letters, digits,
    /// dots and hyphens): a scheme, a path or a port would be built into every URL above, so a malformed value fails the
    /// start rather than sending a sign-in somewhere unintended.
    /// </summary>
    public static string Resolve(string? built)
    {
        if (string.IsNullOrWhiteSpace(built))
        {
            return Production;
        }

        var host = built.Trim();
        if (host.Length > 253
            || host.StartsWith('.') || host.EndsWith('.') || host.StartsWith('-')
            || !host.Contains('.')
            || !host.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-'))
        {
            throw new InvalidOperationException(
                $"This build names its service host as '{host}', which is not a lower-case host name.");
        }

        return host;
    }
}
