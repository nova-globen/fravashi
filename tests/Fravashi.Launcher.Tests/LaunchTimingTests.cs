using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Backlog 379: a launch shows the splash before anything slow runs, and the launcher log says how long that took.
/// Fravashi.Desktop has no test project (its logic lives in the launcher), so the order in its Program.Main is held
/// here by reading the source.
/// </summary>
public sealed class LaunchTimingTests
{
    [Fact]
    public void The_log_line_says_how_long_after_the_start_the_splash_was_on_screen()
    {
        LaunchTiming.SplashShown(TimeSpan.FromMilliseconds(212.4))
            .Should().Be("The splash was shown 212 ms after fravashi.exe started.");
    }

    [Fact]
    public void A_start_past_the_target_says_so()
    {
        LaunchTiming.SplashShown(TimeSpan.FromMilliseconds(2400))
            .Should().Be("The splash was shown 2400 ms after fravashi.exe started. That is past the 300 ms target (backlog 379).");
    }

    [Fact]
    public void A_clock_that_went_backwards_never_gives_a_negative_figure()
    {
        LaunchTiming.SplashShown(TimeSpan.FromMilliseconds(-5)).Should().StartWith("The splash was shown 0 ms");
    }

    [Fact]
    public void Program_shows_the_splash_before_it_moves_the_data_folder_or_migrates()
    {
        var program = File.ReadAllText(Path.Combine(Root(), "src", "Fravashi.Desktop", "Program.cs"));
        var main = program[program.IndexOf("private static int Main(", StringComparison.Ordinal)..];

        var shown = main.IndexOf("splash.Show();", StringComparison.Ordinal);
        var moved = main.IndexOf("MoveDataFolder(parsed);", StringComparison.Ordinal);
        var run = main.IndexOf("Application.Run(", StringComparison.Ordinal);

        shown.Should().BeGreaterThan(0, "Main shows the splash itself");
        shown.Should().BeLessThan(moved, "the data-folder move and the migration run behind the splash");
        main.IndexOf("splash.Refresh();", StringComparison.Ordinal).Should().BeInRange(shown, moved, "it is painted before the slow work");
        main[run..].Should().Contain("splash, splashAfter", "the context takes the splash it was shown with");
        File.ReadAllText(Path.Combine(Root(), "src", "Fravashi.Desktop", "DesktopApplicationContext.cs"))
            .Should().NotContain("new SplashForm()", "a second splash would come only after the slow work again");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("the repository root (Fravashi.sln) was not found");
    }
}
