using AwesomeAssertions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

public sealed class StudioRestartReasonTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-restart-reason-" + Guid.NewGuid().ToString("N"));

    public StudioRestartReasonTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ConsumeOnce_WithNoFile_IsNull()
    {
        StudioRestartReason.ConsumeOnce(_directory).Should().BeNull();
    }

    [Fact]
    public void ConsumeOnce_ReadsAndDeletesTheFile_SoASecondCallFindsNothing()
    {
        var path = Path.Combine(_directory, StudioRestartReason.FileName);
        File.WriteAllText(path, "Fravashi restarted its studio because this PC's network changed.");

        StudioRestartReason.ConsumeOnce(_directory).Should().Be(
            "Fravashi restarted its studio because this PC's network changed.");

        File.Exists(path).Should().BeFalse("consumed once, so a later restart with no reason finds nothing stale");
        StudioRestartReason.ConsumeOnce(_directory).Should().BeNull();
    }

    [Fact]
    public void ConsumeOnce_OfABlankFile_IsNull()
    {
        File.WriteAllText(Path.Combine(_directory, StudioRestartReason.FileName), "   ");

        StudioRestartReason.ConsumeOnce(_directory).Should().BeNull();
    }
}
