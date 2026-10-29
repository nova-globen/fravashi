namespace Fravashi.Application.Updates;

/// <summary>What the update channel offers this install, as fravashi.exe's upgrade operation answered it (backlog 157).</summary>
/// <param name="Installed">The version installed.</param>
/// <param name="Offered">The newer version on offer, or null when there is none.</param>
/// <param name="Critical">Whether the channel marks it required.</param>
/// <param name="Said">Why nothing is offered, in words, when <paramref name="Offered"/> is null.</param>
public sealed record UpgradeOffer(string Installed, string? Offered, bool Critical, string? Said);

/// <summary>Where the last upgrade fravashi.exe ran stands: Idle, Running, Succeeded or Failed, and what it said.</summary>
public sealed record UpgradeProgress(string State, string? Version, string? Said);

/// <summary>
/// The studio's side of the upgrade operation fravashi.exe hosts beside the bridge (backlog 157): it can ask what the update
/// channel offers, ask for exactly that version to be installed, and read how the last upgrade went. Installing
/// replaces this studio's own container, so <see cref="ApplyAsync"/> only STARTS it; the outcome is read after.
/// </summary>
public interface IStudioUpgrades
{
    /// <summary>False when fravashi.exe handed this studio no upgrade operation (a console start, an older fravashi.exe).</summary>
    bool Available { get; }

    /// <summary>What the channel offers now; null when the operation could not be asked.</summary>
    Task<UpgradeOffer?> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts installing <paramref name="version"/>; null when it started, otherwise why not, in words.</summary>
    Task<string?> ApplyAsync(string version, CancellationToken cancellationToken = default);

    /// <summary>How the last upgrade stands; null when the operation could not be asked.</summary>
    Task<UpgradeProgress?> StatusAsync(CancellationToken cancellationToken = default);
}
