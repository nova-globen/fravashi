using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Framework;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Framework;
using Fravashi.Infrastructure.Tests.AgentRuntimes;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Framework;

/// <summary>
/// The model-list probe and the probe-then-catalog resolver (D03-12). No real process is spawned (the command
/// runner is faked), but where the probe issues a command it genuinely issues it and parses the output; a fake,
/// not a stub that always "succeeds".
/// <para>
/// The one CLI that had a scriptable list-models command was retired to v2 (docs/v2.md section 1), so the probe
/// now answers null for every runtime this version offers and never shells out. The cases that drove the real
/// command and its defensive parser are parked below behind <c>V2_CODEX_COPILOT</c>, because that parser was
/// written against a DEBUG surface whose shape is not contractual and re-deriving it would mean re-measuring a
/// binary the image no longer carries.
/// </para>
/// </summary>
public class ModelListProbeAndResolverTests
{
#if V2_CODEX_COPILOT
    [Fact]
    public async Task Codex_probe_issues_codex_debug_models_and_parses_ids()
    {
        var runner = new FakeAgentCommandRunner
        {
            Handler = _ => new AgentCommandResult(
                true, 0,
                """[{"id":"gpt-5.6-sol"},{"id":"gpt-5.4"},{"id":"gpt-5.6-sol"}]""", string.Empty, false),
        };
        var probe = new CliModelListProbe(runner);

        var models = await probe.TryListAsync(AgentTool.Codex);

        models.Should().Equal("gpt-5.6-sol", "gpt-5.4"); // de-duplicated, order preserved
        var call = runner.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("codex");
        call.Arguments.Should().Equal("debug", "models");
    }

    [Fact]
    public async Task Codex_probe_parses_an_object_with_a_models_array_of_strings()
    {
        var runner = new FakeAgentCommandRunner
        {
            Handler = _ => new AgentCommandResult(true, 0, """{"models":["a","b"]}""", string.Empty, false),
        };

        var models = await new CliModelListProbe(runner).TryListAsync(AgentTool.Codex);

        models.Should().Equal("a", "b");
    }

    [Fact]
    public async Task Codex_probe_returns_null_on_a_failed_or_unparseable_run()
    {
        var failing = new FakeAgentCommandRunner { Handler = _ => AgentCommandResult.NotStarted("not installed") };
        (await new CliModelListProbe(failing).TryListAsync(AgentTool.Codex)).Should().BeNull();

        var garbage = new FakeAgentCommandRunner { Handler = _ => new AgentCommandResult(true, 0, "not json", string.Empty, false) };
        (await new CliModelListProbe(garbage).TryListAsync(AgentTool.Codex)).Should().BeNull();
    }
#endif

    [Theory]
    [InlineData(AgentTool.Claude)]
    public async Task Runtimes_without_a_list_command_return_null_and_never_shell_out(AgentTool tool)
    {
        var runner = new FakeAgentCommandRunner();

        var result = await new CliModelListProbe(runner).TryListAsync(tool);

        result.Should().BeNull();
        runner.Calls.Should().BeEmpty();
    }

    private sealed class StubProbe(IReadOnlyList<string>? models) : IModelListProbe
    {
        public Task<IReadOnlyList<string>?> TryListAsync(AgentTool tool, CancellationToken cancellationToken = default)
            => Task.FromResult(models);
    }

    [Fact]
    public async Task Resolver_prefers_the_live_probe_when_it_lists_models()
    {
        var resolver = new ModelListResolver(new StubProbe(["live-1", "live-2"]));

        var result = await resolver.ResolveAsync(AgentTool.Claude);

        result.Source.Should().Be(ModelListSource.Probe);
        result.Models.Select(m => m.Id).Should().Equal("live-1", "live-2");
    }

    [Fact]
    public async Task Resolver_falls_back_to_the_catalog_when_the_probe_returns_null()
    {
        var resolver = new ModelListResolver(new StubProbe(null));

        var result = await resolver.ResolveAsync(AgentTool.Claude);

        result.Source.Should().Be(ModelListSource.Catalog);
        result.Models.Should().BeEquivalentTo(AgentModelCatalog.For(AgentTool.Claude));
        result.Provenance.Should().Contain("Curated catalog");
    }

    [Fact]
    public void The_catalog_covers_every_supported_runtime()
    {
        foreach (var tool in AgentRuntimeCatalog.Tools)
        {
            AgentModelCatalog.For(tool).Should().NotBeEmpty($"{tool} needs a catalog fallback");
        }
    }

    // ---- backlog 111: the pinned Claude list and the build-time drift check tied to the CLI pin --------

    [Fact]
    public void The_claude_catalog_offers_fable_5_1()
    {
        // The reported defect: the owner could not find Fable 5 in the picker because the list is pinned
        // and nobody had re-driven it since 2026-07-16.
        AgentModelCatalog.For(AgentTool.Claude).Select(m => m.Id).Should().Contain("claude-fable-5-1");
    }

    [Fact]
    public void Every_catalogued_id_is_a_plain_cli_model_id()
    {
        // An id is passed verbatim to --model, so it has to be something a shell and the CLI both take as
        // one word: lower-case letters, digits, dots and hyphens. A display name or a stray space here
        // would be offered and then rejected.
        foreach (var tool in AgentRuntimeCatalog.Tools)
        {
            foreach (var model in AgentModelCatalog.For(tool))
            {
                model.Id.Should().MatchRegex("^[a-z0-9.-]+$", $"{tool} offers '{model.Id}'");
            }
        }
    }

    [Fact]
    public void The_claude_list_was_verified_against_the_cli_pin_the_studio_ships()
    {
        // Claude Code has no list-models command, so the Claude list can only be pinned, and a pinned list
        // drifts silently as the vendor ships models. This makes the drift a build-time fact in the spirit
        // of ADR 0023: the catalog records the CLI pin its list was driven against, and promoting the pin
        // (AgentRuntimeCatalog) goes red here until someone re-drives the list and moves that record too.
        AgentModelCatalog.ClaudeVerifiedAgainstCliPin.Should().Be(
            AgentRuntimeCatalog.For(AgentTool.Claude).PinnedVersion,
            "the Claude model list is a claim about the pinned CLI, so a promotion has to re-verify it "
            + "(backlog 111): run every catalogued id through the promoted CLI, record the table in "
            + "AgentModelCatalog, and only then move ClaudeVerifiedAgainstCliPin");
    }

    [Fact]
    public void The_claude_provenance_names_the_binary_that_answered_and_the_pin_it_is_tied_to()
    {
        // Two facts, not one (ADR 0023): the host binary that was actually driven and the pin the list is tied
        // to. A line naming only the pin once read as if the pinned binary had answered, and it had not.
        AgentModelCatalog.ProvenanceFor(AgentTool.Claude)
            .Should().Contain(AgentModelCatalog.ClaudeVerifiedOn)
            .And.Contain(AgentModelCatalog.ClaudeVerifiedWithCli)
            .And.Contain(AgentModelCatalog.ClaudeVerifiedAgainstCliPin);
        AgentModelCatalog.ProvenanceFor(AgentTool.Codex).Should().Contain(AgentModelCatalog.VerifiedOn);
    }
}
