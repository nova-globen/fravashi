using Fravashi.Launcher.Integrity;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests.Integrity;

/// <summary>The host-side HMAC key: generated once, persisted, reused (stable so a plain restart keeps verifying).</summary>
public sealed class IntegrityKeyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fravashi-key-" + Guid.NewGuid().ToString("N"));

    public IntegrityKeyStoreTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void First_load_creates_and_persists_a_key()
    {
        var store = new IntegrityKeyStore(_dir);
        var key = store.LoadOrCreate();

        key.Length.Should().Be(32);
        File.Exists(Path.Combine(_dir, IntegrityKeyStore.FileName)).Should().BeTrue();
    }

    [Fact]
    public void Reload_returns_the_same_key_bytes()
    {
        var first = new IntegrityKeyStore(_dir).LoadOrCreate();
        var second = new IntegrityKeyStore(_dir).LoadOrCreate();

        second.Should().Equal(first);
    }

    [Fact]
    public void A_corrupt_key_file_is_regenerated_not_thrown()
    {
        File.WriteAllText(Path.Combine(_dir, IntegrityKeyStore.FileName), "not-base64!!");

        var key = new IntegrityKeyStore(_dir).LoadOrCreate();

        key.Length.Should().Be(32);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
