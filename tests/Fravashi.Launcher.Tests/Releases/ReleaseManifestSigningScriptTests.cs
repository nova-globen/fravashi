using System.Diagnostics;
using System.Text;
using Fravashi.Launcher.Releases;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests.Releases;

/// <summary>
/// Backlog 242: a dry run of the very script release.yml signs the manifest with (<c>setup/release/sign-manifest.sh</c>),
/// against a throwaway keypair made by openssl the way the owner made the real one, and the signature it writes is
/// verified by the .NET code fravashi.exe runs. Needs bash and openssl, so it runs on Linux (CI, the studio container) and
/// skips on a Windows host with its reason.
/// </summary>
public sealed class ReleaseManifestSigningScriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fravashi-signing-" + Guid.NewGuid().ToString("N"));

    public ReleaseManifestSigningScriptTests() => Directory.CreateDirectory(_dir);

    private static string Script => Path.Combine(ReleaseManifestSignatureTests.RepositoryRoot(), "setup", "release", "sign-manifest.sh");

    private string In(string name) => Path.Combine(_dir, name);

    private (string Private, string Public) Keypair(string name)
    {
        Run("openssl", ["ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", In(name + ".key")]).Code.Should().Be(0);
        Run("openssl", ["ec", "-in", In(name + ".key"), "-pubout", "-out", In(name + ".pub")]).Code.Should().Be(0);
        return (File.ReadAllText(In(name + ".key")), In(name + ".pub"));
    }

    private void Manifest()
    {
        // Written the way release.yml's node writes it: JSON.stringify with a one-space indent, no trailing newline.
        File.WriteAllText(In("release-manifest.json"),
            "{\n \"schemaVersion\": 1,\n \"product\": \"fravashi\",\n \"version\": \"9.9.9\",\n \"critical\": false,\n \"variants\": []\n}",
            new UTF8Encoding(false));
    }

    [ToolsFact]
    public void The_scripts_signature_verifies_with_the_launchers_code_and_one_changed_byte_does_not()
    {
        var (key, pub) = Keypair("release");
        Manifest();

        var run = Run("bash", [Script, In("release-manifest.json"), In("release-manifest.sig"), pub], key);

        run.Code.Should().Be(0, run.Output);
        var bytes = File.ReadAllBytes(In("release-manifest.json"));
        var sidecar = File.ReadAllText(In("release-manifest.sig"));
        sidecar.TrimEnd('\n').Should().NotContain("\n", "the sidecar is base64 on one line");
        ReleaseManifestSignature.Verify(bytes, sidecar, File.ReadAllText(pub)).Should().Be(ManifestSignature.Verified);

        bytes[^3] ^= 0x01;
        ReleaseManifestSignature.Verify(bytes, sidecar, File.ReadAllText(pub)).Should().Be(ManifestSignature.BadSignature);
        Directory.GetFileSystemEntries(In("tmp")).Should().BeEmpty("the copy of the key the script wrote to its temp folder is gone");
    }

    [ToolsFact]
    public void A_secret_that_is_not_the_committed_keys_private_half_signs_nothing()
    {
        var (key, _) = Keypair("release");
        var (_, otherPub) = Keypair("other");
        Manifest();

        var run = Run("bash", [Script, In("release-manifest.json"), In("release-manifest.sig"), otherPub], key);

        run.Code.Should().Be(4, run.Output);
        run.Output.Should().Contain("is not the").And.NotContain("BEGIN EC PRIVATE KEY");
        File.Exists(In("release-manifest.sig")).Should().BeFalse();
    }

    [ToolsFact]
    public void No_secret_signs_nothing_and_says_so()
    {
        Manifest();
        File.WriteAllText(In("release-manifest.sig"), "stale");

        var run = Run("bash", [Script, In("release-manifest.json"), In("release-manifest.sig")], key: null);

        run.Code.Should().Be(3, run.Output);
        run.Output.Should().Contain("RELEASE_MANIFEST_SIGNING_KEY is not set");
        File.Exists(In("release-manifest.sig")).Should().BeFalse("a stale signature never survives a run that signed nothing");
    }

    [ToolsFact]
    public void The_committed_public_key_is_one_openssl_reads_as_P256()
    {
        var pem = Path.Combine(ReleaseManifestSignatureTests.RepositoryRoot(), "setup", "release", "manifest-signing-public.pem");

        var run = Run("openssl", ["pkey", "-pubin", "-in", pem, "-noout", "-text"]);

        run.Code.Should().Be(0, run.Output);
        run.Output.Should().Contain("prime256v1");
    }

    private (int Code, string Output) Run(string file, string[] arguments, string? key = null)
    {
        Directory.CreateDirectory(In("tmp"));
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // The script's own temp folder lands here, where the test can see that it was cleaned up.
        info.Environment["TMPDIR"] = In("tmp");
        info.Environment.Remove("RELEASE_MANIFEST_SIGNING_KEY");
        if (key is not null)
        {
            info.Environment["RELEASE_MANIFEST_SIGNING_KEY"] = key;
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Runs where bash and openssl both are, and says why it did not anywhere else.</summary>
    private sealed class ToolsFactAttribute : FactAttribute
    {
        public ToolsFactAttribute()
        {
            if (OperatingSystem.IsWindows())
            {
                Skip = "The signing script runs on the Linux release runner; on Windows bash may be WSL's and openssl absent. Run it on Linux (CI or the studio container).";
            }
            else if (!OnPath("bash") || !OnPath("openssl"))
            {
                Skip = "bash or openssl is not on PATH, so the signing script cannot be dry-run here.";
            }
        }

        private static bool OnPath(string name) =>
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(directory => File.Exists(Path.Combine(directory, name)));
    }
}
