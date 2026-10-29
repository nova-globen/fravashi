using Bunit;
using Fravashi.Application.Verification;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Web.Components.WorkItems;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 224: under a scenario run's evidence, the item's page shows the design it was checked against with its version,
/// each screen's outcome and findings, and on request the build's screenshots beside the prototype's.
/// </summary>
public class DesignCheckEvidenceTests
{
    private const string Details = """
        {"steps":[],"design":{"number":42,"title":"Team directory","version":1,"artefact":"verification/20261007-120000-abcdef01",
         "screens":[{"screen":0,"id":"directory","title":"Team directory","url":"http://relay:8080/","outcome":"Failed",
                     "findings":["DIFFERENT NAME: the button is called Add someone."],"console":1}]}}
        """;

    private static EvidenceRecord Record(string? details, string? artefact = "verification/20261007-120000-abcdef01") =>
        new("e1", EvidenceKind.TestRun, EvidenceResult.Fail, "It failed.", ArtefactRef: artefact, Details: details);

    [Fact]
    public void The_design_its_version_and_each_screens_findings_are_shown()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<DesignCheckEvidence>(p => p.Add(c => c.Record, Record(Details)));

        var text = cut.Find("[data-testid=work-item-design-check]").TextContent;
        Assert.Contains("@42", text);
        Assert.Contains("version 1", text);
        Assert.Contains("FAILED", cut.Find("[data-testid=work-item-design-screen]").TextContent);
        Assert.Contains("1 console error", text);
        Assert.Contains("DIFFERENT NAME", cut.Find("[data-testid=work-item-design-finding]").TextContent);
    }

    [Fact]
    public void A_run_that_checked_no_design_shows_nothing_extra()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<DesignCheckEvidence>(p => p.Add(c => c.Record, Record("""{"steps":[],"design":"No design."}""", null)));

        Assert.Empty(cut.FindAll("[data-testid=work-item-design-check]"));
    }

    [Fact]
    public async Task Asking_for_the_files_shows_the_build_beside_the_prototype_and_the_report()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<IVerificationArtifacts>(new Files(new Dictionary<string, byte[]>
        {
            ["report.md"] = "# Design check"u8.ToArray(),
            ["screen-0.wide.00-at-rest.build.png"] = [1, 2],
            ["screen-0.wide.00-at-rest.prototype.png"] = [3, 4],
        }));
        var cut = ctx.Render<DesignCheckEvidence>(p => p.Add(c => c.Record, Record(Details)));

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-design-files]").Click());

        cut.WaitForAssertion(() =>
        {
            var pair = cut.Find("[data-testid=work-item-design-pair]");
            Assert.Contains("screen-0.wide.00-at-rest", pair.TextContent);
            Assert.Equal(2, pair.QuerySelectorAll("img").Length);
            Assert.Contains("# Design check", cut.Markup);
        });
    }

    [Fact]
    public async Task Files_deleted_under_the_evidence_lifecycle_are_said_to_be_gone()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<IVerificationArtifacts>(new Files([]));
        var cut = ctx.Render<DesignCheckEvidence>(p => p.Add(c => c.Record, Record(Details)));

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-design-files]").Click());

        cut.WaitForAssertion(() => Assert.Contains("are gone", cut.Markup));
    }

    private sealed class Files(Dictionary<string, byte[]> files) : IVerificationArtifacts
    {
        public Task<string?> SaveAsync(VerificationArtifactSet set, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> ListAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([.. files.Keys.Order(StringComparer.Ordinal)]);

        public Task<byte[]?> ReadAsync(string reference, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(files.GetValueOrDefault(name));
    }
}
