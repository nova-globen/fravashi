using Bunit;
using Fravashi.Application.Framework;
using Fravashi.Application.Persistence;
using Fravashi.Domain.Framework;
using Fravashi.Web.Components.Settings.Framework;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// The remaining T3 kinds (UI11): commands, hooks, MCP servers and plugins. Each form persists a valid artifact
/// to the store; the MCP form additionally routes secret env values to the secret store and keeps them out of
/// the stored payload (the owner's secret-handling requirement).
/// </summary>
public class RemainingArtifactComponentTests
{
    private static BunitContext Ctx(out FakeFrameworkArtifactStore store, out FakeSecretStore secrets)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        store = new FakeFrameworkArtifactStore();
        secrets = new FakeSecretStore();
        ctx.Services.AddSingleton<IFrameworkArtifactStore>(store);
        ctx.Services.AddSingleton<ISecretStore>(secrets);
        ctx.Services.AddSingleton(new FluentIconCatalog());
        return ctx;
    }

    // ── Commands ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Command_form_persists_a_valid_command()
    {
        using var ctx = Ctx(out var store, out _);
        var cut = ctx.Render<CommandForm>();

        cut.Find("[data-testid=command-name]").Input("review-pr");
        cut.Find("[data-testid=command-description]").Input("Review the open PR.");
        cut.Find("[data-testid=command-body]").Input("Review $ARGUMENTS.");
        cut.Find("[data-testid=command-save]").Click();

        var saved = Assert.Single(store.Artifacts.Values);
        Assert.Equal(FrameworkArtifactKind.Command, saved.Kind);
        Assert.Equal("review-pr", saved.Name);
    }

    [Fact]
    public void Command_form_rejects_a_missing_body()
    {
        using var ctx = Ctx(out var store, out _);
        var cut = ctx.Render<CommandForm>();

        cut.Find("[data-testid=command-name]").Input("review-pr");
        cut.Find("[data-testid=command-description]").Input("Review the open PR.");
        cut.Find("[data-testid=command-save]").Click();

        Assert.NotEmpty(cut.FindAll("[data-testid=command-error-body]"));
        Assert.Empty(store.Artifacts);
    }

    // ── Hooks ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hook_form_persists_the_selected_event_and_command()
    {
        using var ctx = Ctx(out var store, out _);
        var cut = ctx.Render<HookForm>();

        cut.Find("[data-testid=hook-name]").Input("format-on-edit");
        cut.Find("[data-testid=hook-event]").Change(nameof(HookEvent.PostToolUse));
        cut.Find("[data-testid=hook-matcher]").Input("Edit");
        cut.Find("[data-testid=hook-command]").Input("prettier --write .");
        cut.Find("[data-testid=hook-save]").Click();

        var saved = Assert.Single(store.Artifacts.Values);
        var hook = FrameworkPayload<HookDefinition>.From(saved);
        Assert.Equal(HookEvent.PostToolUse, hook.Event);
        Assert.Equal("Edit", hook.Matcher);
        Assert.Equal("prettier --write .", hook.Command);
    }

    // ── MCP servers (secret handling) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Mcp_form_routes_a_secret_env_value_to_the_secret_store_and_not_the_payload()
    {
        using var ctx = Ctx(out var store, out var secrets);
        var cut = ctx.Render<McpServerForm>();

        cut.Find("[data-testid=mcp-name]").Input("airtable");
        cut.Find("[data-testid=mcp-command]").Input("npx");

        // Add one env row, mark it secret, and give it a value.
        cut.Find("[data-testid=mcp-env-add]").Click();
        cut.Find("[data-testid=mcp-env-key]").Input("AIRTABLE_API_KEY");
        cut.Find("[data-testid=mcp-env-secret]").Change(true);
        cut.Find("[data-testid=mcp-env-value]").Input("super-secret-token");

        cut.Find("[data-testid=mcp-save]").Click();

        var saved = Assert.Single(store.Artifacts.Values);
        // The plaintext lives in the secret store, referenced by the payload - never in the payload itself.
        Assert.Contains("super-secret-token", secrets.Secrets.Values);
        Assert.DoesNotContain("super-secret-token", saved.PayloadJson);

        var server = FrameworkPayload<McpServerDefinition>.From(saved);
        var entry = Assert.Single(server.Env);
        Assert.True(entry.IsSecret);
        Assert.Null(entry.Value);
        Assert.False(string.IsNullOrWhiteSpace(entry.SecretRef));
        Assert.Equal(secrets.Secrets.Keys.Single(), entry.SecretRef);
    }

    [Fact]
    public void Mcp_form_keeps_a_non_secret_env_value_inline()
    {
        using var ctx = Ctx(out var store, out var secrets);
        var cut = ctx.Render<McpServerForm>();

        cut.Find("[data-testid=mcp-name]").Input("airtable");
        cut.Find("[data-testid=mcp-command]").Input("npx");
        cut.Find("[data-testid=mcp-env-add]").Click();
        cut.Find("[data-testid=mcp-env-key]").Input("AIRTABLE_BASE");
        cut.Find("[data-testid=mcp-env-value]").Input("app123");
        cut.Find("[data-testid=mcp-save]").Click();

        var saved = Assert.Single(store.Artifacts.Values);
        Assert.Empty(secrets.Secrets);
        var server = FrameworkPayload<McpServerDefinition>.From(saved);
        var entry = Assert.Single(server.Env);
        Assert.False(entry.IsSecret);
        Assert.Equal("app123", entry.Value);
    }

    [Fact]
    public void Mcp_form_requires_a_command_for_stdio()
    {
        using var ctx = Ctx(out var store, out _);
        var cut = ctx.Render<McpServerForm>();

        cut.Find("[data-testid=mcp-name]").Input("airtable");
        cut.Find("[data-testid=mcp-save]").Click();

        Assert.NotEmpty(cut.FindAll("[data-testid=mcp-error-command]"));
        Assert.Empty(store.Artifacts);
    }

    // ── Plugins ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Plugin_form_persists_and_rejects_a_bad_version()
    {
        using var ctx = Ctx(out var store, out _);
        var cut = ctx.Render<PluginForm>();

        cut.Find("[data-testid=plugin-name]").Input("deploy-tools");
        cut.Find("[data-testid=plugin-version]").Input("nope");
        cut.Find("[data-testid=plugin-save]").Click();
        Assert.NotEmpty(cut.FindAll("[data-testid=plugin-error-version]"));
        Assert.Empty(store.Artifacts);

        cut.Find("[data-testid=plugin-version]").Input("1.0.0");
        cut.Find("[data-testid=plugin-save]").Click();
        var saved = Assert.Single(store.Artifacts.Values);
        Assert.Equal(FrameworkArtifactKind.Plugin, saved.Kind);
    }
}
