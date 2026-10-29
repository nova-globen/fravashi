using System.Text.Json;
using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Covers the per-runtime container-hardening matrix (F4 / ADR 0007, ADR 0009 capability matrix): that
/// Docker/Podman apply the FULL protection set and wslc applies the accepted subset while OMITTING the flags it
/// rejects - asserted through both the produced <c>run</c> args and the honest posture the launcher records.
/// </summary>
public sealed class ContainerHardeningTests
{
    private static readonly ContainerRunSpec Spec = new("fravashi", "fravashi:latest", 9090, 8080, "/data");

    // ---- run-arg construction ----

    [Fact]
    public void DockerArgs_ApplyTheFullHardeningSet()
    {
        var args = new DockerRuntime().BuildRunArgs(Spec);

        args.Should().ContainInConsecutiveOrder("--publish", "127.0.0.1:9090:8080");
        args.Should().ContainInConsecutiveOrder("--user", "1000:1000");
        args.Should().ContainInConsecutiveOrder("--cap-drop", "ALL");
        args.Should().ContainInConsecutiveOrder("--security-opt", "no-new-privileges");
        args.Should().Contain("--read-only");
        args.Should().ContainInConsecutiveOrder("--pids-limit", "4096");
        args.Should().ContainInConsecutiveOrder("--memory", "8G");
        args.Should().ContainInConsecutiveOrder("--tmpfs", "/tmp");
        // Docker gets the real pids limit, not the nproc-ulimit substitute.
        args.Should().NotContain("--ulimit");
    }

    [Fact]
    public void WslcArgs_ApplyTheAcceptedSubset_AndOmitTheRejectedFlags()
    {
        var args = new WslcRuntime().BuildRunArgs(Spec);

        // Accepted by wslc.
        args.Should().ContainInConsecutiveOrder("--publish", "127.0.0.1:9090:8080");
        args.Should().ContainInConsecutiveOrder("--user", "1000:1000");
        args.Should().ContainInConsecutiveOrder("--memory", "8G");
        args.Should().ContainInConsecutiveOrder("--tmpfs", "/tmp");
        args.Should().ContainInConsecutiveOrder("--ulimit", "nproc=4096:4096");

        // Rejected by wslc - never emitted (ADR 0009).
        args.Should().NotContain("--cap-drop");
        args.Should().NotContain("--security-opt");
        args.Should().NotContain("--read-only");
        args.Should().NotContain("--pids-limit");
    }

    [Fact]
    public void PodmanArgs_MatchDocker_ForHardeningToo_AsideFromTheRootlessKeepIdUserns()
    {
        // Podman applies the identical hardening set to Docker; it carries ONE extra arg - the rootless
        // --userns=keep-id remap that lets the hardened non-root app write the bind-mounted /data (the readonly-DB
        // fix). Removing that pair leaves the two arg lists identical, so the hardening parity is preserved.
        var podman = new PodmanRuntime().BuildRunArgs(Spec).ToList();
        podman.Remove("--userns");
        podman.Remove("keep-id:uid=1000,gid=1000");

        podman.Should().Equal(new DockerRuntime().BuildRunArgs(Spec));
    }

    // ---- posture (the source of truth the app displays) ----

    [Theory]
    [InlineData(RuntimeNames.Docker)]
    [InlineData(RuntimeNames.Wslc)]
    public void The_no_setuid_protection_is_credited_only_to_an_image_that_says_so(string runtime)
    {
        // ADR 0029, D4: a property of the image, stamped by its build step, so an older image is never credited.
        Status(ContainerHardening.Plan(runtime, imageStripped: true)).Should().Be(HardeningStatus.Active);

        var older = ContainerHardening.Plan(runtime, imageStripped: false).Controls
            .Single(c => c.Id == nameof(HardeningControlId.NoPrivilegeRaisingBinaries));
        older.Status.Should().Be(HardeningStatus.Unavailable);
        older.Detail.Should().Contain("predates").And.NotContain("use Docker or Podman");

        static HardeningStatus Status(HardeningPlan plan) =>
            plan.Controls.Single(c => c.Id == nameof(HardeningControlId.NoPrivilegeRaisingBinaries)).Status;
    }

    [Fact]
    public void The_no_setuid_protection_adds_no_run_flag()
    {
        ContainerHardening.ArgsFor(HardeningControlId.NoPrivilegeRaisingBinaries, Spec).Should().BeEmpty();
    }

    [Fact]
    public void DockerPosture_MarksEveryProtectionActive_AndDoesNotRecommendAStrongerRuntime()
    {
        var plan = ContainerHardening.Plan(RuntimeNames.Docker, imageStripped: true);

        plan.StrongerRuntimeRecommended.Should().BeFalse();
        plan.Recommendation.Should().BeNull();
        plan.Controls.Should().OnlyContain(c => c.Status == HardeningStatus.Active);
        plan.Controls.Select(c => c.Id).Should().Contain(
            nameof(HardeningControlId.DropAllCapabilities),
            nameof(HardeningControlId.ReadOnlyRootfs),
            nameof(HardeningControlId.PidsLimit));
        // Docker's posture uses the real pids limit, not the nproc substitute.
        plan.Controls.Select(c => c.Id).Should().NotContain(nameof(HardeningControlId.NprocUlimit));
    }

    [Fact]
    public void WslcPosture_MarksRejectedProtectionsUnavailable_AndRecommendsAStrongerRuntime()
    {
        var plan = ContainerHardening.Plan(RuntimeNames.Wslc);

        plan.StrongerRuntimeRecommended.Should().BeTrue();
        plan.Recommendation.Should().Contain("Docker").And.Contain("Podman");

        var byId = plan.Controls.ToDictionary(c => c.Id, c => c.Status);
        byId[nameof(HardeningControlId.DropAllCapabilities)].Should().Be(HardeningStatus.Unavailable);
        byId[nameof(HardeningControlId.NoNewPrivileges)].Should().Be(HardeningStatus.Unavailable);
        byId[nameof(HardeningControlId.ReadOnlyRootfs)].Should().Be(HardeningStatus.Unavailable);
        byId[nameof(HardeningControlId.PidsLimit)].Should().Be(HardeningStatus.Unavailable);

        byId[nameof(HardeningControlId.LoopbackPublish)].Should().Be(HardeningStatus.Active);
        byId[nameof(HardeningControlId.NonRootUser)].Should().Be(HardeningStatus.Active);
        byId[nameof(HardeningControlId.MemoryLimit)].Should().Be(HardeningStatus.Active);
        byId[nameof(HardeningControlId.TmpfsTmp)].Should().Be(HardeningStatus.Active);
        byId[nameof(HardeningControlId.NprocUlimit)].Should().Be(HardeningStatus.Active);
    }

    [Fact]
    public void UnavailableControls_ExplainWhy_AndNameTheStrongerRuntimes()
    {
        var readOnly = ContainerHardening.Plan(RuntimeNames.Wslc).Controls
            .Single(c => c.Id == nameof(HardeningControlId.ReadOnlyRootfs));

        readOnly.Status.Should().Be(HardeningStatus.Unavailable);
        readOnly.Detail.Should().Contain("wslc").And.Contain("Docker");
    }

    // ---- the posture file the app reads ----

    [Fact]
    public void SandboxPostureStore_WritesTheRuntimeAndPerControlStatus()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fravashi-posture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            new SandboxPostureStore(dir).Write(ContainerHardening.Plan(RuntimeNames.Wslc));

            var json = File.ReadAllText(Path.Combine(dir, SandboxPostureStore.FileName));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            root.GetProperty("runtime").GetString().Should().Be(RuntimeNames.Wslc);
            root.GetProperty("strongerRuntimeRecommended").GetBoolean().Should().BeTrue();

            var controls = root.GetProperty("controls").EnumerateArray().ToList();
            controls.Should().Contain(c =>
                c.GetProperty("id").GetString() == nameof(HardeningControlId.ReadOnlyRootfs) &&
                c.GetProperty("status").GetString() == nameof(HardeningStatus.Unavailable));
            controls.Should().Contain(c =>
                c.GetProperty("id").GetString() == nameof(HardeningControlId.LoopbackPublish) &&
                c.GetProperty("status").GetString() == nameof(HardeningStatus.Active));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
