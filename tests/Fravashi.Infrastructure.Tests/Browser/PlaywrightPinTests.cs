using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Browser;

/// <summary>
/// Backlog 159: the browser the image bakes is the one the .NET package drives. The Dockerfile's PLAYWRIGHT_VERSION
/// decides which Chromium revision is downloaded (that version's browsers.json), and the NuGet package's driver looks
/// for exactly that revision; two different numbers would build an image whose browser the studio cannot find, and
/// the failure would only show in the container. So the two are read from the real files and held equal, and the
/// driver wrapper the image points PLAYWRIGHT_NODEJS_PATH at is held to what it must do.
/// </summary>
public sealed class PlaywrightPinTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fravashi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative));

    [Fact]
    public void The_image_installs_the_browser_of_the_same_version_the_package_drives()
    {
        var package = Regex.Match(Read("Directory.Packages.props"), "<PackageVersion Include=\"Microsoft.Playwright\" Version=\"(?<v>[^\"]+)\"");
        var arg = Regex.Match(Read("Dockerfile"), @"(?m)^ARG PLAYWRIGHT_VERSION=(?<v>\S+)\s*$");

        package.Success.Should().BeTrue("Directory.Packages.props pins Microsoft.Playwright");
        arg.Success.Should().BeTrue("the Dockerfile pins PLAYWRIGHT_VERSION");
        arg.Groups["v"].Value.Should().Be(package.Groups["v"].Value);
    }

    [Fact]
    public void The_image_points_the_package_at_the_clean_environment_wrapper_and_strips_the_bundled_node()
    {
        var dockerfile = Read("Dockerfile");
        dockerfile.Should().Contain("ENV PLAYWRIGHT_NODEJS_PATH=/usr/local/bin/fravashi-playwright-node");
        dockerfile.Should().Contain("COPY --chmod=0555 setup/playwright-node.sh /usr/local/bin/fravashi-playwright-node");
        dockerfile.Should().Contain("rm -rf /app/publish/.playwright/node");
        dockerfile.Should().Contain("ENV PLAYWRIGHT_BROWSERS_PATH=/opt/fravashi/ms-playwright");
    }

    [Fact]
    public void The_wrapper_starts_the_driver_from_an_empty_environment_that_names_no_studio_secret()
    {
        var wrapper = Read("setup/playwright-node.sh").ReplaceLineEndings("\n");

        wrapper.Should().StartWith("#!/bin/sh\n");
        wrapper.Should().Contain("exec env -i", "the driver, and the Chromium it starts, inherit nothing of the studio");
        wrapper.Should().NotContain("FRAVASHI_", "no studio variable is passed on, not even by name");
        wrapper.Should().NotContain("ANTHROPIC_").And.NotContain("CLAUDE_");
        wrapper.Should().Contain("PW_LANG_NAME=").And.Contain("PLAYWRIGHT_BROWSERS_PATH=");
    }
}
