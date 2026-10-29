using AwesomeAssertions;
using Fravashi.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Logging;

public class FileLoggerProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fravashi-filelog-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Writes_dated_app_files_with_category_level_and_exception()
    {
        using var provider = new FileLoggerProvider(_directory, "studio");
        var logger = provider.CreateLogger("Fravashi.Test.Category");

        logger.LogInformation("plain message");
        logger.LogError(new InvalidOperationException("boom"), "failing message");

        var file = Path.Combine(_directory, $"studio-{DateTime.Now:yyyy-MM-dd}.log");
        var content = File.ReadAllText(file);
        content.Should().Contain("[inf] Fravashi.Test.Category: plain message");
        content.Should().Contain("[err] Fravashi.Test.Category: failing message");
        content.Should().Contain("InvalidOperationException").And.Contain("boom");
    }

    [Fact]
    public void Levels_below_the_minimum_are_filtered_out()
    {
        using var provider = new FileLoggerProvider(_directory, "studio", LogLevel.Warning);
        var logger = provider.CreateLogger("Cat");

        logger.LogInformation("too quiet");
        logger.LogWarning("loud enough");

        var file = Path.Combine(_directory, $"studio-{DateTime.Now:yyyy-MM-dd}.log");
        File.ReadAllText(file).Should().Contain("loud enough").And.NotContain("too quiet");
    }

    [Fact]
    public void A_credential_in_a_log_line_is_redacted_before_it_is_written()
    {
        // Backlog 75: a log is evidence, so the redactor runs before the line reaches the disk.
        using var provider = new FileLoggerProvider(_directory, "studio");
        var logger = provider.CreateLogger("Cat");

        logger.LogWarning("tool printed sk-" + "ant-api03-Fake0123456789Fake0123456789Fake0123456789FakeAA in its output");
        logger.LogError(new InvalidOperationException("token ghp_SecretMaterialABCDEF123456 rejected"), "call failed");

        var content = File.ReadAllText(Path.Combine(_directory, $"studio-{DateTime.Now:yyyy-MM-dd}.log"));
        content.Should().NotContain("Fake0123456789");
        content.Should().NotContain("SecretMaterial");
        content.Should().Contain("[redacted:sk-ant-api-key]").And.Contain("[redacted:github-token]");
    }

    [Fact]
    public void This_apps_log_files_past_the_retention_are_deleted_and_other_apps_logs_are_not()
    {
        Directory.CreateDirectory(_directory);
        var old = Path.Combine(_directory, $"studio-{DateTime.Now.AddDays(-(FileLoggerProvider.RetentionDays + 1)):yyyy-MM-dd}.log");
        var recent = Path.Combine(_directory, $"studio-{DateTime.Now.AddDays(-(FileLoggerProvider.RetentionDays - 1)):yyyy-MM-dd}.log");
        var otherApp = Path.Combine(_directory, $"launcher-{DateTime.Now.AddDays(-60):yyyy-MM-dd}.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");
        File.WriteAllText(otherApp, "launcher");

        using var provider = new FileLoggerProvider(_directory, "studio");
        provider.CreateLogger("Cat").LogInformation("first line today");

        File.Exists(old).Should().BeFalse();
        File.Exists(recent).Should().BeTrue();
        File.Exists(otherApp).Should().BeTrue("the launcher's logs are the launcher's to keep");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort (covers a missing directory too).
        }
    }
}
