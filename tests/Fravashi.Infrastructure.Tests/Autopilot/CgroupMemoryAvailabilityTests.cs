using AwesomeAssertions;
using Fravashi.Application.Autopilot;
using Fravashi.Domain.Autopilot;
using Fravashi.Infrastructure.Autopilot;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Autopilot;

/// <summary>
/// #308 host note point 5: the container's own cgroup v2 memory figures, read fresh, naming the source a
/// refusal actually answered from. Paths are injectable precisely so these do not depend on, or assume
/// anything about, whatever cgroup files the host running them happens to have.
/// </summary>
public sealed class CgroupMemoryAvailabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fravashi-cgroup-" + Guid.NewGuid().ToString("N"));

    public CgroupMemoryAvailabilityTests() => Directory.CreateDirectory(_directory);

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

    private string WriteMax(string value) => Write("memory.max", value);

    private string WriteCurrent(string value) => Write("memory.current", value);

    private string Write(string name, string value)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, value);
        return path;
    }

    [Fact]
    public void Reads_the_difference_between_max_and_current_and_names_the_cgroup_source()
    {
        var reader = new CgroupMemoryAvailability(maxPath: WriteMax("1000"), currentPath: WriteCurrent("300"));

        reader.AvailableBytes().Should().Be(700);
        reader.Source.Should().Be("cgroup");
    }

    [Fact]
    public void A_literal_max_with_no_limit_set_falls_back_to_the_estimate()
    {
        var fallback = new FixedFallback(123L);
        var reader = new CgroupMemoryAvailability(fallback, WriteMax("max"), WriteCurrent("300"));

        reader.AvailableBytes().Should().Be(123);
        reader.Source.Should().Be("estimate");
    }

    [Fact]
    public void An_unparseable_figure_falls_back_to_the_estimate()
    {
        var fallback = new FixedFallback(456L);
        var reader = new CgroupMemoryAvailability(fallback, WriteMax("not-a-number"), WriteCurrent("300"));

        reader.AvailableBytes().Should().Be(456);
        reader.Source.Should().Be("estimate");
    }

    [Fact]
    public void Missing_files_fall_back_to_the_estimate()
    {
        var fallback = new FixedFallback(789L);
        var reader = new CgroupMemoryAvailability(
            fallback, Path.Combine(_directory, "missing-max"), Path.Combine(_directory, "missing-current"));

        reader.AvailableBytes().Should().Be(789);
        reader.Source.Should().Be("estimate");
    }

    [Fact]
    public void A_refusal_names_the_source_that_actually_answered()
    {
        var reader = new CgroupMemoryAvailability(maxPath: WriteMax("1000"), currentPath: WriteCurrent("999"));

        var refusal = AutopilotConcurrencyGuard.Refuse(0, 2, reader.AvailableBytes(), floorBytes: 500, reader.Source);

        refusal.Should().NotBeNull();
        refusal!.Message.Should().Contain("cgroup");
    }

    private sealed class FixedFallback(long bytes) : IAutopilotMemoryAvailability
    {
        public long? AvailableBytes() => bytes;
    }
}
