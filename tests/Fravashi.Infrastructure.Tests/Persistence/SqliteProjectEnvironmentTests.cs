using AwesomeAssertions;
using Fravashi.Application.Projects;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Tests.AgentRuntimes;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

/// <summary>
/// Backlog 154: the variables a project hands its agents. A plain one is listed with its value; a secret is listed
/// without it, lives in the secret store, reaches an agent through Resolve, and is on the list both guards check.
/// A name the studio or the runtime owns is refused at the door.
/// </summary>
public sealed class SqliteProjectEnvironmentTests : IDisposable
{
    private static readonly ProjectId Project = new("p1");

    private readonly SqliteTestContext _context = new();
    private readonly FakeSecretStore _secrets = new();
    private readonly SqliteProjectEnvironment _variables;

    public SqliteProjectEnvironmentTests()
    {
        _variables = new SqliteProjectEnvironment(_context.Factory, _secrets, TimeProvider.System);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void A_plain_variable_is_listed_with_its_value_and_a_secret_without_it()
    {
        _variables.Set(Project, "API_BASE", "https://api.example.test", secret: false).Should().BeNull();
        _variables.Set(Project, "SERVICE_TOKEN", "tok-0123456789abcdef", secret: true).Should().BeNull();

        var listed = _variables.List(Project);
        listed.Select(v => v.Name).Should().Equal("API_BASE", "SERVICE_TOKEN");
        listed[0].Value.Should().Be("https://api.example.test");
        listed[0].IsSecret.Should().BeFalse();
        listed[1].Value.Should().BeNull("a secret is never shown again once saved");
        listed[1].IsSecret.Should().BeTrue();

        // The secret's value is in the protected store under the project's own reference, not in the row.
        _secrets.Values.Should().ContainKey(SqliteProjectEnvironment.SecretRef(Project, "SERVICE_TOKEN"))
            .WhoseValue.Should().Be("tok-0123456789abcdef");
    }

    [Fact]
    public void Resolve_hands_an_agent_every_value_and_held_names_only_the_secrets()
    {
        _variables.Set(Project, "API_BASE", "https://api.example.test", secret: false);
        _variables.Set(Project, "SERVICE_TOKEN", "tok-0123456789abcdef", secret: true);
        _variables.Set(new ProjectId("other"), "OTHER_SECRET", "other-0123456789", secret: true);

        var resolved = _variables.Resolve(Project);
        resolved.Should().HaveCount(2);
        resolved["API_BASE"].Should().Be("https://api.example.test");
        resolved["SERVICE_TOKEN"].Should().Be("tok-0123456789abcdef");

        // Every project's secrets, and none of the plain values: what the two guards look for.
        var held = _variables.Held();
        held.Select(h => h.Name).Should().BeEquivalentTo(["SERVICE_TOKEN", "OTHER_SECRET"]);
        held.Should().NotContain(h => h.Value == "https://api.example.test");

        // And the same list rides on the helper both guards are wired with.
        AgentHeldCredentials.From(null, _variables).Select(h => h.Value).Should().Contain("tok-0123456789abcdef");
    }

    [Fact]
    public void Replacing_and_removing_a_variable_leaves_no_secret_behind()
    {
        _variables.Set(Project, "TOKEN", "secret-0123456789", secret: true);
        _variables.Set(Project, "TOKEN", "now-plain", secret: false);

        _secrets.Values.Should().NotContainKey(SqliteProjectEnvironment.SecretRef(Project, "TOKEN"), "it turned plain");
        _variables.Resolve(Project)["TOKEN"].Should().Be("now-plain");

        _variables.Set(Project, "TOKEN", "secret-again-0123456789", secret: true);
        _variables.Remove(Project, "TOKEN");

        _variables.List(Project).Should().BeEmpty();
        _secrets.Values.Should().BeEmpty();
        _variables.Held().Should().BeEmpty();
    }

    [Fact]
    public void Changes_are_announced_per_project()
    {
        var changed = new List<ProjectId>();
        _variables.Changed += changed.Add;

        _variables.Set(Project, "A", "1", secret: false);
        _variables.Remove(Project, "A");

        changed.Should().Equal(Project, Project);
    }

    [Theory]
    [InlineData("PATH", "the child's PATH is the studio's to set")]
    [InlineData("HOME", "the child's HOME is the studio's to set")]
    [InlineData("ANTHROPIC_API_KEY", "the provider key comes from the credential store")]
    [InlineData("ANTHROPIC_BASE_URL", "the broker address comes from the credential store")]
    [InlineData("CLAUDE_CONFIG_DIR", "the config home is the studio's")]
    [InlineData("FRAVASHI_BRIDGE_TOKEN", "nothing of the studio's is a project's to set")]
    [InlineData("NODE_OPTIONS", "a loader hook would run code inside the agent CLI")]
    [InlineData("LD_PRELOAD", "a loader hook would run code inside every child")]
    [InlineData("BASH_DEFAULT_TIMEOUT_MS", "the studio keeps a long build in the foreground")]
    [InlineData("BASH_MAX_TIMEOUT_MS", "the studio keeps a long build in the foreground")]
    [InlineData("CLAUDE_CODE_DISABLE_BACKGROUND_TASKS", "the studio keeps a long build in the foreground")]
    [InlineData("1BAD", "a name does not start with a digit")]
    [InlineData("MY VAR", "a name has no spaces")]
    [InlineData("", "a name is needed")]
    public void A_name_the_studio_or_the_runtime_owns_or_that_is_no_name_is_refused(string name, string why)
    {
        _variables.Set(Project, name, "x", secret: false).Should().NotBeNull(why);
        _variables.List(Project).Should().BeEmpty();
    }

    /// <summary>
    /// Backlog 189 refused two names that a project could set before. A row stored then is not handed to an agent,
    /// because project variables are applied after the studio's own and would override them.
    /// </summary>
    [Fact]
    public void A_row_stored_before_its_name_was_refused_is_not_resolved()
    {
        _variables.Set(Project, "API_BASE", "https://api.example.test", secret: false);
        using (var connection = _context.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO project_variables (project_id, name, value, is_secret, updated_utc) "
                + "VALUES ($p, 'BASH_DEFAULT_TIMEOUT_MS', '1000', 0, '2026-10-01T00:00:00.0000000+00:00');";
            command.Parameters.AddWithValue("$p", Project.Value);
            command.ExecuteNonQuery();
        }

        var resolved = _variables.Resolve(Project);

        resolved.Should().ContainKey("API_BASE");
        resolved.Should().NotContainKey("BASH_DEFAULT_TIMEOUT_MS");
    }

    [Theory]
    [InlineData("MY_SERVICE_TOKEN")]
    [InlineData("_private")]
    [InlineData("NPM_CONFIG_REGISTRY")]
    [InlineData("DOTNET_CLI_TELEMETRY_OPTOUT")]
    public void An_ordinary_name_is_accepted(string name) =>
        _variables.Set(Project, name, "x", secret: false).Should().BeNull();
}
