using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Backlog 79: what <c>fravashi restore</c> reads out of the studio's health answer. The schema version is the
/// only part of a restore that can be confirmed by the PRODUCT rather than asserted by the launcher, so
/// the parse has to survive both the answer the current studio gives and the one an older image gives.
/// </summary>
public class StudioHealthProbeTests
{
    [Fact]
    public void The_schema_version_is_read_out_of_the_health_document()
        => StudioHealthProbe.ReadSchemaVersion("{\"status\":\"ok\",\"schemaVersion\":10}").Should().Be(10);

    [Fact]
    public void An_older_studio_that_answers_only_a_status_is_still_a_healthy_studio()
        // The pre-backlog-79 image answers {"status":"ok"}. That is a studio that is up and cannot say
        // which schema it opened, which is a different thing from a studio that did not answer.
        => StudioHealthProbe.ReadSchemaVersion("{\"status\":\"ok\"}").Should().BeNull();

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":\"ten\"}")]
    [InlineData("{\"schemaVersion\":null}")]
    public void Anything_else_reads_as_no_version_rather_than_throwing(string body)
        => StudioHealthProbe.ReadSchemaVersion(body).Should().BeNull();
}
