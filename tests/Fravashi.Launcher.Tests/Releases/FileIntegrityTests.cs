using System.Security.Cryptography;
using System.Text;
using Fravashi.Launcher.Releases;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests.Releases;

/// <summary>
/// Covers the SHA-256 integrity check the update path (T5) verifies an asset with before loading it - the
/// launcher-side twin of the install scripts' shell check (ADR 0006).
/// </summary>
public sealed class FileIntegrityTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"fravashi-fi-{Guid.NewGuid():N}.bin");

    private string WriteContent(string content)
    {
        File.WriteAllText(_file, content);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public void Sha256Hex_MatchesTheKnownDigest()
    {
        var expected = WriteContent("fravashi release asset");
        FileIntegrity.Sha256Hex(_file).Should().Be(expected);
    }

    [Fact]
    public void VerifyFile_TrueOnMatch_ToleratesCaseAndSha256Prefix()
    {
        var expected = WriteContent("payload");
        FileIntegrity.VerifyFile(_file, expected).Should().BeTrue();
        FileIntegrity.VerifyFile(_file, expected.ToUpperInvariant()).Should().BeTrue();
        FileIntegrity.VerifyFile(_file, "sha256:" + expected).Should().BeTrue();
    }

    [Fact]
    public void VerifyFile_FalseOnMismatchOrEmptyExpected()
    {
        WriteContent("payload");
        FileIntegrity.VerifyFile(_file, new string('0', 64)).Should().BeFalse();
        FileIntegrity.VerifyFile(_file, "").Should().BeFalse();     // fail closed
        FileIntegrity.VerifyFile(_file, "   ").Should().BeFalse();
    }

    [Fact]
    public void Sha256Hex_OverBytes_MatchesFile()
    {
        var bytes = Encoding.UTF8.GetBytes("both paths agree");
        File.WriteAllBytes(_file, bytes);
        FileIntegrity.Sha256Hex(bytes).Should().Be(FileIntegrity.Sha256Hex(_file));
    }

    public void Dispose()
    {
        if (File.Exists(_file))
        {
            File.Delete(_file);
        }
    }
}
