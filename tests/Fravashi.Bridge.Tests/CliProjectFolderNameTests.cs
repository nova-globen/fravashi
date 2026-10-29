using AwesomeAssertions;
using Xunit;

namespace Fravashi.Bridge.Tests;

/// <summary>
/// Backlog 43. The expected names are not predictions: the first four are folder names that really exist
/// under the owner's host CLI projects folder (read by name only), and the rest were produced by running the
/// encoder function copied out of the CLI bundle itself (claude.exe 2.1.270) under node, because no folder
/// with a space or a long path existed to read.
/// </summary>
public sealed class CliProjectFolderNameTests
{
    [Theory]
    // Observed on the host.
    [InlineData(@"C:\Users\ada\Data\Repositories\NovaGloben\Fravashi", "C--Users-ada-Data-Repositories-NovaGloben-Fravashi")]
    [InlineData(@"C:\Users\ada\.claude\jobs\0a1b70d5\tmp\probe25", "C--Users-ada--claude-jobs-0a1b70d5-tmp-probe25")]
    [InlineData(@"c:\Users\ada\Data\Repositories\NovaGloben\Stomme", "c--Users-ada-Data-Repositories-NovaGloben-Stomme")]
    [InlineData(@"C:\Users\ada\Data\Repositories\NovaGloben\Sites\Example-Site", "C--Users-ada-Data-Repositories-NovaGloben-Sites-Example-Site")]
    // Produced by the CLI's own encoder.
    [InlineData(@"C:\Users\Jane Doe\My Repo", "C--Users-Jane-Doe-My-Repo")]
    [InlineData(@"D:\Arbete\Projektår\x", "D--Arbete-Projekt-r-x")]
    [InlineData("/home/jane/my repo", "-home-jane-my-repo")]
    public void Encodes_the_way_the_cli_names_its_folders(string workingDirectory, string expected)
        => CliProjectFolderName.Encode(workingDirectory).Should().Be(expected);

    [Fact]
    public void A_space_and_a_drive_colon_each_become_a_hyphen()
        => CliProjectFolderName.Encode(@"E:\a b").Should().Be("E--a-b");

    [Fact]
    public void A_path_over_200_characters_is_cut_and_suffixed_with_the_cli_hash()
    {
        var path = @"C:\Users\Jane Doe\" + string.Concat(Enumerable.Repeat(@"very-long-folder-name\", 10)) + "repo";

        CliProjectFolderName.Encode(path).Should().Be(
            "C--Users-Jane-Doe-very-long-folder-name-very-long-folder-name-very-long-folder-name-very-long-folder-name-"
            + "very-long-folder-name-very-long-folder-name-very-long-folder-name-very-long-folder-name-very-l-dczf0b");
    }
}
