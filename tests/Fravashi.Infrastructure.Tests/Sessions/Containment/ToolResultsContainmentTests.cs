using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions.Containment;

/// <summary>
/// Backlog 338: a Read, Glob or Grep that stays inside THIS session's own saved-tool-output folder (the
/// pinned CLI's <c>&lt;config home&gt;/projects/&lt;key&gt;/&lt;providerSessionId&gt;/tool-results</c>) runs
/// unattended, exactly as a read inside the workspace copy does. That folder sits right beside things that
/// must keep asking (the session's settings, its approval script, the bare session folder, another
/// session's own tool-results), so every negative case here is one of those near misses rather than an
/// unrelated path.
/// <para>
/// Unlike memory, no second, independently-pinned "target" string is needed here: there is only one real
/// path, not a link-and-target pair. But the folder IS still agent-writable (the whole session config home
/// is), so it is not trusted on the bare strength of resolving either: <c>AgentReadScope.ContainmentRoots</c>
/// drops it unless its kernel-resolved reading equals its plain lexical one, meaning no symlink sits anywhere
/// between the config home and <c>tool-results</c>. The re-pointing tests below prove that live, the same way
/// <c>MemoryContainmentTests</c> proves it for the memory link.
/// </para>
/// <para>
/// Ground truth is the real fixture tree (<see cref="ContainmentFixture"/>), never a restated model of the
/// check: every "still asks" case below is a real <c>..</c> chain or a real symlink, not a string the test
/// merely asserts should be refused.
/// </para>
/// </summary>
public class ToolResultsContainmentTests
{
    [PlatformOracleFact]
    public void A_read_of_the_current_sessions_own_tool_results_file_runs_unattended()
    {
        using var fixture = ContainmentFixture.Create();
        var path = Path.Combine(fixture.ToolResults, "proof-marker.txt");

        File.ReadAllText(path).Trim().Should().Be(
            "saved output", "the case only means something if this really is the saved tool output");

        var input = Call(("file_path", path));

        AgentReadScope.Check("Read", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)
            .Contained.Should().BeTrue(
                "a read of this session's own saved tool output, reached the way the CLI reaches it, must not ask");

        AgentReadScope.Resolve("Read", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)!
            .Containment.Should().Be(AgentTargetContainment.Inside);
    }

    [PlatformOracleFact]
    public void A_grep_under_the_tool_results_folder_runs_unattended()
    {
        // Scoped to "logs", a sibling of the "escape" link tested below, for the same reason
        // MemoryContainmentTests scopes its own wildcard case to "notes": round 7's containment check
        // establishes the WHOLE subtree a wildcard can walk holds no escaping link, so a pattern rooted
        // where "escape" itself lives would correctly have to ask.
        using var fixture = ContainmentFixture.Create();
        var input = Call(("path", Path.Combine(fixture.ToolResults, "logs")), ("glob", "*.txt"));

        AgentReadScope.Check("Grep", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)
            .Contained.Should().BeTrue();
    }

    [PlatformOracleFact]
    public void A_glob_of_the_tool_results_folder_with_a_wildcard_pattern_runs_unattended()
    {
        using var fixture = ContainmentFixture.Create();
        var input = Call(("pattern", Path.Combine(fixture.ToolResults, "logs") + "/*"));

        AgentReadScope.Check("Glob", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)
            .Contained.Should().BeTrue();
    }

    [PlatformOracleFact]
    public void A_read_of_an_entry_that_does_not_exist_yet_under_tool_results_runs_unattended()
    {
        using var fixture = ContainmentFixture.Create();
        var path = Path.Combine(fixture.ToolResults, "brand-new-result.txt");
        File.Exists(path).Should().BeFalse("the case only means something if the leaf is genuinely absent");

        var input = Call(("file_path", path));

        AgentReadScope.Check("Read", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)
            .Contained.Should().BeTrue(
                "a leaf that does not exist yet must not be a way to dodge the check in either direction");
    }

    [PlatformOracleFact]
    public void The_sessions_own_approval_script_still_asks()
    {
        using var fixture = ContainmentFixture.Create();
        var input = Call(("file_path", fixture.SessionApprove));

        AgentReadScope.Check("Read", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)
            .Contained.Should().BeFalse("the approval script carries the session's secret and must keep asking");
    }

    [PlatformOracleFact]
    public void The_sessions_own_settings_file_still_asks()
    {
        using var fixture = ContainmentFixture.Create();
        var input = Call(("file_path", fixture.SessionSettings));

        AgentReadScope.Check("Read", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)
            .Contained.Should().BeFalse();
    }

    [PlatformOracleFact]
    public void The_session_folder_itself_still_asks()
    {
        using var fixture = ContainmentFixture.Create();
        var input = Call(("path", fixture.SessionConfigHome), ("glob", "*"));

        AgentReadScope.Check("Grep", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)
            .Contained.Should().BeFalse(
                "being inside the broader session folder is not the same as being inside tool-results");
    }

    [PlatformOracleFact]
    public void Another_sessions_tool_results_is_not_auto_allowed()
    {
        using var fixture = ContainmentFixture.Create();
        var path = Path.Combine(fixture.OtherSessionToolResults, "proof-marker.txt");
        File.Exists(path).Should().BeTrue();

        var input = Call(("file_path", path));

        AgentReadScope.Check("Read", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)
            .Contained.Should().BeFalse("this session's root resolves to its own provider id, never another session's");

        AgentReadScope.Resolve("Read", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)!
            .Containment.Should().NotBe(AgentTargetContainment.Inside);
    }

    [PlatformOracleTheory]
    [InlineData("../../../../settings.json", "the approval hook's settings")]
    [InlineData("../../../../approve.sh", "the approval script, which carries the session's secret")]
    [InlineData("../../abcd1234.jsonl", "the transcript file right beside the provider-id folder")]
    public void A_dot_dot_chain_from_tool_results_into_the_session_directory_still_asks(string suffix, string why)
    {
        using var fixture = ContainmentFixture.Create();
        var path = Path.Combine(fixture.ToolResults, suffix);

        // GROUND TRUTH: the lexical collapse really does land on the sibling file, not on tool-results.
        var landed = Path.GetFullPath(path);
        File.Exists(landed).Should().BeTrue($"the fixture has to actually hold {suffix} for this to prove anything");
        landed.Should().NotStartWith(fixture.ToolResults, $"{why} is not under tool-results at all");

        var input = Call(("file_path", path));

        AgentReadScope.Check("Read", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)
            .Contained.Should().BeFalse($"{why} sits right beside tool-results and must keep asking");
    }

    [PlatformOracleFact]
    public void A_link_inside_tool_results_that_points_out_of_it_is_not_auto_allowed()
    {
        using var fixture = ContainmentFixture.Create();
        fixture.HasSymlinks.Should().BeTrue("this case needs a real symlink to mean anything");
        var path = Path.Combine(fixture.ToolResults, "escape", "keys.txt");

        File.ReadAllText(path).Trim().Should().Be(
            "secret", "the case only means something if this really reaches the secrets tree");

        var input = Call(("file_path", path));

        AgentReadScope.Check("Read", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)
            .Contained.Should().BeFalse("a link inside tool-results must not be trusted to stay inside it");

        AgentReadScope.Resolve("Read", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)!
            .Containment.Should().NotBe(AgentTargetContainment.Inside);
    }

    /// <summary>
    /// The session config home is writable by the agent's own uid (the CLI itself writes there), so an
    /// approved command could replace <c>tool-results</c> itself with a symlink, standing in for
    /// <c>rm -r &lt;...&gt;/tool-results &amp;&amp; ln -s /data/secrets &lt;...&gt;/tool-results</c>. Unlike
    /// the memory link this is not a link by design, so there is no independent "target" to pin against;
    /// the root is dropped because its kernel-resolved reading no longer equals its plain lexical one.
    /// </summary>
    [PlatformOracleFact]
    public void A_tool_results_folder_replaced_by_a_symlink_is_not_trusted_and_keeps_asking()
    {
        using var fixture = ContainmentFixture.Create();
        fixture.HasSymlinks.Should().BeTrue("this case needs a real symlink to mean anything");

        Directory.Delete(fixture.ToolResults, recursive: true);
        Directory.CreateSymbolicLink(fixture.ToolResults, fixture.Secrets);

        var path = Path.Combine(fixture.ToolResults, "keys.txt");
        File.ReadAllText(path).Trim().Should().Be(
            "secret", "the replacement only means something if this really reaches the secrets tree now");

        var input = Call(("file_path", path));

        AgentReadScope.Check("Read", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)
            .Contained.Should().BeFalse(
                "tool-results' own name is now a link to secrets, not the real folder the CLI would have made");
    }

    /// <summary>
    /// The same attack one level up: a fresh session's provider-id folder does not exist until the CLI
    /// creates it, so an approved command could plant the symlink there FIRST, before <c>tool-results</c>
    /// itself is ever created under it. The root still has to be dropped on the strength of the ancestor
    /// alone.
    /// </summary>
    [PlatformOracleFact]
    public void A_provider_id_folder_replaced_by_a_symlink_before_tool_results_exists_is_not_trusted()
    {
        using var fixture = ContainmentFixture.Create();
        fixture.HasSymlinks.Should().BeTrue("this case needs a real symlink to mean anything");

        var providerIdFolder = Path.Combine(fixture.SessionProjectDirectory, "abcd1234");
        Directory.Delete(providerIdFolder, recursive: true);
        Directory.CreateSymbolicLink(providerIdFolder, fixture.Secrets);

        var input = Call(("file_path", Path.Combine(fixture.ToolResults, "anything.txt")));

        AgentReadScope.Check("Read", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)
            .Contained.Should().BeFalse(
                "the provider-id folder above tool-results is now a link, so the whole root must be dropped");
    }

    [PlatformOracleFact]
    public void A_tool_results_read_with_no_root_registered_keeps_asking()
    {
        using var fixture = ContainmentFixture.Create();
        var path = Path.Combine(fixture.ToolResults, "proof-marker.txt");

        // No toolResultsRoot handed in at all: a fresh session whose provider id the CLI has not yet
        // announced, or a studio with no framework projection, and every tool-results read keeps asking
        // exactly as it did before backlog 338.
        AgentReadScope.Check("Read", Call(("file_path", path)), fixture.Copy).Contained.Should().BeFalse();
    }

    [PlatformOracleFact]
    public void An_ordinary_read_inside_the_copy_is_unaffected_by_a_tool_results_root_being_registered()
    {
        using var fixture = ContainmentFixture.Create();
        var input = Call(("file_path", Path.Combine(fixture.Copy, "src", "main.txt")));

        AgentReadScope.Check("Read", input, fixture.Copy, toolResultsRoot: fixture.ToolResults)
            .Contained.Should().BeTrue();
    }

    private static AgentToolInput Call(params (string Field, string Value)[] fields)
    {
        var values = fields.ToDictionary(f => f.Field, f => f.Value, StringComparer.Ordinal);
        return AgentToolInput.Create(values, values.Keys);
    }
}
