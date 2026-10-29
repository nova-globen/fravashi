using Fravashi.Launcher;
using Fravashi.Launcher.Integrity;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// The <c>fravashi repin</c> recovery verb (backlog 101): after a legitimate LOCAL image change moved the tag, the
/// integrity gate refuses to start, and the message now names <c>fravashi repin</c> as the recovery. The verb resolves
/// the CURRENT local digest, shows old and new before writing, and re-pins ONLY on an explicit confirmation
/// (fail-closed without one) so tamper-evidence is preserved; it is a no-op when the digests already match.
/// </summary>
public sealed class RepinVerbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fravashi-repin-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDir;
    private readonly LauncherConfigStore _configStore;
    private readonly List<string> _log = new();

    public RepinVerbTests()
    {
        Directory.CreateDirectory(_dir);
        _dataDir = Path.Combine(_dir, "data");
        Directory.CreateDirectory(_dataDir);
        _configStore = new LauncherConfigStore(Path.Combine(_dir, "launcher.json"));
        _configStore.Save(new LauncherConfig { Port = 9090, DataDir = _dataDir, ContainerName = "fravashi-test" });
    }

    private LauncherApp Build(FakeContainerRuntime runtime, bool confirm) =>
        new(runtime, _configStore, launcherDirectory: _dir, log: _log.Add,
            openBrowser: _ => { }, isPortFree: _ => true, tray: new FakeTrayControl(),
            confirm: _ => confirm, defaultDataDir: () => _dataDir);

    private string RecordPath => Path.Combine(_dataDir, IntegrityRecordStore.FileName);

    [Fact]
    public void Repin_WithoutConfirmation_LeavesThePinUnchanged()
    {
        _configStore.Save(_configStore.Load()! with { PinnedImageDigest = "sha256:old" });
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, ImageDigestValue = "sha256:new" };

        Build(runtime, confirm: false).Repin().Should().Be(ExitCodes.Success);

        _configStore.Load()!.PinnedImageDigest.Should().Be("sha256:old", "a declined re-pin must not touch the pin");
        // Both digests are shown before the prompt (acceptance: names old and new).
        var text = string.Join('\n', _log);
        text.Should().Contain("sha256:old");
        text.Should().Contain("sha256:new");
    }

    [Fact]
    public void Repin_WithConfirmation_RepinsToTheResolvedDigest_AndRecordsTheShape()
    {
        _configStore.Save(_configStore.Load()! with { PinnedImageDigest = "sha256:old" });
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, ImageDigestValue = "sha256:new" };

        Build(runtime, confirm: true).Repin().Should().Be(ExitCodes.Success);

        _configStore.Load()!.PinnedImageDigest.Should().Be("sha256:new");
        // The expected-shape record is re-provisioned so the app-side integrity check matches the new digest.
        File.Exists(RecordPath).Should().BeTrue();
        var key = new IntegrityKeyStore(_dir).LoadOrCreate();
        var record = System.Text.Json.JsonSerializer.Deserialize<IntegrityRecord>(
            File.ReadAllText(RecordPath), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        IntegrityCanonical.Verify(record, key).Should().BeTrue();
        record.Components[0].ImageDigest.Should().Be("sha256:new");
    }

    [Fact]
    public void Repin_WhenDigestsAlreadyMatch_IsANoOp_AndNeverPrompts()
    {
        _configStore.Save(_configStore.Load()! with { PinnedImageDigest = "sha256:same" });
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, ImageDigestValue = "sha256:same" };
        var confirmCalls = 0;
        var app = new LauncherApp(runtime, _configStore, launcherDirectory: _dir, log: _log.Add,
            openBrowser: _ => { }, isPortFree: _ => true, tray: new FakeTrayControl(),
            confirm: _ => { confirmCalls++; return true; }, defaultDataDir: () => _dataDir);

        app.Repin().Should().Be(ExitCodes.Success);

        confirmCalls.Should().Be(0, "a matching digest needs no confirmation");
        _configStore.Load()!.PinnedImageDigest.Should().Be("sha256:same");
        string.Join('\n', _log).Should().Contain("nothing to re-pin");
    }

    [Fact]
    public void Repin_WhenNoPinYet_PinsWithoutPrompting()
    {
        // launcher.json seeded with no pin; this is ordinary trust-on-first-use, so it never overrides anything.
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, ImageDigestValue = "sha256:first" };
        var confirmCalls = 0;
        var app = new LauncherApp(runtime, _configStore, launcherDirectory: _dir, log: _log.Add,
            openBrowser: _ => { }, isPortFree: _ => true, tray: new FakeTrayControl(),
            confirm: _ => { confirmCalls++; return true; }, defaultDataDir: () => _dataDir);

        app.Repin().Should().Be(ExitCodes.Success);

        confirmCalls.Should().Be(0);
        _configStore.Load()!.PinnedImageDigest.Should().Be("sha256:first");
    }

    [Fact]
    public void Repin_WhenRuntimeUnavailable_ReturnsRuntimeUnavailable()
    {
        _configStore.Save(_configStore.Load()! with { PinnedImageDigest = "sha256:old" });
        var runtime = new FakeContainerRuntime { Available = false };

        Build(runtime, confirm: true).Repin().Should().Be(ExitCodes.RuntimeUnavailable);
        _configStore.Load()!.PinnedImageDigest.Should().Be("sha256:old");
    }

    [Fact]
    public void The_integrity_refusal_message_names_the_working_recovery_verb()
    {
        _configStore.Save(_configStore.Load()! with { PinnedImageDigest = "sha256:original" });
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, ContainerPresent = false, ImageDigestValue = "sha256:TAMPERED" };

        Build(runtime, confirm: false).Run().Should().Be(ExitCodes.IntegrityRefused);

        var text = string.Join('\n', _log);
        text.Should().Contain("fravashi repin");
        text.Should().NotContain("fravashi update", "the dead-end advice must not remain in the refusal");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
