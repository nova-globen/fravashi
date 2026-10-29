using System.Text;
using Fravashi.Application.Framework;
using Fravashi.Domain.Framework;
using Fravashi.Infrastructure.Persistence;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

/// <summary>
/// The generic framework-artifact store: CRUD round-trip, name uniqueness per kind (typed exception), and the
/// resource blob path (attach, list, cap, cascade-delete). Skills use it now; the other kinds reuse it in T3.
/// </summary>
public sealed class SqliteFrameworkArtifactStoreTests
{
    private static FrameworkArtifact Artifact(string id, FrameworkArtifactKind kind, string name, string payload = "{}") =>
        new(id, kind, name, payload, new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Add_then_Get_round_trips_all_fields()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteFrameworkArtifactStore(ctx.Factory);
        var artifact = Artifact("a1", FrameworkArtifactKind.Skill, "weekly-status", """{"Name":"weekly-status"}""");

        store.Add(artifact);

        store.Get("a1").Should().Be(artifact);
    }

    [Fact]
    public void List_returns_only_the_requested_kind()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteFrameworkArtifactStore(ctx.Factory);
        store.Add(Artifact("a1", FrameworkArtifactKind.Skill, "skill-one"));
        store.Add(Artifact("a2", FrameworkArtifactKind.Agent, "agent-one"));

        store.List(FrameworkArtifactKind.Skill).Should().ContainSingle(a => a.Id == "a1");
    }

    [Fact]
    public void Add_with_a_duplicate_name_in_the_same_kind_throws()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteFrameworkArtifactStore(ctx.Factory);
        store.Add(Artifact("a1", FrameworkArtifactKind.Skill, "weekly-status"));

        var act = () => store.Add(Artifact("a2", FrameworkArtifactKind.Skill, "weekly-status"));

        act.Should().Throw<DuplicateArtifactNameException>().Which.Name.Should().Be("weekly-status");
    }

    [Fact]
    public void The_same_name_is_allowed_across_different_kinds()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteFrameworkArtifactStore(ctx.Factory);
        store.Add(Artifact("a1", FrameworkArtifactKind.Skill, "shared-name"));

        var act = () => store.Add(Artifact("a2", FrameworkArtifactKind.Agent, "shared-name"));

        act.Should().NotThrow();
    }

    [Fact]
    public void Update_can_rename_but_not_onto_another_artifacts_name()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteFrameworkArtifactStore(ctx.Factory);
        store.Add(Artifact("a1", FrameworkArtifactKind.Skill, "one"));
        store.Add(Artifact("a2", FrameworkArtifactKind.Skill, "two"));

        // Renaming a1 to a free name is fine…
        store.Update(Artifact("a1", FrameworkArtifactKind.Skill, "one-renamed"));
        store.Get("a1")!.Name.Should().Be("one-renamed");

        // …but onto a2's name throws.
        var act = () => store.Update(Artifact("a1", FrameworkArtifactKind.Skill, "two"));
        act.Should().Throw<DuplicateArtifactNameException>();
    }

    [Fact]
    public void Update_keeping_the_same_name_on_the_same_id_is_allowed()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteFrameworkArtifactStore(ctx.Factory);
        store.Add(Artifact("a1", FrameworkArtifactKind.Skill, "one", "{}"));

        var act = () => store.Update(Artifact("a1", FrameworkArtifactKind.Skill, "one", """{"changed":true}"""));

        act.Should().NotThrow();
        store.Get("a1")!.PayloadJson.Should().Contain("changed");
    }

    [Fact]
    public void Remove_deletes_the_artifact_and_its_resources()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteFrameworkArtifactStore(ctx.Factory);
        store.Add(Artifact("a1", FrameworkArtifactKind.Skill, "one"));
        store.AddResource(new FrameworkResource("r1", "a1", "notes.md", "text/markdown",
            Encoding.UTF8.GetBytes("hello"), DateTimeOffset.UtcNow));

        store.Remove("a1");

        store.Get("a1").Should().BeNull();
        store.GetResource("r1").Should().BeNull();
    }

    [Fact]
    public void Resources_round_trip_bytes_and_size()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteFrameworkArtifactStore(ctx.Factory);
        store.Add(Artifact("a1", FrameworkArtifactKind.Skill, "one"));
        var bytes = Encoding.UTF8.GetBytes("resource-body");
        store.AddResource(new FrameworkResource("r1", "a1", "file.txt", "text/plain", bytes, DateTimeOffset.UtcNow));

        var read = store.GetResource("r1")!;
        read.Content.Should().Equal(bytes);
        read.SizeBytes.Should().Be(bytes.Length);
        store.ListResources("a1").Should().ContainSingle();
    }

    [Fact]
    public void AddResource_over_the_cap_throws()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteFrameworkArtifactStore(ctx.Factory);
        store.Add(Artifact("a1", FrameworkArtifactKind.Skill, "one"));
        var tooBig = new byte[FrameworkArtifactLimits.MaxResourceBytes + 1];

        var act = () => store.AddResource(new FrameworkResource("r1", "a1", "big.bin", null, tooBig, DateTimeOffset.UtcNow));

        act.Should().Throw<ResourceTooLargeException>().Which.FileName.Should().Be("big.bin");
    }

    [Fact]
    public void Artifacts_persist_across_a_reopen_of_the_database()
    {
        using var ctx = new SqliteTestContext();
        new SqliteFrameworkArtifactStore(ctx.Factory).Add(Artifact("a1", FrameworkArtifactKind.Skill, "persist"));

        // A fresh store over the same file - simulating a process restart - still sees the row.
        var reopened = new SqliteFrameworkArtifactStore(ctx.Factory);
        reopened.GetByName(FrameworkArtifactKind.Skill, "persist").Should().NotBeNull();
    }
}
