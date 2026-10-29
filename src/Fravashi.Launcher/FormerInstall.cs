using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// The launcher's public view of the names an install from before the internal rename still carries, read from
/// <see cref="FormerNames"/> (linked here as source and internal) and nowhere else: fravashi.exe, which cannot see the
/// linked copy, and Launcher.Tests, which sees two copies of it, read them here. The cleanup release (plan phase 5)
/// deletes this file with FormerNames.
/// </summary>
public static class FormerInstall
{
    /// <summary>The former studio container.</summary>
    public const string ContainerName = FormerNames.ContainerName;

    /// <summary>The former image tag.</summary>
    public const string Image = FormerNames.Image;

    /// <summary>The former VPN sidecar container.</summary>
    public const string VpnContainerName = FormerNames.VpnContainerName;

    /// <summary>The former rootless-podman studio network.</summary>
    public const string StudioNetwork = FormerNames.StudioNetwork;

    /// <summary>The former agent-workspaces volume (regenerable; kept until phase 5, never copied).</summary>
    public const string WorkspacesVolume = FormerNames.WorkspacesVolume;

    /// <summary>The former database file name.</summary>
    public const string DatabaseFileName = FormerNames.DatabaseFileName;

    /// <summary>The former app-runner owner label key.</summary>
    public const string AppRunnerOwnerLabel = FormerNames.AppRunnerOwnerLabel;

    /// <summary>The value the former fravashi.exe put under <see cref="AppRunnerOwnerLabel"/>.</summary>
    public const string AppRunnerOwner = FormerNames.AppRunnerOwner;

    /// <summary>The former single-instance mutex.</summary>
    public const string SingleInstanceMutex = FormerNames.SingleInstanceMutex;

    /// <summary>The former exit event.</summary>
    public const string ExitEvent = FormerNames.ExitEvent;

    /// <summary>The former backup archive kind.</summary>
    public const string BackupKind = FormerNames.BackupKind;

    /// <summary>The former first-run hint marker.</summary>
    public const string FirstRunHintMarker = FormerNames.FirstRunHintMarker;

    /// <summary>The image label an old fravashi.exe reads the variant from (the bridge release's refusal, see the Dockerfile).</summary>
    public const string ImageVariantLabel = FormerNames.ImageVariantLabel;
}
