using AwesomeAssertions;
using Xunit;

namespace Fravashi.WebSetup.Tests;

public sealed class EngineProgressTests
{
    [Fact]
    public void Every_line_the_progress_reads_is_still_printed_by_the_engine()
    {
        // The wizard follows the engine's own narration. A reworded Write-Host must turn this red rather than
        // leave the progress page stuck on a stage that never arrives.
        var engine = Repository.EngineText;

        engine.Should().Contain($"Write-Host \"{EngineProgress.LookingUpLine}\"");
        engine.Should().Contain("Write-Host \"Downloading $What ...\"");
        engine.Should().Contain("Write-Host \"Release: $($plan.Version) (manifest signature verified against the release key)\"");
        engine.Should().Contain("Write-Host \"  reassembled $($Entry.Asset) from $($Entry.Urls.Count) slices\"");
        engine.Should().Contain("Write-Host \"  verified sha256 $($entry.Asset)\"");
        engine.Should().Contain($"Write-Host \"{EngineProgress.UnpackingLine}\"");
        engine.Should().Contain("Write-Host \"Decompressing $(Split-Path $imagePath -Leaf) ...\"");
        engine.Should().Contain($"Write-Host \"{EngineProgress.HandOffLine}\"");
        engine.Should().Contain("Write-Host \"Nova Globen Fravashi $($plan.Version) is installed.\"");
        engine.Should().Contain("Write-Host \"FAILED: $_\"");
        engine.Should().Contain("'--progress-bar'", "the bar on stderr is what the percentage is read from");
        engine.Should().Contain("(\"" + EngineProgress.NotPublishedMarker + "$folderUrl yet (HTTP 404). Check the version.\")");
        engine.Should().Contain("if ($NotFound -and $status -eq 404) { Die $NotFound }",
            "the marker is printed for a 404 only, never for a DNS failure or a refused signature");
    }

    [Fact]
    public void An_install_moves_through_the_stages_in_order()
    {
        var progress = new EngineProgress();
        var seen = new List<EngineStage>();
        foreach (var line in new[]
                 {
                     "Nova Globen Fravashi web installer starting (version: latest, from https://updates.example.test/fravashi) ...",
                     "Looking up the release ...",
                     "Downloading release-manifest.json ...",
                     "Downloading release-manifest.sig ...",
                     "Release: 0.9.99 (manifest signature verified against the release key)",
                     "Downloading fravashi-win-x64.tar.gz ...",
                     "Downloading fravashi-image-amd64.tar.gz (slice 1 of 14) ...",
                     "  reassembled fravashi-image-amd64.tar.gz from 14 slices",
                     "  verified sha256 fravashi-win-x64.tar.gz",
                     "Unpacking the launcher bundle ...",
                     "",
                     "Handing off to the release's install-windows.ps1 ...",
                     "Nova Globen Fravashi 0.9.99 is installed.",
                 })
        {
            progress.OnOutputLine(line);
            if (seen.Count == 0 || seen[^1] != progress.Stage)
            {
                seen.Add(progress.Stage);
            }
        }

        seen.Should().Equal(
            EngineStage.Starting, EngineStage.LookingUp, EngineStage.Downloading, EngineStage.Verifying,
            EngineStage.Unpacking, EngineStage.Installing, EngineStage.Installed);
        progress.ResolvedTag.Should().Be("0.9.99");
        progress.NotPublished.Should().BeFalse();
        progress.LastLine.Should().Be("Nova Globen Fravashi 0.9.99 is installed.", "an empty line never replaces the last one");
    }

    [Fact]
    public void A_download_line_names_the_file()
    {
        var progress = new EngineProgress();

        progress.OnOutputLine("Downloading fravashi-image-amd64.tar.gz (slice 3 of 14) ...");

        progress.Stage.Should().Be(EngineStage.Downloading);
        progress.Item.Should().Be("fravashi-image-amd64.tar.gz (slice 3 of 14)");
    }

    [Fact]
    public void Nothing_published_on_the_update_channel_is_noticed_from_the_engines_reason()
    {
        var progress = new EngineProgress();

        progress.OnOutputLine("Could not download release-manifest.json from https://updates.example.test/fravashi/latest/release-manifest.json (curl exit 6).");
        progress.NotPublished.Should().BeFalse("a failure that is not a 404 is not 'nothing published'");

        progress.OnOutputLine("No Fravashi release is published at https://updates.example.test/fravashi/latest yet (HTTP 404). Check the version.");
        progress.NotPublished.Should().BeTrue();
    }

    [Fact]
    public void An_engine_from_before_the_rename_saying_its_former_name_is_read_as_installed()
    {
        var progress = new EngineProgress();

        progress.OnOutputLine($"{Fravashi.Domain.FormerNames.Product} v0.9.80 is installed.");

        progress.Stage.Should().Be(EngineStage.Installed);
    }

    [Theory]
    [InlineData("######                                                     10.2%", 10.2)]
    [InlineData("                                                            0.0%", 0.0)]
    [InlineData("################################################################ 100.0%", 100.0)]
    [InlineData("##### 42,5%", 42.5)]
    public void A_curl_progress_bar_is_read_as_a_percentage(string fragment, double expected)
    {
        EngineProgress.CurlPercent(fragment).Should().BeApproximately(expected, 0.001);
    }

    [Theory]
    [InlineData("curl: (22) The requested URL returned error: 404")]
    [InlineData("-=O=-   #    #    #")]
    [InlineData("100% of nothing")]
    [InlineData("####  250.0%")]
    public void Anything_else_on_stderr_is_not_a_percentage(string fragment)
    {
        EngineProgress.CurlPercent(fragment).Should().BeNull();
    }

    [Fact]
    public void A_progress_fragment_updates_the_bar_and_is_not_a_line()
    {
        var progress = new EngineProgress();
        progress.OnOutputLine("Downloading x ...");

        progress.OnErrorFragment("#####     33.3%").Should().BeNull();

        progress.Percent.Should().BeApproximately(33.3, 0.001);
        progress.LastErrorLine.Should().BeNull();
    }

    [Fact]
    public void A_new_stage_clears_the_previous_files_percentage()
    {
        var progress = new EngineProgress();
        progress.OnOutputLine("Downloading x ...");
        progress.OnErrorFragment("### 100.0%");

        progress.OnOutputLine("Downloading y ...");

        progress.Percent.Should().BeNull();
    }

    [Fact]
    public void Other_stderr_text_is_kept_as_the_last_error_line()
    {
        var progress = new EngineProgress();

        progress.OnErrorFragment("curl: (6) Could not resolve host: updates.example.test").Should().Be("curl: (6) Could not resolve host: updates.example.test");

        progress.LastErrorLine.Should().Be("curl: (6) Could not resolve host: updates.example.test");
    }

    [Fact]
    public void Changed_is_raised_for_lines_and_for_progress()
    {
        var progress = new EngineProgress();
        var raised = 0;
        progress.Changed += () => raised++;

        progress.OnOutputLine("Looking up the release ...");
        progress.OnErrorFragment("## 5.0%");

        raised.Should().Be(2);
    }
}
