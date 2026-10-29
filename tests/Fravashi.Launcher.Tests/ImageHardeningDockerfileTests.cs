using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// The no-setuid protection (ADR 0028, D4) is a claim the launcher makes from an image LABEL, so the label has to mean
/// what it says. These read the real Dockerfile and script: the strip runs as root and checks its own work, the label
/// comes only after it, and nothing runs as root after that.
/// </summary>
public sealed class ImageHardeningDockerfileTests
{
    private static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fravashi.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests run from inside the repository");
        return File.ReadAllText(Path.Combine(directory!.FullName, relative));
    }

    [Fact]
    public void The_label_is_stamped_only_after_the_strip_ran_and_before_the_switch_to_the_app_user()
    {
        var lines = RepositoryFile("Dockerfile").Split('\n').Select(l => l.Trim()).ToList();

        var strip = lines.FindIndex(l => l.StartsWith("RUN ", StringComparison.Ordinal) && l.Contains("strip-privileges.sh"));
        var label = lines.FindIndex(l => l.StartsWith("LABEL ", StringComparison.Ordinal) && l.Contains(ImageLabels.NoPrivilegeRaisingBinaries));
        var user = lines.FindLastIndex(l => l.StartsWith("USER ", StringComparison.Ordinal));

        strip.Should().BePositive("the image must run the strip");
        label.Should().BeGreaterThan(strip, "the label is the strip's receipt");
        user.Should().BeGreaterThan(label);
        lines[user].Should().Be("USER 1000:1000");
        lines.Skip(strip + 1).Take(user - strip - 1)
            .Should().NotContain(l => l.StartsWith("RUN ", StringComparison.Ordinal) || l.StartsWith("COPY ", StringComparison.Ordinal),
                "nothing may be added as root after the strip, or it could bring a setuid binary back");
    }

    [Fact]
    public void The_workspaces_mount_point_is_owned_by_the_app_user_before_the_switch_to_it()
    {
        // wslc and Docker have no ":U", so a fresh named volume takes its ownership from this directory (backlog 186).
        var lines = RepositoryFile("Dockerfile").Split('\n').Select(l => l.Trim()).ToList();

        var workspaces = lines.FindIndex(l => l.StartsWith("RUN ", StringComparison.Ordinal)
            && l.Contains("mkdir -p") && l.Contains("/workspaces")
            && l.Contains("chown -R 1000:1000") && l.EndsWith("/workspaces", StringComparison.Ordinal));
        var strip = lines.FindIndex(l => l.StartsWith("RUN ", StringComparison.Ordinal) && l.Contains("strip-privileges.sh"));
        var user = lines.FindLastIndex(l => l.StartsWith("USER ", StringComparison.Ordinal));

        workspaces.Should().BePositive("the image must create /workspaces owned by 1000:1000");
        workspaces.Should().BeLessThan(strip, "nothing may be added as root after the strip");
        workspaces.Should().BeLessThan(user, "the chown needs root");
        lines[user].Should().Be("USER 1000:1000");
    }

    [Fact]
    public void The_script_checks_its_own_work_and_fails_the_build_when_anything_remains()
    {
        var script = RepositoryFile(Path.Combine("setup", "strip-privileges.sh"));

        script.Should().Contain("set -eu");
        script.Should().Contain("find / -xdev -perm /6000 -type f -exec chmod a-s {} +");
        script.Should().Contain("security.capability");
        script.Should().Contain("exit 1", "a remaining setuid binary must fail the build, not pass with a warning");
    }
}
