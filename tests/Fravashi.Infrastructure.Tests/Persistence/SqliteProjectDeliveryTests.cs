using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Persistence;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

/// <summary>The per-project autopilot delivery choice (schema step 40, ADR 0031 decision 3, backlog 298).</summary>
public sealed class SqliteProjectDeliveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "fravashi-project-delivery-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteProjectDelivery _store;
    private static readonly ProjectId Project = new("proj-1");

    public SqliteProjectDeliveryTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _store = new SqliteProjectDelivery(_factory);
    }

    [Fact]
    public void A_project_that_never_chose_reads_branch_and_pull_request()
    {
        _store.For(Project).Should().Be(AutopilotDeliveryMode.BranchAndPullRequest);
    }

    [Fact]
    public void Set_RoundTrips()
    {
        _store.Set(Project, AutopilotDeliveryMode.CommitToMain);

        _store.For(Project).Should().Be(AutopilotDeliveryMode.CommitToMain);
    }

    [Fact]
    public void Set_Overwrites_TheExistingChoice()
    {
        _store.Set(Project, AutopilotDeliveryMode.CommitToMain);
        _store.Set(Project, AutopilotDeliveryMode.BranchAndPullRequest);

        _store.For(Project).Should().Be(AutopilotDeliveryMode.BranchAndPullRequest);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
