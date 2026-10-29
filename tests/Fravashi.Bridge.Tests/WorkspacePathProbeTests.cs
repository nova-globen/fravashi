using AwesomeAssertions;
using Xunit;

namespace Fravashi.Bridge.Tests;

/// <summary>
/// The probe exists so nothing can act on "Directory.Exists said false" as if it meant "deleted".
/// The transient cases below are the ones that cost a user their workspace: a mapped network drive
/// that is not mapped yet, an external disk that is not plugged in.
/// </summary>
public sealed class WorkspacePathProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fravashi-probe-tests", Guid.NewGuid().ToString("N"));

    public WorkspacePathProbeTests() => Directory.CreateDirectory(Path.Combine(_root, "widgets"));

    [Fact]
    public void An_existing_folder_is_available()
        => WorkspacePathProbe.Probe(Path.Combine(_root, "widgets")).Should().Be(WorkspacePathState.Available);

    [Fact]
    public void A_folder_deleted_from_a_readable_parent_is_missing()
    {
        var gone = Path.Combine(_root, "gone");

        WorkspacePathProbe.Probe(gone).Should().Be(WorkspacePathState.Missing);
    }

    [Fact]
    public void A_folder_on_a_volume_that_is_not_attached_is_unreachable_not_missing()
    {
        // The reviewer's scenario: Z:\repos\widgets on a drive that is not mapped yet (equally an
        // external SSD that is not plugged in). Directory.Exists is false for BOTH this and a
        // deleted folder, which is exactly the confusion that used to prune the grant.
        var unattached = UnattachedRoot();
        if (unattached is null)
        {
            // Every drive letter in use is not a scenario worth failing the suite over; the
            // deleted-folder case above still pins the other half of the distinction.
            return;
        }

        var path = Path.Combine(unattached, "repos", "widgets");
        Directory.Exists(path).Should().BeFalse("the whole point is that Exists cannot tell these apart");

        WorkspacePathProbe.Probe(path).Should().Be(WorkspacePathState.Unreachable);
    }

    [Fact]
    public void A_share_on_a_host_that_does_not_answer_is_unreachable_not_missing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A UNC root that resolves to nothing: the server is off, the VPN is down, the share was
        // unpublished. None of that is evidence that the folder was deleted.
        var path = @"\\fravashi-no-such-host.invalid\share\repos\widgets";

        WorkspacePathProbe.Probe(path).Should().Be(WorkspacePathState.Unreachable);
    }

    [Fact]
    public void A_folder_whose_parent_is_itself_unattached_is_unreachable()
    {
        var unattached = UnattachedRoot();
        if (unattached is null)
        {
            return;
        }

        WorkspacePathProbe.Probe(unattached).Should().Be(WorkspacePathState.Unreachable);
    }

    [Fact]
    public void A_file_standing_where_a_parent_folder_belongs_is_missing()
    {
        var file = Path.Combine(_root, "not-a-folder.txt");
        File.WriteAllText(file, "x");

        WorkspacePathProbe.Probe(Path.Combine(file, "widgets")).Should().Be(WorkspacePathState.Missing);
    }

    /// <summary>A drive letter that is genuinely not attached on this machine, or null.</summary>
    private static string? UnattachedRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!used.Contains(letter) && !Directory.Exists($"{letter}:\\"))
            {
                return $"{letter}:\\";
            }
        }

        return null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
