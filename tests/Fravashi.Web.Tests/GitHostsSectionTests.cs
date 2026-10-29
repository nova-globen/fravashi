using Bunit;
using Fravashi.Application.Notifications;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>Settings > Connections > Git hosts (plan 2c, decision 8).</summary>
public class GitHostsSectionTests
{
    private sealed class FakeGitHostStore : IGitHostStore
    {
        public Dictionary<string, GitHostRecord> Map { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<GitHostRecord> GetAll() => Map.Values.OrderBy(r => r.Host).ToList();

        public GitHostRecord? Get(string host) => Map.GetValueOrDefault(host.ToLowerInvariant());

        public void Upsert(GitHostRecord record) => Map[record.Host] = record;

        public void Remove(string host) => Map.Remove(host.ToLowerInvariant());
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Dictionary<string, string> Map { get; } = new(StringComparer.Ordinal);

        public List<string> SetCalls { get; } = [];

        public void Set(string reference, string plaintext)
        {
            SetCalls.Add(reference);
            Map[reference] = plaintext;
        }

        public string? Get(string reference) => Map.GetValueOrDefault(reference);

        public void Remove(string reference) => Map.Remove(reference);
    }

    private static (BunitContext Ctx, FakeGitHostStore Hosts, FakeSecretStore Secrets) Context()
    {
        var ctx = new BunitContext();
        var hosts = new FakeGitHostStore();
        var secrets = new FakeSecretStore();
        ctx.Services.AddSingleton<IGitHostStore>(hosts);
        ctx.Services.AddSingleton<ISecretStore>(secrets);
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        return (ctx, hosts, secrets);
    }

    [Fact]
    public void Empty_state_shows_the_hint_and_the_add_form()
    {
        var (ctx, _, _) = Context();
        using var _ctx = ctx;

        var cut = ctx.Render<GitHostsSection>();

        Assert.Contains("No git hosts stored yet", cut.Markup);
        Assert.NotNull(cut.Find("[data-testid='git-host-save']"));
    }

    [Fact]
    public async Task Adding_a_host_normalizes_it_and_stores_the_token_under_the_githost_ref()
    {
        var (ctx, hosts, secrets) = Context();
        using var _ctx = ctx;
        var cut = ctx.Render<GitHostsSection>();

        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid='git-host-host']").Change("https://Forge.Example.Test/nova-globen/fravashi");
            cut.Find("[data-testid='git-host-provider']").Change("Forgejo");
            cut.Find("[data-testid='git-host-token']").Change("forgejo-pat");
            cut.Find("[data-testid='git-host-save']").Click();
        });

        cut.WaitForAssertion(() =>
        {
            var stored = Assert.Single(hosts.Map.Values);
            Assert.Equal("forge.example.test", stored.Host);
            Assert.Equal("Forgejo", stored.Provider);
            Assert.Equal("githost:forge.example.test:token", stored.CredentialRef);
        });

        var secretRef = Assert.Single(secrets.SetCalls);
        Assert.Equal("githost:forge.example.test:token", secretRef);
        var credential = CredentialEnvelope.Parse(secrets.Map[secretRef]);
        Assert.Equal("forgejo-pat", credential!.AccessToken);
        Assert.Contains("git-host-row-forge.example.test", cut.Markup);
    }

    [Fact]
    public async Task Editing_without_a_token_keeps_the_stored_secret()
    {
        var (ctx, hosts, secrets) = Context();
        using var _ctx = ctx;
        hosts.Upsert(new GitHostRecord("forge.example.test", "Forgejo", "githost:forge.example.test:token", DateTimeOffset.UtcNow));
        secrets.Map["githost:forge.example.test:token"] = CredentialEnvelope.Serialize(StoredCredential.ForPat("original"));
        var cut = ctx.Render<GitHostsSection>();

        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid='git-host-edit-forge.example.test']").Click();
            cut.Find("[data-testid='git-host-provider']").Change("GitHub");
            cut.Find("[data-testid='git-host-save']").Click();
        });

        cut.WaitForAssertion(() => Assert.Equal("GitHub", hosts.Map["forge.example.test"].Provider));
        Assert.Empty(secrets.SetCalls);
        Assert.Equal("original", CredentialEnvelope.Parse(secrets.Map["githost:forge.example.test:token"])!.AccessToken);
    }

    [Fact]
    public async Task Removing_a_host_deletes_the_entry_and_its_secret()
    {
        var (ctx, hosts, secrets) = Context();
        using var _ctx = ctx;
        hosts.Upsert(new GitHostRecord("forge.example.test", "Forgejo", "githost:forge.example.test:token", DateTimeOffset.UtcNow));
        secrets.Map["githost:forge.example.test:token"] = "anything";
        var cut = ctx.Render<GitHostsSection>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='git-host-remove-forge.example.test']").Click());

        cut.WaitForAssertion(() => Assert.Empty(hosts.Map));
        Assert.Empty(secrets.Map);
        Assert.Contains("No git hosts stored yet", cut.Markup);
    }

    [Fact]
    public async Task An_empty_host_shows_an_error_and_saves_nothing()
    {
        var (ctx, hosts, secrets) = Context();
        using var _ctx = ctx;
        var cut = ctx.Render<GitHostsSection>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='git-host-save']").Click());

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='git-host-error']")));
        Assert.Empty(hosts.Map);
        Assert.Empty(secrets.SetCalls);
    }

    [Theory]
    [InlineData("git.example.com", "git.example.com")]
    [InlineData("  GIT.Example.Com  ", "git.example.com")]
    [InlineData("https://git.example.com/some/path", "git.example.com")]
    [InlineData("git.example.com:3000", "git.example.com")]
    [InlineData("git.example.com/owner/repo", "git.example.com")]
    public void Host_normalization_strips_scheme_path_port_and_case(string raw, string expected) =>
        Assert.Equal(expected, GitHostsSection.NormalizeHost(raw));
}
