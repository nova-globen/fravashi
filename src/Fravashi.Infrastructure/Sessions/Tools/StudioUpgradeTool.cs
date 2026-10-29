using System.Text.Json;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Updates;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// Schedules the product's own upgrade for the end of the current turn (backlog 157): the one thing an agent developing
/// Fravashi inside Fravashi could not do, install the release it just cut. It ASKS, every time (it is on no unattended
/// list), and the person approving sees the exact version, because the call names it and nothing else is installed:
/// at the turn boundary fravashi.exe installs what the update channel offers only if it is still that version.
/// </summary>
public sealed class StudioUpgradeTool(IStudioUpgrades upgrades, StudioUpgradeSchedule schedule) : IStudioTool
{
    public const string ToolName = "studio_upgrade";

    public string Name => ToolName;

    public string Description =>
        $"Schedules an upgrade of {ProductName.Short} itself to the version the update channel offers, carried out when this turn "
        + $"ends: {ProductName.Executable} installs it, the studio restarts on it, and this conversation is resumed there (studio_version "
        + "then reports the new number). Name the exact version; studio_version says what the channel offers. A person is "
        + "asked to approve. Edits you made reach the host before the studio restarts.";

    public string InputSchemaJson =>
        """{"type":"object","properties":{"version":{"type":"string","description":"The exact version to install, e.g. 0.9.45."}},"required":["version"],"additionalProperties":false}""";

    public StudioToolAccess Access => StudioToolAccess.Mutating;

    public async Task<StudioToolResult> InvokeAsync(StudioToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var version = call.Arguments.ValueKind == JsonValueKind.Object
            && call.Arguments.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!.Trim().TrimStart('v')
                : null;
        if (string.IsNullOrWhiteSpace(version))
        {
            return StudioToolResult.Failure("Name the version to install, as a string, e.g. {\"version\": \"0.9.45\"}.");
        }

        if (!upgrades.Available)
        {
            return StudioToolResult.Failure(
                $"This studio was started without {ProductName.Executable}'s upgrade operation (a console start, or a {ProductName.Executable} older than this "
                + $"feature), so it cannot upgrade itself. A person can run the installer or `{ProductName.Command} update`.");
        }

        if (await upgrades.CheckAsync(cancellationToken).ConfigureAwait(false) is not { } offer)
        {
            return StudioToolResult.Failure($"{ProductName.Executable}'s upgrade operation could not be asked what the update channel offers.");
        }

        if (offer.Offered is null)
        {
            return StudioToolResult.Failure(offer.Said ?? $"The update channel offers nothing newer than {offer.Installed}.");
        }

        if (!string.Equals(offer.Offered, version, StringComparison.Ordinal))
        {
            return StudioToolResult.Failure(
                $"The update channel offers {offer.Offered}, not {version}. Nothing was scheduled; ask again for {offer.Offered} if that is the one.");
        }

        if (schedule.Schedule(call.SessionId, offer.Offered) is { } refusal)
        {
            return StudioToolResult.Failure(refusal);
        }

        return StudioToolResult.Success(
            $"Scheduled: when this turn ends, {ProductName.Short} is upgraded from {offer.Installed} to {offer.Offered}. Finish what you are "
            + "doing in this turn and end it; the studio restarts on the new build and this conversation is resumed there.");
    }
}
