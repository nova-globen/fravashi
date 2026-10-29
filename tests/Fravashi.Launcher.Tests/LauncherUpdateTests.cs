using Fravashi.Launcher;
using Fravashi.Launcher.Releases;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// The launcher's product-update wiring (T5 / ADR 0006): <c>fravashi update</c> confirm/critical handling, the
/// on-launch update-state write the app's lock reads, and folding <c>installed-release.json</c> into
/// launcher.json on first run. Driven with a fake manifest source + fake runtime - no network.
/// </summary>
public sealed class LauncherUpdateTests : IDisposable
{
    private readonly string _directory;
    private readonly LauncherConfigStore _configStore;
    private readonly List<string> _log = new();
    private readonly List<string> _downloads = new();

    public LauncherUpdateTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "fravashi-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _configStore = new LauncherConfigStore(Path.Combine(_directory, "launcher.json"));
    }

    private string DataDir => Path.Combine(_directory, "data");

    private void SeedConfig(string? version = "1.0.0") => _configStore.Save(new LauncherConfig
    {
        Port = 9090, DataDir = DataDir, ContainerName = "fravashi", Image = "fravashi:latest",
        Version = version, Variant = "full",
    });

    private LauncherApp Build(
        FakeContainerRuntime runtime,
        ReleaseManifest? manifest,
        bool confirm = true,
        byte[]? asset = null,
        BridgeLaunch? bridge = null,
        IResidentUpgrade? resident = null)
        => new(
            runtime,
            _configStore,
            launcherDirectory: _directory,
            log: _log.Add,
            openBrowser: _ => { },
            isPortFree: _ => true,
            tray: new FakeTrayControl(),
            manifestSourceFactory: _ => new FakeManifestSource(manifest),
            assetDownloader: new FakeAssetDownloader(asset, _downloads),
            confirm: _ => confirm,
            defaultDataDir: () => DataDir,
            bridge: bridge,
            residentUpgrade: resident);

    private static ReleaseManifest Manifest(string version, bool critical = false, string sha = "", long size = 0) => new()
    {
        Product = "fravashi", Version = version, Critical = critical, ReleaseNotesUrl = "https://example/notes",
        Variants =
        [
            new ReleaseVariant
            {
                Name = "full",
                Images =
                [
                    new ReleaseImage
                    {
                        Os = "linux", Arch = HostArch.Current, Asset = "img.tar", AssetUrl = "https://example/img.tar",
                        SizeBytes = size, Sha256 = sha, ImageRef = $"fravashi:{version}-full",
                    },
                ],
            },
        ],
    };

    private string StatePath => Path.Combine(DataDir, UpdateStateStore.FileName);

    [Fact]
    public void Update_WhenUpToDate_ReportsAndDoesNothing()
    {
        SeedConfig("9.9.9");
        var runtime = new FakeContainerRuntime { Available = true };

        Build(runtime, Manifest("1.0.0")).Update().Should().Be(ExitCodes.Success);

        runtime.LoadedTars.Should().BeEmpty();
        _log.Should().Contain(l => l.Contains("up to date"));
    }

    [Fact]
    public void Update_Available_Confirmed_AppliesTheUpdate()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true, ImageDigestAfterLoadValue = "sha256:newimage" };

        Build(runtime, Manifest("1.1.0", sha: FileIntegrity.Sha256Hex(content), size: content.Length), confirm: true, asset: content)
            .Update().Should().Be(ExitCodes.Success);

        runtime.LoadedTars.Should().ContainSingle();
        runtime.RunSpecs.Should().ContainSingle();
        _configStore.Load()!.Version.Should().Be("1.1.0");
    }

    [Fact]
    public void Update_RecreatesTheContainer_WithTheWindowToken()
    {
        // Backlog item 1 (docs/backlog.md): the update path rebuilt the run spec without the window
        // token, so a post-update container enforced only the UA marker (ADR 0017 weakened until the
        // next manual recreate). The updated container must carry the same persisted token.
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true, ImageDigestAfterLoadValue = "sha256:newimage" };

        Build(runtime, Manifest("1.1.0", sha: FileIntegrity.Sha256Hex(content), size: content.Length),
                confirm: true, asset: content)
            .Update().Should().Be(ExitCodes.Success);

        var spec = runtime.RunSpecs.Should().ContainSingle().Subject;
        spec.WindowToken.Should().NotBeNullOrWhiteSpace();
        spec.WindowToken.Should().Be(WindowAccessToken.TryRead(_directory));
    }

    // ---- backlog 160: an update creates the container a start would have created ---------------------------

    [Fact]
    public void Update_InsideAResidentDesktop_GivesTheNewContainerTheBridgeItHolds()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true, ImageDigestAfterLoadValue = "sha256:newimage" };

        Build(runtime, Manifest("1.1.0", sha: FileIntegrity.Sha256Hex(content), size: content.Length),
                confirm: true, asset: content,
                bridge: new BridgeLaunch("http://172.23.160.1:9091", "bridge-secret"))
            .Update().Should().Be(ExitCodes.Success);

        runtime.RunSpecs.Should().ContainSingle().Which.BridgeUrl.Should().Be("http://172.23.160.1:9091");
        _configStore.Load()!.BridgeUrl.Should().Be("http://172.23.160.1:9091");
    }

    // ---- backlog 157: the resident upgrade an agent asks for ------------------------------------------------------

    [Fact]
    public void CheckForUpgrade_says_what_the_channel_offers_and_why_not_when_nothing()
    {
        SeedConfig("1.0.0");
        var runtime = new FakeContainerRuntime { Available = true };

        var offered = Build(runtime, Manifest("1.1.0", critical: true)).CheckForUpgrade();
        var none = Build(runtime, manifest: null).CheckForUpgrade();

        offered.Should().Be(("1.0.0", "1.1.0", true, (string?)null));
        none.Offered.Should().BeNull();
        none.Said.Should().Contain("1.0.0");
    }

    [Fact]
    public void ApplyUpgrade_installs_only_the_approved_version_and_never_asks_at_a_console()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var refused = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true, ImageDigestAfterLoadValue = "sha256:newimage" };

        // The person approved 1.2.0; the channel moved to 1.1.0 since. Nothing is installed.
        Build(refused, Manifest("1.1.0", sha: FileIntegrity.Sha256Hex(content), size: content.Length), asset: content)
            .ApplyUpgrade("1.2.0").Should().Be((false, "The update channel now offers 1.1.0, not 1.2.0; nothing was installed."));
        refused.LoadedTars.Should().BeEmpty();

        // confirm: false would decline a console prompt; the resident path must not ask one.
        var runtime = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true, ImageDigestAfterLoadValue = "sha256:newimage" };
        var (applied, said) = Build(runtime, Manifest("1.1.0", sha: FileIntegrity.Sha256Hex(content), size: content.Length),
                confirm: false, asset: content,
                bridge: new BridgeLaunch("http://172.23.160.1:9091", "bridge-secret", UpgradeUrl: "http://172.23.160.1:9097", UpgradeToken: "u"))
            .ApplyUpgrade("1.1.0");

        applied.Should().BeTrue();
        said.Should().Contain("Updated to 1.1.0");
        var spec = runtime.RunSpecs.Should().ContainSingle().Subject;
        spec.BridgeUrl.Should().Be("http://172.23.160.1:9091", "the resident launcher holds the bridge");
        spec.UpgradeUrl.Should().Be("http://172.23.160.1:9097", "and the new studio can ask for the next upgrade");
        _configStore.Load()!.Version.Should().Be("1.1.0");
    }

    [Fact]
    public void Update_FromTheConsoleVerb_LeavesAConfigurationTheNextStartSeesAsStale()
    {
        // The unrecoverable half of backlog 160. `fravashi update` at a console holds no bridge, so its container
        // has none; before the fix it also SAVED the previous container's bridge record, so the next `fravashi`
        // start compared that, saw nothing stale, and started the bridgeless studio for ever.
        _configStore.Save(new LauncherConfig
        {
            Port = 9090, DataDir = DataDir, ContainerName = "fravashi", Image = "fravashi:latest",
            Version = "1.0.0", Variant = "full",
            BridgeUrl = "http://172.23.160.1:9091",
            BridgeTokenFingerprint = "0123456789" + "abcdef",
        });
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var console = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true, ImageDigestAfterLoadValue = "sha256:newimage" };

        Build(console, Manifest("1.1.0", sha: FileIntegrity.Sha256Hex(content), size: content.Length),
                confirm: true, asset: content)
            .Update().Should().Be(ExitCodes.Success);

        console.RunSpecs.Should().ContainSingle().Which.BridgeUrl.Should().BeNull();
        _configStore.Load()!.BridgeUrl.Should().BeNull("the record must describe the container that now exists");

        // Now the resident fravashi.exe starts, holding a bridge: the container it finds carries none, so it is
        // recreated with one instead of being started as it is. Same machine, so the image is the one the update loaded.
        var resident = new FakeContainerRuntime
        {
            Available = true, HasImage = true, ContainerPresent = true, ContainerIsRunning = true,
            ImageDigestValue = "sha256:newimage",
        };

        Build(resident, manifest: null, bridge: new BridgeLaunch("http://172.23.160.1:9091", "bridge-secret"))
            .Run().Should().Be(ExitCodes.Success);

        resident.Removed.Should().ContainSingle("a restart cannot change a container's environment");
        resident.RunSpecs.Should().ContainSingle().Which.BridgeToken.Should().Be("bridge-secret");
    }

    [Fact]
    public void Update_Available_Declined_DoesNotApply()
    {
        SeedConfig("1.0.0");
        var runtime = new FakeContainerRuntime { Available = true, ContainerPresent = true };

        Build(runtime, Manifest("1.1.0", sha: "abc"), confirm: false).Update().Should().Be(ExitCodes.Success);

        runtime.LoadedTars.Should().BeEmpty();
        _log.Should().Contain(l => l.Contains("cancelled"));
    }

    [Fact]
    public void Update_Critical_AppliesWithoutConfirmation()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true, ImageDigestAfterLoadValue = "sha256:newimage" };

        Build(runtime, Manifest("2.0.0", critical: true, sha: FileIntegrity.Sha256Hex(content), size: content.Length),
                confirm: false /* declining is ignored for critical */, asset: content)
            .Update().Should().Be(ExitCodes.Success);

        runtime.LoadedTars.Should().ContainSingle();
        _configStore.Load()!.Version.Should().Be("2.0.0");
    }

    [Fact]
    public void Update_Applied_RetiresTheSupersededImage()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = new FakeContainerRuntime
        {
            Available = true, HasImage = true, ContainerPresent = true, ContainerIsRunning = true,
            ImageDigestValue = "sha256:oldimage",
            ImageDigestAfterLoadValue = "sha256:newimage",
        };

        Build(runtime, Manifest("1.1.0", sha: FileIntegrity.Sha256Hex(content), size: content.Length), confirm: true, asset: content)
            .Update().Should().Be(ExitCodes.Success);

        // Housekeeping: the image the tag pointed at before the load is retired (non-forced) after the
        // swap, so an install holds one studio image instead of one more dangling <none> per update.
        runtime.RemovedImages.Should().ContainSingle().Which.Should().Be("sha256:oldimage");
        _configStore.Load()!.PinnedImageDigest.Should().Be("sha256:newimage");
    }

    [Fact]
    public void Update_Applied_KeepsASupersededImageThatSomebodyStillNames()
    {
        // Backlog 165: a backup tag was the old image's only name once `latest` had moved, and a remove by id on
        // podman takes an image's last name with it, so every back-out the release tools printed pointed at nothing.
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = new FakeContainerRuntime
        {
            Available = true, HasImage = true, ContainerPresent = true, ContainerIsRunning = true,
            ImageDigestValue = "sha256:oldimage",
            ImageDigestAfterLoadValue = "sha256:newimage",
        };
        runtime.ImageNamesValue["sha256:oldimage"] = ["localhost/fravashi:backup-1.0.0"];

        Build(runtime, Manifest("1.1.0", sha: FileIntegrity.Sha256Hex(content), size: content.Length), confirm: true, asset: content)
            .Update().Should().Be(ExitCodes.Success);

        runtime.RemovedImages.Should().BeEmpty();
        _log.Should().Contain(l => l.Contains("Kept the superseded image") && l.Contains("localhost/fravashi:backup-1.0.0"));
    }

    [Fact]
    public void Update_WhenTheLoadDidNotMoveTheName_RefusesAndLeavesTheStudioRunning()
    {
        // Backlog 190. No ImageDigestAfterLoadValue: after the load the name still resolves to the image the studio
        // runs now. That used to end in "Updated to 1.1.0" over the old image and a studio its own integrity check
        // stopped; now nothing is swapped and the update says why.
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = new FakeContainerRuntime
        {
            Available = true, HasImage = true, ContainerPresent = true, ContainerIsRunning = true,
            ImageDigestValue = "sha256:sameimage",
        };

        Build(runtime, Manifest("1.1.0", sha: FileIntegrity.Sha256Hex(content), size: content.Length), confirm: true, asset: content)
            .Update().Should().NotBe(ExitCodes.Success);

        runtime.Stopped.Should().BeEmpty();
        runtime.Removed.Should().BeEmpty();
        runtime.RunSpecs.Should().BeEmpty();
        runtime.RemovedImages.Should().BeEmpty();
        _configStore.Load()!.Version.Should().Be("1.0.0");
        _log.Should().Contain(l => l.Contains("still names the image the studio runs now") && l.Contains("keeps running"));
    }

    // ---- backlog 190: the tarball's name is not the launcher's on wslc and Docker ----------------------------------

    /// <summary>
    /// An engine with Moby naming, as wslc 3.0.1 and Docker were measured: the running studio was created from the
    /// image <c>fravashi:latest</c> names, and a load prints and moves ONLY <c>localhost/fravashi:latest</c>.
    /// </summary>
    private static FakeContainerRuntime MobyNamed()
    {
        var runtime = new FakeContainerRuntime
        {
            Available = true, HasImage = true, ContainerPresent = true, ContainerIsRunning = true,
            ContainerImageIdValue = "sha256:old",
            LoadedImageId = "sha256:new",
        };
        runtime.Names["fravashi:latest"] = "sha256:old";
        runtime.Names["localhost/fravashi:latest"] = "sha256:old";
        runtime.LoadNames.Add("localhost/fravashi:latest");
        return runtime;
    }

    /// <summary>The shape the real channel serves: no imageRef, so the container is created from launcher.json's name.</summary>
    private static ReleaseManifest ChannelManifest(string version, byte[] content) => Manifest(
        version, sha: FileIntegrity.Sha256Hex(content), size: content.Length) with
    {
        Variants =
        [
            new ReleaseVariant
            {
                Name = "full",
                Images =
                [
                    new ReleaseImage
                    {
                        Os = "linux", Arch = HostArch.Current, Asset = "img.tar", AssetUrl = "https://example/img.tar",
                        SizeBytes = content.Length, Sha256 = FileIntegrity.Sha256Hex(content),
                    },
                ],
            },
        ],
    };

    [Fact]
    public void Update_on_moby_naming_creates_the_studio_from_the_image_it_just_loaded()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = MobyNamed();
        runtime.VersionLabels["sha256:new"] = "1.1.0";

        Build(runtime, ChannelManifest("1.1.0", content), confirm: true, asset: content)
            .Update().Should().Be(ExitCodes.Success);

        runtime.Tags.Should().Equal(("localhost/fravashi:latest", "fravashi:latest"));
        runtime.RunSpecs.Should().ContainSingle().Which.Image.Should().Be("fravashi:latest");
        runtime.CreatedFromImageIds.Should().Equal("sha256:new");
        var saved = _configStore.Load()!;
        saved.Version.Should().Be("1.1.0");
        saved.PinnedImageDigest.Should().Be("sha256:new", "the pin, the version and the running image agree");
        runtime.RemovedImages.Should().Equal("sha256:old");
    }

    [Fact]
    public void Update_on_moby_naming_refuses_before_touching_the_studio_when_the_name_cannot_be_moved()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = MobyNamed();
        runtime.RefusesTags = true; // so nothing moves fravashi:latest off the running image

        Build(runtime, ChannelManifest("1.1.0", content), confirm: true, asset: content)
            .Update().Should().NotBe(ExitCodes.Success);

        runtime.Stopped.Should().BeEmpty();
        runtime.Removed.Should().BeEmpty();
        runtime.RunSpecs.Should().BeEmpty();
        _configStore.Load()!.Version.Should().Be("1.0.0");
        _log.Should().Contain(l => l.Contains("still names") && l.Contains("keeps running at version 1.0.0"));
    }

    [Fact]
    public void Update_refuses_an_image_whose_label_is_not_the_version_it_was_offered_as()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = MobyNamed();
        runtime.VersionLabels["sha256:new"] = "1.0.9";

        Build(runtime, ChannelManifest("1.1.0", content), confirm: true, asset: content)
            .Update().Should().NotBe(ExitCodes.Success);

        runtime.RunSpecs.Should().BeEmpty();
        _log.Should().Contain(l => l.Contains("says it is version 1.0.9, not 1.1.0"));
    }

    [Fact]
    public void Update_on_podman_naming_tags_nothing_because_both_names_are_one_reference()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = MobyNamed();
        runtime.PodmanNaming = true;
        runtime.Names.Clear();
        runtime.Names["fravashi:latest"] = "sha256:old";

        Build(runtime, ChannelManifest("1.1.0", content), confirm: true, asset: content)
            .Update().Should().Be(ExitCodes.Success);

        runtime.Tags.Should().BeEmpty();
        runtime.CreatedFromImageIds.Should().Equal("sha256:new");
    }

    // ---- backlog 256: a refused update leaves the install's name where it was -------------------------------------

    /// <summary>The <see cref="MobyNamed"/> engine with podman's naming: the load itself moves <c>fravashi:latest</c>.</summary>
    private static FakeContainerRuntime PodmanNamed()
    {
        var runtime = MobyNamed();
        runtime.PodmanNaming = true;
        runtime.Names.Clear();
        runtime.Names["fravashi:latest"] = "sha256:old";
        return runtime;
    }

    /// <summary>
    /// The start after a refusal: the person stopped fravashi.exe and starts it again. It must start the container it has,
    /// not recreate it from the refused image, and record nothing the refused image says.
    /// </summary>
    private void TheNextStartRunsThePreviousImage(FakeContainerRuntime runtime)
    {
        runtime.ContainerIsRunning = false;
        _log.Clear();

        Build(runtime, manifest: null).Run().Should().Be(ExitCodes.Success);

        runtime.Started.Should().Contain("fravashi");
        runtime.Removed.Should().BeEmpty();
        runtime.RunSpecs.Should().BeEmpty();
        var saved = _configStore.Load()!;
        saved.Version.Should().Be("1.0.0");
        saved.Variant.Should().Be("full");
        saved.PinnedImageDigest.Should().Be("sha256:old");
    }

    [Fact]
    public void A_label_refusal_on_moby_naming_is_decided_before_the_install_name_moves()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = MobyNamed();
        runtime.VersionLabels["sha256:old"] = "1.0.0";
        runtime.VersionLabels["sha256:new"] = "1.0.9";

        Build(runtime, ChannelManifest("1.1.0", content), confirm: true, asset: content)
            .Update().Should().NotBe(ExitCodes.Success);

        runtime.Tags.Should().BeEmpty("nothing may name the refused image fravashi:latest");
        runtime.ResolveImageDigest("fravashi:latest").Should().Be("sha256:old");
        runtime.RunSpecs.Should().BeEmpty();
        runtime.RemovedImages.Should().Equal("localhost/fravashi:latest", "sha256:new");
        _log.Should().Contain(l => l.Contains("says it is version 1.0.9, not 1.1.0") && l.Contains("Nothing was swapped"));
        _log.Should().Contain(l => l.Contains("Removed the refused image sha256:new"));

        TheNextStartRunsThePreviousImage(runtime);
    }

    [Fact]
    public void A_label_refusal_on_podman_naming_names_the_previous_image_again_and_retires_the_refused_one()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = PodmanNamed();
        runtime.VersionLabels["sha256:old"] = "1.0.0";
        runtime.VersionLabels["sha256:new"] = "1.0.9";

        Build(runtime, ChannelManifest("1.1.0", content), confirm: true, asset: content)
            .Update().Should().NotBe(ExitCodes.Success);

        runtime.Tags.Should().Equal(("sha256:old", "fravashi:latest"));
        runtime.ResolveImageDigest("fravashi:latest").Should().Be("sha256:old");
        runtime.RunSpecs.Should().BeEmpty();
        runtime.RemovedImages.Should().Equal(["sha256:new"], "the load's name is the install's name on podman and must stay");
        _log.Should().Contain(l => l.Contains("named the previous image sha256:old fravashi:latest again"));
        _log.Should().Contain(l => l.Contains("says it is version 1.0.9, not 1.1.0") && l.Contains("Nothing was swapped"));

        TheNextStartRunsThePreviousImage(runtime);
    }

    [Fact]
    public void A_refusal_whose_name_cannot_be_put_back_says_so_and_never_that_the_studio_is_unchanged()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = PodmanNamed();
        runtime.RefusesTags = true;
        runtime.VersionLabels["sha256:old"] = "1.0.0";
        runtime.VersionLabels["sha256:new"] = "1.0.9";

        Build(runtime, ChannelManifest("1.1.0", content), confirm: true, asset: content)
            .Update().Should().NotBe(ExitCodes.Success);

        runtime.RunSpecs.Should().BeEmpty();
        runtime.RemovedImages.Should().BeEmpty("the refused image is what the install's name still names");
        _log.Should().Contain(l => l.Contains("says it is version 1.0.9, not 1.1.0")
            && l.Contains("could not be named back to the image it named before the update (sha256:old)")
            && l.Contains("fake tag sha256:old fravashi:latest"));
        _log.Should().NotContain(l => l.Contains("Nothing was swapped") || l.Contains("keeps running"));
        _configStore.Load()!.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void An_image_of_another_variant_is_refused_before_the_install_name_moves()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = MobyNamed();
        runtime.VersionLabels["sha256:old"] = "1.0.0";
        runtime.VersionLabels["sha256:new"] = "1.1.0";
        runtime.VariantLabels["sha256:old"] = "full";
        runtime.VariantLabels["sha256:new"] = "corp-broker";

        Build(runtime, ChannelManifest("1.1.0", content), confirm: true, asset: content)
            .Update().Should().NotBe(ExitCodes.Success);

        runtime.Tags.Should().BeEmpty();
        runtime.ResolveImageDigest("fravashi:latest").Should().Be("sha256:old");
        runtime.RunSpecs.Should().BeEmpty();
        _log.Should().Contain(l => l.Contains("says it is the corp-broker variant, not full") && l.Contains("Nothing was swapped"));

        TheNextStartRunsThePreviousImage(runtime);
    }

    [Fact]
    public void An_image_of_the_install_variant_is_installed_as_before()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = MobyNamed();
        runtime.VersionLabels["sha256:new"] = "1.1.0";
        runtime.VariantLabels["sha256:old"] = "full";
        runtime.VariantLabels["sha256:new"] = "full";

        Build(runtime, ChannelManifest("1.1.0", content), confirm: true, asset: content)
            .Update().Should().Be(ExitCodes.Success);

        runtime.CreatedFromImageIds.Should().Equal("sha256:new");
        runtime.RemovedImages.Should().Equal("sha256:old");
    }

    [Fact]
    public void Start_from_a_tarball_beside_the_host_names_the_loaded_image_on_moby_naming()
    {
        // The other place a tarball is loaded: a missing image on start, with fravashi-image.tar beside fravashi.exe.
        SeedConfig("1.0.0");
        File.WriteAllText(Path.Combine(_directory, LauncherDefaults.ImageTarFileName), "fake tar");
        var runtime = new FakeContainerRuntime { Available = true, LoadedImageId = "sha256:new" };
        runtime.LoadNames.Add("localhost/fravashi:latest");

        Build(runtime, manifest: null).Run().Should().Be(ExitCodes.Success);

        runtime.Tags.Should().Equal(("localhost/fravashi:latest", "fravashi:latest"));
        runtime.CreatedFromImageIds.Should().Equal("sha256:new");
    }

    [Fact]
    public void Start_from_a_tarball_that_names_something_else_entirely_says_so_instead_of_running_nothing()
    {
        SeedConfig("1.0.0");
        File.WriteAllText(Path.Combine(_directory, LauncherDefaults.ImageTarFileName), "fake tar");
        var runtime = new FakeContainerRuntime { Available = true };
        runtime.LoadNames.Add("localhost/fravashi:a");
        runtime.LoadNames.Add("localhost/fravashi:b");

        Build(runtime, manifest: null).Run().Should().Be(ExitCodes.ImageMissing);

        runtime.RunSpecs.Should().BeEmpty();
        _log.Should().Contain(l => l.Contains("still names no image") && l.Contains("localhost/fravashi:a"));
    }

    [Fact]
    public void Run_WritesUpdateStateForTheAppLock()
    {
        SeedConfig("1.0.0");
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true };

        Build(runtime, Manifest("3.0.0", critical: true, sha: "x")).Run().Should().Be(ExitCodes.Success);

        File.Exists(StatePath).Should().BeTrue();
        File.ReadAllText(StatePath).Should().Contain("Critical").And.Contain("3.0.0");
        _log.Should().Contain(l => l.Contains("locked"));
    }

    [Fact]
    public void FoldsInstalledReleaseIntoLauncherJsonOnFirstRun()
    {
        // launcher.json exists (port/data-dir chosen) but has no version; the installer's marker supplies it.
        _configStore.Save(new LauncherConfig { Port = 9090, DataDir = DataDir, ContainerName = "fravashi", Version = null });
        File.WriteAllText(
            Path.Combine(_directory, "installed-release.json"),
            """{ "version": "1.5.0", "variant": "full", "imageRef": "fravashi:1.5.0-full" }""");
        var runtime = new FakeContainerRuntime { Available = true };

        Build(runtime, manifest: null).Status().Should().Be(ExitCodes.Success);

        _configStore.Load()!.Version.Should().Be("1.5.0");
        _configStore.Load()!.Variant.Should().Be("full");
    }

    [Fact]
    public void Status_WhenLauncherJsonHasNoVersion_ReportsTheImageLabelVersion_NotUnknown()
    {
        // launcher.json has no version yet and no installer marker is present; status should read the running
        // image's baked org.fravashi.version label rather than printing "unknown" (the cosmetic fix).
        _configStore.Save(new LauncherConfig
        {
            Port = 9090, DataDir = DataDir, ContainerName = "fravashi", Image = "fravashi:latest", Version = null,
        });
        var runtime = new FakeContainerRuntime
        {
            Available = true, HasImage = true, ContainerPresent = true, ContainerIsRunning = true,
            ImageReleaseVersionValue = "0.3.0",
        };

        Build(runtime, manifest: null).Status().Should().Be(ExitCodes.Success);

        _log.Should().Contain(l => l.Contains("Version", StringComparison.Ordinal) && l.Contains("0.3.0", StringComparison.Ordinal));
        _log.Should().NotContain(l => l.Contains("Version", StringComparison.Ordinal) && l.Contains("unknown", StringComparison.Ordinal));
    }

    [Theory]
    // Seen on the owner's machine 2026-10-02 after installing v0.9.44 with the bundle's installer: launcher.json had
    // no version (nothing writes installed-release.json), so the start offered "an update to 0.9.44".
    [InlineData(null)]
    // And the stale case: a reinstall of a newer image never moved a version recorded earlier.
    [InlineData("1.0.0")]
    public void The_installed_image_says_which_version_is_installed_so_the_same_release_is_not_offered(string? recorded)
    {
        _configStore.Save(new LauncherConfig
        {
            Port = 9090, DataDir = DataDir, ContainerName = "fravashi", Image = "fravashi:latest", Version = recorded,
        });
        var runtime = new FakeContainerRuntime
        {
            Available = true, HasImage = true, ContainerPresent = true, ContainerIsRunning = true,
            ImageReleaseVersionValue = "1.1.0",
        };

        Build(runtime, Manifest("1.1.0")).Run().Should().Be(ExitCodes.Success);

        _log.Should().NotContain(l => l.Contains("is available", StringComparison.Ordinal));
        File.ReadAllText(StatePath).Should().Contain("None");
        _configStore.Load()!.Version.Should().Be("1.1.0");
    }

    [Fact]
    public void The_expected_shape_written_at_the_first_start_after_an_install_names_the_new_version()
    {
        // Seen 2026-10-02 (v0.9.46 at 01:17 and a dev image at 14:12 UTC): the record the studio's integrity check
        // compares against was written from launcher.json's version BEFORE the new image's version was recorded, so it
        // expected the old one, observed the new one in the container, and the studio failed closed and stopped itself.
        _configStore.Save(new LauncherConfig
        {
            Port = 9090, DataDir = DataDir, ContainerName = "fravashi", Image = "fravashi:latest", Version = "1.0.0",
        });
        var runtime = new FakeContainerRuntime
        {
            Available = true, HasImage = true, ContainerPresent = false, ImageReleaseVersionValue = "1.1.0",
        };

        Build(runtime, manifest: null).Run().Should().Be(ExitCodes.Success);

        var record = File.ReadAllText(Path.Combine(DataDir, Fravashi.Launcher.Integrity.IntegrityRecordStore.FileName));
        record.Should().Contain("1.1.0").And.NotContain("\"1.0.0\"");
    }

    [Fact]
    public void Recording_the_installed_version_keeps_what_creating_the_container_just_recorded()
    {
        // Seen installing v0.9.46, 2026-10-02: the start created the container (which saved the upgrade endpoint it
        // baked), then recorded the version read from the image by saving the configuration it had loaded BEFORE the
        // container existed, so launcher.json lost the upgrade record and the next start would recreate for nothing.
        _configStore.Save(new LauncherConfig
        {
            Port = 9090, DataDir = DataDir, ContainerName = "fravashi", Image = "fravashi:latest", Version = "1.0.0",
        });
        var runtime = new FakeContainerRuntime
        {
            Available = true, HasImage = true, ContainerPresent = false, ImageReleaseVersionValue = "1.1.0",
        };

        Build(runtime, manifest: null, bridge: new BridgeLaunch(
                "http://172.23.160.1:9091", "bridge-secret", "http://172.23.160.1:9095", "runner-secret", "http://172.23.160.1:9097", "upgrade-secret"))
            .Run().Should().Be(ExitCodes.Success);

        var saved = _configStore.Load()!;
        saved.Version.Should().Be("1.1.0");
        saved.UpgradeBaked.Should().StartWith("http://172.23.160.1:9097|", "the container carries it, so the record must say so");
        saved.AppRunnerBaked.Should().StartWith("http://172.23.160.1:9095|");
        saved.BridgeUrl.Should().Be("http://172.23.160.1:9091");
    }

    // ---- backlog 251: the installed image says which variant it is ------------------------------------------------

    private string IntegrityRecordText =>
        File.ReadAllText(Path.Combine(DataDir, Fravashi.Launcher.Integrity.IntegrityRecordStore.FileName));

    private static FakeContainerRuntime ImageSaying(string? variant, bool containerPresent = false) => new()
    {
        Available = true, HasImage = true, ContainerPresent = containerPresent, ImageReleaseVersionValue = "0.9.64",
        ImageVariantValue = variant,
    };

    [Fact]
    public void A_corp_image_on_a_fresh_data_directory_is_recorded_as_its_own_variant_and_the_expected_shape_names_it()
    {
        // Seen on the test VM 2026-10-08: a hand-installed corp-broker bundle was recorded as "full", the expected
        // shape said FRAVASHI_VARIANT=full, and the studio's integrity check stopped the container ten seconds in.
        var runtime = ImageSaying("corp-broker");

        Build(runtime, manifest: null).Run().Should().Be(ExitCodes.Success);

        _configStore.Load()!.Variant.Should().Be("corp-broker");
        IntegrityRecordText.Should().Contain("\"corp-broker\"").And.NotContain("\"full\"");
        _log.Should().Contain("Recorded installed variant corp-broker (read from the installed image) in launcher.json.");

        _log.Clear();
        Build(runtime, manifest: null).Status().Should().Be(ExitCodes.Success);
        _log.Should().Contain(l => l.Contains("Version", StringComparison.Ordinal) && l.Contains("0.9.64 (corp-broker)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_recorded_full_variant_under_a_corp_image_is_corrected_logged_and_the_container_recreated()
    {
        _configStore.Save(new LauncherConfig
        {
            Port = 9090, DataDir = DataDir, ContainerName = "fravashi", Image = "fravashi:latest",
            Version = "0.9.64", Variant = "full",
        });
        var runtime = ImageSaying("corp-broker", containerPresent: true);

        Build(runtime, manifest: null).Run().Should().Be(ExitCodes.Success);

        _configStore.Load()!.Variant.Should().Be("corp-broker");
        _log.Should().Contain(
            "Recorded installed variant corp-broker (read from the installed image) in launcher.json, which said full.");
        _log.Should().Contain(l => l.Contains("created expecting the full variant", StringComparison.Ordinal)
            && l.Contains("corp-broker", StringComparison.Ordinal));
        runtime.Removed.Should().Equal("fravashi");
        runtime.RunSpecs.Should().ContainSingle();
        runtime.Started.Should().BeEmpty("it is created afresh against the corrected record, not restarted");
        IntegrityRecordText.Should().Contain("\"corp-broker\"").And.NotContain("\"full\"");
    }

    [Theory]
    [InlineData("corp-vpn", "corp-vpn")]
    [InlineData(null, "full")]
    public void An_image_with_no_variant_label_keeps_the_recorded_variant_then_full(string? recorded, string expected)
    {
        _configStore.Save(new LauncherConfig
        {
            Port = 9090, DataDir = DataDir, ContainerName = "fravashi", Image = "fravashi:latest",
            Version = "0.9.64", Variant = recorded,
        });
        var runtime = ImageSaying(variant: null, containerPresent: true);

        Build(runtime, manifest: null).Run().Should().Be(ExitCodes.Success);

        _configStore.Load()!.Variant.Should().Be(recorded);
        IntegrityRecordText.Should().Contain($"\"{expected}\"");
        runtime.Removed.Should().BeEmpty();
        runtime.Started.Should().Equal("fravashi");
        _log.Should().NotContain(l => l.Contains("installed variant", StringComparison.Ordinal));
    }

    [Fact]
    public void A_full_image_under_a_full_record_changes_nothing()
    {
        _configStore.Save(new LauncherConfig
        {
            Port = 9090, DataDir = DataDir, ContainerName = "fravashi", Image = "fravashi:latest",
            Version = "0.9.64", Variant = "full",
        });
        var runtime = ImageSaying("full", containerPresent: true);

        Build(runtime, manifest: null).Run().Should().Be(ExitCodes.Success);

        _configStore.Load()!.Variant.Should().Be("full");
        runtime.Removed.Should().BeEmpty();
        runtime.RunSpecs.Should().BeEmpty();
        runtime.Started.Should().Equal("fravashi");
        _log.Should().NotContain(l => l.Contains("installed variant", StringComparison.Ordinal)
            || l.Contains("expecting the", StringComparison.Ordinal));
    }

    private static ReleaseManifest TwoVariantManifest(string version, byte[] content)
    {
        ReleaseVariant Variant(string name) => new()
        {
            Name = name,
            Images =
            [
                new ReleaseImage
                {
                    Os = "linux", Arch = HostArch.Current, Asset = $"{name}.tar", AssetUrl = $"https://example/{name}.tar",
                    SizeBytes = content.Length, Sha256 = FileIntegrity.Sha256Hex(content),
                },
            ],
        };

        return Manifest(version) with { Variants = [Variant("full"), Variant("corp-broker")] };
    }

    [Fact]
    public void The_update_check_of_a_corp_install_never_offers_the_full_image()
    {
        // launcher.json says full (as the VM's did); the image says corp-broker, and only the full variant is published.
        SeedConfig("0.9.64");
        var runtime = ImageSaying("corp-broker", containerPresent: true);

        var offered = Build(runtime, Manifest("0.9.65")).CheckForUpgrade();

        offered.Offered.Should().BeNull("the channel has no corp-broker image, and the full one is not this install's");
        _configStore.Load()!.Variant.Should().Be("corp-broker");
    }

    [Fact]
    public void An_update_of_a_corp_install_downloads_the_corp_image()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = MobyNamed();
        runtime.VersionLabels["sha256:old"] = "1.0.0";
        runtime.VariantLabels["sha256:old"] = "corp-broker";
        runtime.VersionLabels["sha256:new"] = "1.1.0";
        runtime.VariantLabels["sha256:new"] = "corp-broker";

        Build(runtime, TwoVariantManifest("1.1.0", content), confirm: true, asset: content)
            .Update().Should().Be(ExitCodes.Success);

        _downloads.Should().Equal("https://example/corp-broker.tar");
        _configStore.Load()!.Variant.Should().Be("corp-broker");
        _configStore.Load()!.Version.Should().Be("1.1.0");
    }

    // ---- backlog 157: a console `fravashi update` hands the update to the running fravashi.exe ----------------------------

    [Fact]
    public void Update_hands_a_confirmed_update_to_the_running_host_and_touches_nothing_itself()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true, ImageDigestAfterLoadValue = "sha256:newimage" };
        var resident = new FakeResident(new ResidentUpgradeResult(true, "Updated to 1.1.0."));

        Build(runtime, Manifest("1.1.0", sha: FileIntegrity.Sha256Hex(content), size: content.Length), asset: content, resident: resident)
            .Update().Should().Be(ExitCodes.Success);

        resident.Asked.Should().Equal("1.1.0");
        runtime.LoadedTars.Should().BeEmpty("the running fravashi.exe did the work, with the bridge it holds");
        runtime.RunSpecs.Should().BeEmpty();
        _log.Should().Contain("Updated to 1.1.0.");
    }

    [Fact]
    public void Update_says_what_the_running_host_refused_and_does_not_do_it_behind_its_back()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true, ImageDigestAfterLoadValue = "sha256:newimage" };
        var resident = new FakeResident(new ResidentUpgradeResult(false, "An upgrade to 1.1.0 is already running."));

        Build(runtime, Manifest("1.1.0", sha: FileIntegrity.Sha256Hex(content), size: content.Length), asset: content, resident: resident)
            .Update().Should().Be(ExitCodes.ImageMissing);

        runtime.LoadedTars.Should().BeEmpty();
        _log.Should().Contain("An upgrade to 1.1.0 is already running.");
    }

    [Fact]
    public void Update_with_no_running_host_updates_by_itself_as_before()
    {
        SeedConfig("1.0.0");
        var content = System.Text.Encoding.UTF8.GetBytes("tar");
        var runtime = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true, ImageDigestAfterLoadValue = "sha256:newimage" };

        Build(runtime, Manifest("1.1.0", sha: FileIntegrity.Sha256Hex(content), size: content.Length), asset: content, resident: new FakeResident(null))
            .Update().Should().Be(ExitCodes.Success);

        runtime.LoadedTars.Should().ContainSingle();
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeResident(ResidentUpgradeResult? answer) : IResidentUpgrade
    {
        public List<string> Asked { get; } = [];

        public ResidentUpgradeResult? TryApply(string version, Action<string> log)
        {
            Asked.Add(version);
            return answer;
        }
    }

    /// <summary>A null manifest is a channel that could not be checked (backlog 230), never "nothing newer".</summary>
    private sealed class FakeManifestSource(ReleaseManifest? manifest) : IReleaseManifestSource
    {
        public Task<ManifestFetch> FetchAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(manifest is null ? ManifestFetch.Failed("name not resolved") : ManifestFetch.Read(manifest));
    }

    private sealed class FakeAssetDownloader(byte[]? content, List<string>? urls = null) : IReleaseAssetDownloader
    {
        public Task<AssetDownload> DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
        {
            urls?.Add(url);
            if (content is null)
            {
                return Task.FromResult(AssetDownload.Failed("it answered HTTP 404", retryable: false));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllBytes(destinationPath, content);
            return Task.FromResult(AssetDownload.Done(destinationPath));
        }
    }
}
