using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Setup;

/// <summary>
/// Tracker item #370 step (b): <c>setup/webinstall/webinstall.ps1</c> installs from the update channel. It
/// reads <c>release-manifest.json</c>, verifies <c>release-manifest.sig</c> beside it against the release key
/// every fravashi.exe embeds (the launcher's <c>ReleaseManifestSignature</c>: ECDSA P-256, SHA-256, a DER signature in
/// base64), and downloads only what that signed manifest names. These drive the engine's own functions, dot-sourced with
/// <c>FRAVASHI_WEBINSTALL_FUNCTIONS_ONLY=1</c>, against keys and manifests made here: under whichever PowerShell is on
/// PATH, and the signature check also under Windows PowerShell 5.1 where it exists, because that is what the setup EXE
/// runs the engine with and its .NET Framework has none of the PEM, SPKI or DER helpers newer .NET has.
/// </summary>
public sealed class WebInstallDownloadsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fravashi-webinstall-dl-" + Guid.NewGuid().ToString("N"));

    public WebInstallDownloadsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        (dir is not null).Should().BeTrue("the test must be able to locate the repository root (Fravashi.sln)");
        return dir!.FullName;
    }

    private static string ScriptPath => Path.Combine(RepositoryRoot(), "setup", "webinstall", "webinstall.ps1");

    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Run(string body, string? exeName = null)
    {
        var command = "$ErrorActionPreference = 'Stop'\n$env:FRAVASHI_WEBINSTALL_FUNCTIONS_ONLY = '1'\n. " + Quote(ScriptPath) + "\n" + body;
        var (stdout, stderr) = RealPowerShell.Run(command, TimeSpan.FromSeconds(60), exeName: exeName);
        stdout.Should().NotBeNull($"{exeName ?? RealPowerShell.ExeName} should run the dot-sourced functions; stderr: {stderr}");
        stdout.Should().Contain("END", $"the command should run to its last line; stderr: {stderr}");
        return stdout!.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_written_into_the_engine_is_the_committed_release_key()
    {
        var script = File.ReadAllText(ScriptPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        var committed = File.ReadAllText(Path.Combine(RepositoryRoot(), "setup", "release", "manifest-signing-public.pem"))
            .Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        script.Should().Contain("$script:ReleaseKeyPem = @\"\n" + committed + "\n\"@",
            "the engine must trust exactly the key every fravashi.exe embeds, or it refuses every real manifest");
    }

    [RealPowerShellFact]
    public void A_manifest_signed_by_the_release_key_verifies_and_anything_else_is_refused() =>
        SignatureCheck(exeName: null);

    [RealPowerShellFact]
    public void The_signature_check_works_under_windows_powershell_51_which_the_setup_exe_uses()
    {
        if (!OperatingSystem.IsWindows() || !RealPowerShell.IsAvailable("powershell"))
        {
            // Not a skip attribute: the fact above covers the check wherever any PowerShell is; this one adds 5.1 only.
            return;
        }

        SignatureCheck(exeName: "powershell");
    }

    private void SignatureCheck(string? exeName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = Path.Combine(_dir, "key.pem");
        File.WriteAllText(pem, key.ExportSubjectPublicKeyInfoPem());
        var manifest = Encoding.UTF8.GetBytes("{\n \"schemaVersion\": 1,\n \"product\": \"fravashi\",\n \"version\": \"0.9.99\"\n}");
        var manifestPath = Path.Combine(_dir, "release-manifest.json");
        File.WriteAllBytes(manifestPath, manifest);
        var tampered = Path.Combine(_dir, "tampered.json");
        File.WriteAllBytes(tampered, Encoding.UTF8.GetBytes("{\n \"schemaVersion\": 1,\n \"product\": \"fravashi\",\n \"version\": \"0.9.98\"\n}"));

        // Twenty signatures, so an r or s with its top bit set (a leading 00 in DER) and one shorter than 32 bytes both
        // turn up; sign-manifest.sh writes base64 on one line and a newline after it.
        var signatures = Enumerable.Range(0, 20)
            .Select(_ => Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) + "\n")
            .ToArray();
        var foreign = Convert.ToBase64String(other.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var sigFile = Path.Combine(_dir, "sigs.txt");
        File.WriteAllLines(sigFile, signatures.Select(s => s.TrimEnd('\n')));

        var output = Run($$"""
            $pem = [IO.File]::ReadAllText({{Quote(pem)}})
            $bytes = [IO.File]::ReadAllBytes({{Quote(manifestPath)}})
            $ok = 0
            foreach ($sig in [IO.File]::ReadAllLines({{Quote(sigFile)}})) { if ((Test-ReleaseManifestSignature $bytes ($sig + "`n") $pem) -eq 'Verified') { $ok++ } }
            "GOOD $ok"
            $first = @([IO.File]::ReadAllLines({{Quote(sigFile)}}))[0]
            "TAMPERED $(Test-ReleaseManifestSignature ([IO.File]::ReadAllBytes({{Quote(tampered)}})) $first $pem)"
            "FOREIGN $(Test-ReleaseManifestSignature $bytes '{{foreign}}' $pem)"
            "REALKEY $(Test-ReleaseManifestSignature $bytes $first)"
            "NONE $(Test-ReleaseManifestSignature $bytes $null $pem)"
            "GARBAGE $(Test-ReleaseManifestSignature $bytes 'not base64 at all!' $pem)"
            "EMPTYDER $(Test-ReleaseManifestSignature $bytes 'MAA=' $pem)"
            try { Get-P256PublicKeyPoint "-----BEGIN PUBLIC KEY-----`nAAAA`n-----END PUBLIC KEY-----" | Out-Null; 'ODDKEY accepted' } catch { "ODDKEY $($_.Exception.Message)" }
            'END'
            """, exeName);

        output.Should().Contain("GOOD 20", "every signature the release key makes over these bytes verifies");
        output.Should().Contain("TAMPERED BadSignature");
        output.Should().Contain("FOREIGN BadSignature", "a signature by any other key is not the release key's");
        output.Should().Contain("REALKEY BadSignature", "by default the committed release key is the one trusted, and the test key is not it");
        output.Should().Contain("NONE Unsigned");
        output.Should().Contain("GARBAGE BadSignature");
        output.Should().Contain("EMPTYDER BadSignature");
        output.Should().Contain("ODDKEY the release key is not a P-256 public key in SPKI form.");
    }

    private const string Manifest = """
        {
         "schemaVersion": 1,
         "product": "fravashi",
         "version": "0.9.99",
         "releasedAt": "2026-10-25T12:00:00.000Z",
         "releaseNotesUrl": "https://forge.example.test/nova-globen/fravashi/releases/tag/v0.9.99",
         "critical": false,
         "variants": [
          {
           "name": "full",
           "displayName": "Fravashi",
           "images": [
            {
             "os": "linux",
             "arch": "amd64",
             "asset": "fravashi-image-amd64.tar.gz",
             "sizeBytes": 701234567,
             "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
             "imageDigest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
             "partUrls": [
              "https://updates.example.test/fravashi/0.9.99/fravashi-image-amd64.tar.part00.gz",
              "https://updates.example.test/fravashi/0.9.99/fravashi-image-amd64.tar.part01.gz"
             ]
            }
           ],
           "launchers": [
            {
             "rid": "win-x64",
             "asset": "fravashi-win-x64.tar.gz",
             "assetUrl": "https://updates.example.test/fravashi/0.9.99/fravashi-win-x64.tar.gz",
             "sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
            }
           ]
          }
         ]
        }
        """;

    [RealPowerShellFact]
    public void The_downloads_come_from_the_signed_manifest_and_a_wrong_one_is_refused_in_words()
    {
        var good = Path.Combine(_dir, "good.json");
        File.WriteAllBytes(good, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Manifest)]);
        var cases = new Dictionary<string, string>
        {
            ["product"] = Manifest.Replace("\"product\": \"fravashi\"", "\"product\": \"other\"", StringComparison.Ordinal),
            ["http"] = Manifest.Replace("https://updates.example.test/fravashi/0.9.99/fravashi-win-x64", "http://updates.example.test/fravashi/0.9.99/fravashi-win-x64", StringComparison.Ordinal),
            ["path"] = Manifest.Replace("\"asset\": \"fravashi-win-x64.tar.gz\"", "\"asset\": \"..\\\\evil.tar.gz\"", StringComparison.Ordinal),
            ["nolauncher"] = Manifest.Replace("\"rid\": \"win-x64\"", "\"rid\": \"linux-x64\"", StringComparison.Ordinal),
            ["whole"] = System.Text.RegularExpressions.Regex.Replace(
                Manifest, @"""partUrls"": \[[^\]]*\]","\"assetUrl\": \"https://updates.example.test/fravashi/0.9.99/fravashi-image-amd64.tar.gz\""),
        };
        foreach (var (name, text) in cases)
        {
            text.Should().NotBe(Manifest, $"the {name} case must change the manifest");
            File.WriteAllText(Path.Combine(_dir, name + ".json"), text);
        }

        string Load(string name) => $"(ConvertFrom-ManifestBytes ([IO.File]::ReadAllBytes({Quote(Path.Combine(_dir, name + ".json"))})))";
        var output = Run($$"""
            $p = Select-ManifestDownloads {{Load("good")}} 'amd64' ''
            "LATEST $($p.Version) L=$($p.Launcher.Asset) $($p.Launcher.Sliced) $($p.Launcher.Urls -join ',') I=$($p.Image.Asset) $($p.Image.Sliced) $($p.Image.Urls.Count) $($p.Image.Urls[1]) $($p.Image.Sha256)"
            $p = Select-ManifestDownloads {{Load("good")}} 'amd64' '0.9.99'
            "PINNED $($p.Version)"
            $w = Select-ManifestDownloads {{Load("whole")}} 'amd64' ''
            "WHOLE $($w.Image.Sliced) $($w.Image.Urls -join ',')"
            foreach ($c in @(
                @('VERSION', {{Load("good")}}, 'amd64', '0.9.98'),
                @('ARM', {{Load("good")}}, 'arm64', ''),
                @('PRODUCT', {{Load("product")}}, 'amd64', ''),
                @('HTTP', {{Load("http")}}, 'amd64', ''),
                @('PATH', {{Load("path")}}, 'amd64', ''),
                @('NOLAUNCHER', {{Load("nolauncher")}}, 'amd64', ''))) {
                try { Select-ManifestDownloads $c[1] $c[2] $c[3] | Out-Null; "$($c[0]) accepted" } catch { "$($c[0]) $($_.Exception.Message)" }
            }
            $sums = Read-Sha256Sums @("{{new string('a', 64)}}  fravashi-image-amd64.tar.gz", "{{new string('C', 64)}} *fravashi-win-x64.tar.gz", '', 'junk')
            "SUMS $($sums.Count) $($sums['fravashi-win-x64.tar.gz'])"
            'END'
            """);

        output.Should().Contain(
            "LATEST 0.9.99 L=fravashi-win-x64.tar.gz False https://updates.example.test/fravashi/0.9.99/fravashi-win-x64.tar.gz " +
            "I=fravashi-image-amd64.tar.gz True 2 https://updates.example.test/fravashi/0.9.99/fravashi-image-amd64.tar.part01.gz " + new string('a', 64),
            "the names and URLs are the manifest's, a byte-order mark is left out, the slices kept in order");
        output.Should().Contain("PINNED 0.9.99");
        output.Should().Contain("WHOLE False https://updates.example.test/fravashi/0.9.99/fravashi-image-amd64.tar.gz");
        output.Should().Contain("VERSION the manifest in the 0.9.98 folder describes version 0.9.99; refusing it.",
            "an older or other signed manifest served in a version's folder is not that version");
        output.Should().Contain("ARM release 0.9.99 has no linux/arm64 image");
        output.Should().Contain("PRODUCT the manifest is not Nova Globen Fravashi's.");
        output.Should().Contain("HTTP the manifest's URL for fravashi-win-x64.tar.gz is not https");
        output.Should().Contain("PATH the manifest names an asset");
        output.Should().Contain("NOLAUNCHER release 0.9.99 lists no win-x64 launcher to download.");
        output.Should().Contain($"SUMS 2 {new string('c', 64)}", "sha256sum's binary-mode star is dropped and the hash compared lower case");
    }

    [Fact]
    public void The_engine_installs_only_from_the_update_channel_through_the_signed_manifest()
    {
        var script = File.ReadAllText(ScriptPath);
        // Infra D47: the forge retired, and with it the engine's -Tag, its tokens and its release lookup.
        script.Should().NotContain("$Tag").And.NotContain("$Token").And.NotContain("Invoke-RestMethod");
        // Infra C36: production's update channel; the setup EXE passes its own build's channel (EngineCommand).
        script.Should().Contain("[string]$DownloadBase = \"https://nova-globen.se/fravashi/updates\",");
        script.Should().Contain("Invoke-PublicDownload \"$folderUrl/release-manifest.json\"");
        script.Should().Contain("Invoke-PublicDownload \"$folderUrl/release-manifest.sig\"");
        script.Should().Contain("Invoke-PublicDownload \"$folderUrl/SHA256SUMS.txt\"");
        script.Should().Contain("if ($verdict -ne \"Verified\")", "nothing a manifest names is downloaded before its signature verified");

        script.Should().Contain("if ($NotFound -and $status -eq 404) { Die $NotFound }", "a 404 for the manifest is said as 'nothing published yet'");
    }
}
