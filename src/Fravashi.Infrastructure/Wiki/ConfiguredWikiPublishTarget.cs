using Fravashi.Application.Wiki;
using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// The space published pages go to (backlog 83). <c>Wiki:Publish:Space</c> wins when somebody set it; otherwise
/// it is the space key the wiki connection's access check was run against, because that is the one space
/// where a write is KNOWN to be permitted: the check created, changed and removed a page there. Read at
/// each use, so a key entered in a running studio is the one the next publish uses.
/// </summary>
public sealed class ConfiguredWikiPublishTarget(IConfiguration configuration) : IWikiPublishTarget
{
    /// <summary>The explicit setting.</summary>
    public const string SpaceKey = "Wiki:Publish:Space";

    /// <summary>The key the wiki step's access check uses, kept by the onboarding draft.</summary>
    public const string CheckedSpaceKey = "Onboarding:Draft:Wiki:SpaceKey";

    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    /// <inheritdoc />
    public string? Space
    {
        get
        {
            var chosen = _configuration[SpaceKey];
            if (!string.IsNullOrWhiteSpace(chosen))
            {
                return chosen.Trim();
            }

            var verified = _configuration[CheckedSpaceKey];
            return string.IsNullOrWhiteSpace(verified) ? null : verified.Trim();
        }
    }
}
