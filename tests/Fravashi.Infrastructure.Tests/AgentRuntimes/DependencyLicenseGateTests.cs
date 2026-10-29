using System.Text.RegularExpressions;
using AwesomeAssertions;
using Fravashi.Application.AgentRuntimes;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentRuntimes;

/// <summary>
/// Backlog 73: the dependency licence gate used to cover only NuGet. The pre-commit hook now also gates
/// the npm agent CLIs the studio image bakes and ships (Claude Code / Codex / Copilot), against the same
/// <c>.githooks/licenses.allow</c> + <c>.githooks/licenses.approved</c> pair. That gate is a bash hook, so
/// it only fires for someone who commits with the hooks installed and touches the Dockerfile. This class
/// is the durable half: it reads the real Dockerfile and the real manifest the way <c>AgentCliPinTests</c>
/// does, and fails the build (which CI always runs) when a shipped npm CLI is not recorded and vetted, so
/// the gate's data cannot silently drift out from under it.
/// <para>
/// It deliberately does NOT re-implement the vetting: it asserts the manifest's recorded id is on the
/// approved list, which is exactly what the hook enforces at commit time. The two agree by construction.
/// </para>
/// </summary>
public class DependencyLicenseGateTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the test must be able to locate the repository root (Fravashi.sln)");
        return dir!.FullName;
    }

    private static string Read(string relative)
    {
        var path = Path.Combine(RepositoryRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue($"{relative} should exist at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The npm packages the studio image actually bakes and ships: every <c>name@${VERSION}</c> install in
    /// the Dockerfile. This is the SAME extraction the pre-commit hook runs, expressed in C#, so the two
    /// gates see the same set. If the image starts installing a package this regex does not catch, the hook
    /// would miss it too, so this is also the definition of what "shipped npm dependency" means to the gate.
    /// </summary>
    /// <summary>Model weights the image bakes, pinned in the Dockerfile the same way (backlog 40).</summary>
    private static readonly string[] BakedModels = ["all-MiniLM-L6-v2"];

    /// <summary>
    /// Tools the image installs from npm at build time without shipping them as agent runtimes (backlog 159): the
    /// Playwright driver that downloads the pinned browser. Its browser has its own recorded line, checked below.
    /// </summary>
    private static readonly string[] BakedTools = ["playwright"];

    private static IReadOnlyList<string> ShippedNpmPackages(string dockerfile) =>
        dockerfile.ReplaceLineEndings("\n").Split('\n')

            // A COMMENTED line is not an install, and saying otherwise gets the gate wrong in both
            // directions: it demands a manifest entry for something the image does not ship, and it reports
            // a package named only in prose as shipped. Found on 2026-09-22, when two installs were
            // commented out with their pins kept beside them (docs/v2.md section 1) and the gate went on
            // insisting the image shipped them.
            .Where(line => !line.TrimStart().StartsWith('#'))
            .SelectMany(line => Regex.Matches(line, @"(?<name>(@[A-Za-z0-9._-]+/)?[A-Za-z0-9._-]+)@\$\{[A-Za-z_]+\}"))
            .Select(m => m.Groups["name"].Value)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>Reads the <c>Id=license-id</c> lines from licenses.allow (comment and blank lines skipped).</summary>
    private static IReadOnlyDictionary<string, string> Manifest()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in Read(".githooks/licenses.allow").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('='))
            {
                continue;
            }

            var split = line.IndexOf('=');
            map[line[..split]] = line[(split + 1)..].Trim();
        }

        return map;
    }

    /// <summary>The bare SPDX / exception ids on licenses.approved (comment and blank lines skipped).</summary>
    private static IReadOnlySet<string> ApprovedIds() =>
        Read(".githooks/licenses.approved").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void The_image_ships_exactly_the_named_models_and_tools_and_nothing_unrecorded()
    {
        // The gate is only as honest as its notion of "what the image ships": everything it pins as name@${...}.
        // Backlog 40 added the embedding model to that pattern on purpose, so the hook gates its licence too, and
        // backlog 159 the Playwright driver. Each is named here, so a further thing pinned that way is a red build
        // until somebody says what it is.
        var shipped = ShippedNpmPackages(Read("Dockerfile"));
        var expected = BakedModels.Concat(BakedTools).OrderBy(n => n, StringComparer.Ordinal);

        shipped.Should().Equal(
            expected,
            "everything the image pins as name@${...} should be a model named in BakedModels or a tool named in "
            + "BakedTools, and vice versa");
    }

    [Fact]
    public void The_image_carries_no_agent_cli()
    {
        // Decided 2026-10-29: the published image does not redistribute an agent CLI. Update-on-launch installs the
        // catalog's pin from npm at the first start, under the vendor's own terms, so a CLI baked back in would ship
        // a third party's program in every release bundle again.
        var dockerfile = Read("Dockerfile").ReplaceLineEndings("\n").Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#'));

        foreach (var descriptor in AgentRuntimeCatalog.All)
        {
            dockerfile.Should().NotContain(
                line => line.Contains(descriptor.NpmPackage, StringComparison.Ordinal),
                $"the image must not install {descriptor.NpmPackage}; update-on-launch does, at the first start");
        }
    }

    [Fact]
    public void Every_shipped_npm_cli_has_a_recorded_and_approved_licence()
    {
        // This is the mechanical gate, enforced by the build rather than only by the commit hook: a shipped
        // CLI with no manifest line, or one whose recorded id is not on the approved list, fails here.
        var manifest = Manifest();
        var approved = ApprovedIds();

        foreach (var package in ShippedNpmPackages(Read("Dockerfile")))
        {
            manifest.Should().ContainKey(
                package,
                $"the studio image ships {package}, so its licence must be recorded in .githooks/licenses.allow "
                + "(verify the real npm package.json / LICENSE, do not guess)");

            approved.Should().Contain(
                manifest[package],
                $"{package} is recorded as '{manifest.GetValueOrDefault(package)}', which must be on "
                + ".githooks/licenses.approved: a permissive SPDX id, or an owner-recorded vendor exception");
        }
    }

    [Fact]
    public void The_nuget_gate_is_not_weakened_its_permissive_core_and_a_pinned_package_still_hold()
    {
        // Backlog 73 said in as many words: do not weaken the existing NuGet gate while extending it. The
        // permissive SPDX core must stay on the approved list, and a package known to be pinned must still
        // carry an approved entry, so a change that empties or guts either file trips here.
        var approved = ApprovedIds();
        approved.Should().Contain(["MIT", "Apache-2.0", "BSD-3-Clause"], "the permissive core must remain approved");

        var manifest = Manifest();
        manifest.Should().ContainKey("xunit", "a known-pinned NuGet package must still be recorded");
        approved.Should().Contain(manifest["xunit"], "and its recorded licence must still be approved");
    }

    [Fact]
    public void The_baked_browser_has_its_own_recorded_and_approved_licence_line()
    {
        // Backlog 159: the driver's line is checked with every other shipped npm package; the browser it downloads is
        // not an npm package the regex sees, so its line is held here, or it would be an unrecorded passenger.
        Manifest().Should().ContainKey("chromium-headless-shell");
        ApprovedIds().Should().Contain(Manifest()["chromium-headless-shell"]);
        Manifest().Should().ContainKey("Microsoft.Playwright");
        ApprovedIds().Should().Contain(Manifest()["Microsoft.Playwright"]);
    }
}
