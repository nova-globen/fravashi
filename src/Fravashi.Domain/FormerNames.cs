namespace Fravashi.Domain;

/// <summary>
/// Every name the product had before the internal rename to Fravashi that an installed copy still carries, in ONE place
/// (<c>docs/plans/rename-internal-names.md</c>, phase 2, "the bridge release"). The migrations that move an install from
/// the old names to the new ones read the old names here and nowhere else, and <c>NoFormerNamesTests</c> allows this file
/// for exactly that reason. THE CLEANUP RELEASE (plan phase 5) DELETES THIS FILE, together with every migration that reads
/// it, once this PC and the test VM run the bridge release and their data has moved.
/// </summary>
/// <remarks>
/// Linked as source, like <see cref="ProductName"/>, into the projects that take no project references by design (the
/// launcher, the bridge, the app runner, the web setup core and the broker installer). Linked, it is compiled
/// <c>internal</c> (the <c>FORMER_NAMES_LINKED</c> symbol those projects define), so a project that sees Domain's public
/// copy and a linked one never has two types of the same name to choose from; the launcher re-exports what fravashi.exe
/// needs through its own <c>FormerInstall</c>.
/// <para>
/// Only the product's OWN former names are here, never a host: the names external systems knew it by (the
/// identity provider's client, the update channel's and the website's old hosts) were retired with phase 3.
/// </para>
/// </remarks>
#if FORMER_NAMES_LINKED
internal
#else
public
#endif
static class FormerNames
{
    /// <summary>The product's former name: ProductName.Former says "formerly ..." with it.</summary>
    public const string Product = "DidaDeck";

    /// <summary>The former data folder under %LOCALAPPDATA%: ProductHome and DataFolderMove move it to the new one.</summary>
    public const string DataFolder = "DidaDeck";

    /// <summary>The former database file in the data directory: the launcher renames it before the studio starts
    /// (FormerInstallMigration), the studio's DataDirectory opens it while that has not happened yet, and a restore takes
    /// it from an old archive.</summary>
    public const string DatabaseFileName = "didadeck.db";

    /// <summary>The former studio container: launcher.json named it; the launcher stops and removes it before it creates the new one.</summary>
    public const string ContainerName = "didadeck";

    /// <summary>The former image tag: launcher.json named it; the launcher removes the tag once the new studio is healthy.</summary>
    public const string Image = "didadeck:latest";

    /// <summary>The former VPN sidecar container: launcher.json's vpn section named it; removed with the old studio container.</summary>
    public const string VpnContainerName = "didadeck-vpn";

    /// <summary>The former rootless-podman studio network: removed once the old studio container is gone.</summary>
    public const string StudioNetwork = "didadeck-studio";

    /// <summary>The former agent-workspaces volume. It holds only synced copies the host re-syncs, so it is not copied:
    /// the new volume starts empty and this one is kept until phase 5 (FormerInstallMigration says so in the log).</summary>
    public const string WorkspacesVolume = "didadeck-workspaces";

    /// <summary>The former app-runner owner label key: leftovers an old app runner made are found by it and removed.</summary>
    public const string AppRunnerOwnerLabel = "didadeck.apprunner";

    /// <summary>The value the former fravashi.exe put under <see cref="AppRunnerOwnerLabel"/>.</summary>
    public const string AppRunnerOwner = "didadeck";

    /// <summary>The former Data Protection application name: the studio unprotects every stored secret with it once and
    /// protects it again under the new name (FormerKeyRingCarryOver).</summary>
    public const string DataProtectionApplicationName = "DidaDeck";

    /// <summary>The former protector purpose of the secret store, read by the same carry-over.</summary>
    public const string CredentialsPurpose = "DidaDeck.Credentials.v1";

    /// <summary>The former marker of the stored credential envelope; CredentialEnvelope.Parse reads both.</summary>
    public const string CredentialEnvelopeMarker = "didadeck.cred.v1";

    /// <summary>The former single-instance mutex: the new fravashi.exe holds it too, so an old and a new copy never run together.</summary>
    public const string SingleInstanceMutex = @"Local\DidaDeck.Tray.SingleInstance";

    /// <summary>The former exit event: <c>fravashi exit</c> and the new resident's start ask an old resident to close through it.</summary>
    public const string ExitEvent = @"Local\DidaDeck.Tray.Exit";

    /// <summary>The former first-run hint marker beside launcher.json: an old marker counts as the hint already shown.</summary>
    public const string FirstRunHintMarker = ".deck-hint-shown";

    /// <summary>The former kind of a backup archive's manifest: the launcher's restore accepts it.</summary>
    public const string BackupKind = "didadeck-backup";

    /// <summary>The former settings key of the studio assistant's model, renamed by schema step 49.</summary>
    public const string StudioAssistantModelKey = "AgentDefaults:DeckAssistant:Model";

    /// <summary>The former settings key of the studio assistant's effort, renamed by schema step 49.</summary>
    public const string StudioAssistantEffortKey = "AgentDefaults:DeckAssistant:Effort";

    /// <summary>The former prefix of the studio assistant's topic settings, renamed by schema step 49.</summary>
    public const string StudioAssistantTopicPrefix = "DeckAssistant:Topic:";

    /// <summary>The prefix the studio's tools carried in the agent CLI (the MCP server's former name): schema step 49
    /// rewrites it in stored hooks and settings.</summary>
    public const string StudioToolPrefix = "mcp__didadeck__";

    /// <summary>The former sibling folder suffix of an item's worktree in a user's repository: an existing worktree there
    /// is still found (BridgeWorktreeService, AutopilotConcurrency); new ones go under the new suffix.</summary>
    public const string WorktreeContainerSuffix = ".didadeck-worktrees";

    /// <summary>The former prefix of an item worktree's own branch: an existing branch is reused for the same item.</summary>
    public const string WorktreeBranchPrefix = "didadeck/worktree/item-";

    /// <summary>The former stamp a provider creation carried (Jira label, Azure DevOps tag): a creation the old studio
    /// left in flight is still found by it, so it is never made twice.</summary>
    public const string TrackerStampPrefix = "didadeck-op-";

    /// <summary>The former <c>customizations</c> key of a project's devcontainer.json: the app runner reads both.</summary>
    public const string DevcontainerCustomization = "didadeck";

    /// <summary>The prefix of every former browser-storage key of the studio window: bootstrap.js copies each one to its
    /// new name once, before anything reads it (App.razor hands it the prefix).</summary>
    public const string BrowserStoragePrefix = "didadeck";

    /// <summary>
    /// The image label an OLD fravashi.exe reads the variant from when it updates the image alone; it refuses an image
    /// whose variant is not its own before it touches the running studio. The Dockerfile sets it to a variant no install
    /// has, which is how the bridge release's image is refused by an old executable (a test holds the two together).
    /// </summary>
    public const string ImageVariantLabel = "org.didadeck.variant";
}
