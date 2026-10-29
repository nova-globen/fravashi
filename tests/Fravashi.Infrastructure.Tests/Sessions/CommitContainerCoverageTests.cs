using AwesomeAssertions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 295: the text scanner that finds, from test SOURCES alone (never a test assembly, never a test's
/// constructor run), which tests a commit's changed paths touch and whether those tests skip in this container.
/// </summary>
public sealed class CommitContainerCoverageTests : IDisposable
{
    private readonly string _copy =
        Path.Combine(Path.GetTempPath(), "fravashi-coverage-" + Guid.NewGuid().ToString("N")[..8]);

    public CommitContainerCoverageTests()
    {
        Directory.CreateDirectory(Path.Combine(_copy, "tests", "Sample"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_copy, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_change_that_touches_no_test_source_names_nothing()
    {
        CommitContainerCoverage.Scan(_copy, ["src/widget.cs"]).Should().BeEmpty();
    }

    [Fact]
    public void No_copy_path_names_nothing()
    {
        CommitContainerCoverage.Scan(null, ["src/widget.cs"]).Should().BeEmpty();
        CommitContainerCoverage.Scan(string.Empty, ["src/widget.cs"]).Should().BeEmpty();
    }

    [Fact]
    public void A_windows_only_test_is_named_as_not_run_here()
    {
        Write("WindowsOnlyThingTests.cs", """
            using Xunit;

            namespace Sample;

            // Exercises src/widget.cs.
            public class WindowsOnlyThingTests
            {
                [WindowsOnlyFact]
                public void Some_windows_behavior()
                {
                }

                private sealed class WindowsOnlyFactAttribute : FactAttribute
                {
                    public WindowsOnlyFactAttribute() => Skip = !OperatingSystem.IsWindows() ? "Needs Windows; skips elsewhere." : null;
                }
            }
            """);

        // Container-shaped facts (never this process's own): on a Windows dev host OperatingSystem.IsWindows()
        // is true and this assertion would be backwards.
        CommitContainerCoverage.Scan(_copy, ["src/widget.cs"], FakeContainerFacts.Container).Should().Equal(
            "WindowsOnlyThingTests (Needs Windows; skips elsewhere.)");
    }

    [Fact]
    public void A_test_that_skips_on_windows_runs_here_and_is_named_as_nothing()
    {
        Write("PosixOnlyThingTests.cs", """
            using Xunit;

            namespace Sample;

            // Exercises src/gadget.cs.
            public class PosixOnlyThingTests
            {
                [PosixOnlyFact]
                public void Some_posix_behavior()
                {
                }

                private sealed class PosixOnlyFactAttribute : FactAttribute
                {
                    public PosixOnlyFactAttribute() => Skip = OperatingSystem.IsWindows() ? "POSIX only." : null;
                }
            }
            """);

        CommitContainerCoverage.Scan(_copy, ["src/gadget.cs"], FakeContainerFacts.Container).Should().BeEmpty();
    }

    [Fact]
    public void An_opt_in_live_tier_is_named_unless_its_variable_is_set()
    {
        // CLAUDE_ is a prefix AgentProcessEnvironment allows wholesale, unlike FRAVASHI_ (backlog 108), so
        // setting this one really does change what an agent's own test run would see.
        const string Variable = "CLAUDE_TEST_295_LIVE_FEATURE";
        Write("LiveThingTests.cs", $$"""
            using Xunit;

            namespace Sample;

            // Exercises src/feature.cs.
            public class LiveThingTests
            {
                private const string LiveEnvVar = "{{Variable}}";

                [LiveFact]
                public void Some_live_behavior()
                {
                }

                private sealed class LiveFactAttribute : FactAttribute
                {
                    public LiveFactAttribute()
                    {
                        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LiveThingTests.LiveEnvVar)))
                        {
                            Skip = $"Set {LiveThingTests.LiveEnvVar}=1 to run it.";
                        }
                    }
                }
            }
            """);

        var original = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, null);
            CommitContainerCoverage.Scan(_copy, ["src/feature.cs"]).Should().ContainSingle()
                .Which.Should().StartWith("LiveThingTests (Set CLAUDE_TEST_295_LIVE_FEATURE=1 to run it.");

            Environment.SetEnvironmentVariable(Variable, "1");
            CommitContainerCoverage.Scan(_copy, ["src/feature.cs"]).Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, original);
        }
    }

    [Fact]
    public void A_tier_whose_own_reason_names_the_studio_container_is_unclear_never_a_contradicting_skip()
    {
        // The real-browser live tier's own shape (AgentBrowserLiveTests, DesignCheckLiveTests and others): gated
        // on a FRAVASHI_ variable, which EnvVarIsSet always answers "not inherited" for (backlog 108), but the
        // reason text itself says the STUDIO CONTAINER is where this runs. Reporting "not run in the container"
        // would contradict the test's own documentation, so this is unclear instead.
        Write("BrowserLiveThingTests.cs", """
            using Xunit;

            namespace Sample;

            // Exercises src/browser-feature.cs.
            public class BrowserLiveThingTests
            {
                [BrowserLiveFact]
                public void Some_browser_behavior()
                {
                }

                private sealed class BrowserLiveFactAttribute : FactAttribute
                {
                    public BrowserLiveFactAttribute() => Skip = Environment.GetEnvironmentVariable("FRAVASHI_BROWSER_LIVE") == "1"
                        ? null
                        : "Set FRAVASHI_BROWSER_LIVE=1 where a Chromium is installed (the studio container) to drive a real browser.";
                }
            }
            """);

        CommitContainerCoverage.Scan(_copy, ["src/browser-feature.cs"]).Should().ContainSingle()
            .Which.Should().Be("BrowserLiveThingTests (unclear from source whether this runs in the container; check by hand)");
    }

    [Fact]
    public void A_statement_before_the_if_is_read_as_unclear_never_as_an_unconditional_skip()
    {
        // The real shape of RealEmbeddingModelFactAttribute: a local read of the environment variable before the
        // if, which a naive scan could mistake for an unconditional "Skip = ..." found anywhere in the text.
        Write("GatedThingTests.cs", """
            using Xunit;

            namespace Sample;

            // Exercises src/gated.cs.
            public class GatedThingTests
            {
                [GatedFact]
                public void Some_gated_behavior()
                {
                }

                private sealed class GatedFactAttribute : FactAttribute
                {
                    public GatedFactAttribute()
                    {
                        var directory = Environment.GetEnvironmentVariable("SOME_MODEL_DIR");
                        if (string.IsNullOrWhiteSpace(directory) || !File.Exists(directory))
                        {
                            Skip = "No model directory.";
                        }
                    }
                }
            }
            """);

        CommitContainerCoverage.Scan(_copy, ["src/gated.cs"]).Should().ContainSingle()
            .Which.Should().Be("GatedThingTests (unclear from source whether this runs in the container; check by hand)");
    }

    [Fact]
    public void Two_test_classes_in_one_file_sharing_one_attribute_are_both_named()
    {
        Write("TwoClassesTests.cs", """
            using Xunit;

            namespace Sample;

            // Exercises src/shared.cs.
            internal sealed class SharedWindowsOnlyFactAttribute : FactAttribute
            {
                public SharedWindowsOnlyFactAttribute() => Skip = !OperatingSystem.IsWindows() ? "Needs Windows." : null;
            }

            public sealed class FirstThingTests
            {
                [SharedWindowsOnlyFact]
                public void First_behavior()
                {
                }
            }

            public sealed class SecondThingTests
            {
                [SharedWindowsOnlyFact]
                public void Second_behavior()
                {
                }
            }
            """);

        CommitContainerCoverage.Scan(_copy, ["src/shared.cs"], FakeContainerFacts.Container).Should().Equal(
            "FirstThingTests (Needs Windows.)", "SecondThingTests (Needs Windows.)");
    }

    [Fact]
    public void An_unrelated_changed_path_names_nothing_even_with_a_blind_spot_test_present()
    {
        Write("WindowsOnlyThingTests.cs", """
            using Xunit;

            namespace Sample;

            // Exercises src/widget.cs.
            public class WindowsOnlyThingTests
            {
                [WindowsOnlyFact]
                public void Some_windows_behavior()
                {
                }

                private sealed class WindowsOnlyFactAttribute : FactAttribute
                {
                    public WindowsOnlyFactAttribute() => Skip = !OperatingSystem.IsWindows() ? "Needs Windows; skips elsewhere." : null;
                }
            }
            """);

        CommitContainerCoverage.Scan(_copy, ["docs/unrelated-topic.md"]).Should().BeEmpty();
    }

    // ---- the acceptance scenario: the real PowerShell helper and its real caller -----------------

    [Fact]
    public void A_diff_naming_webinstall_ps1_names_the_powershell_tests_not_run_here()
    {
        CopyRealFixture("Setup/RealPowerShell.cs");
        CopyRealFixture("Setup/WebInstallCurlRetryTests.cs");

        var found = CommitContainerCoverage.Scan(_copy, ["setup/webinstall/webinstall.ps1"], FakeContainerFacts.Container);

        found.Should().ContainSingle();
        found[0].Should().StartWith("WebInstallCurlRetryTests (").And.Contain("PowerShell");
    }

    [Fact]
    public void A_diff_naming_an_unrelated_path_names_nothing_even_with_the_real_powershell_tests_present()
    {
        CopyRealFixture("Setup/RealPowerShell.cs");
        CopyRealFixture("Setup/WebInstallCurlRetryTests.cs");

        CommitContainerCoverage.Scan(_copy, ["docs/CLAUDE.md"], FakeContainerFacts.Container).Should().BeEmpty();
    }

    [Fact]
    public void A_diff_naming_the_test_file_itself_names_its_own_class()
    {
        // #272's own follow-up touched only WebInstallCurlRetryTests.cs itself, which does not contain its own
        // basename as text, so matching by "the changed path is this very file" is needed too.
        CopyRealFixture("Setup/RealPowerShell.cs");
        CopyRealFixture("Setup/WebInstallCurlRetryTests.cs");

        var found = CommitContainerCoverage.Scan(
            _copy, ["tests/Sample/WebInstallCurlRetryTests.cs"], FakeContainerFacts.Container);

        found.Should().ContainSingle().Which.Should().StartWith("WebInstallCurlRetryTests (");
    }

    // ---- harness ------------------------------------------------------------------------------

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_copy, "tests", "Sample", name), content);

    private void CopyRealFixture(string relativeUnderInfrastructureTests)
    {
        var source = Path.Combine(
            RepositoryRoot(), "tests", "Fravashi.Infrastructure.Tests", relativeUnderInfrastructureTests.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(source).Should().BeTrue($"the real fixture {source} should exist");
        var destination = Path.Combine(_copy, "tests", "Sample", Path.GetFileName(source));
        File.Copy(source, destination, overwrite: true);
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
}
