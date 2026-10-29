using AwesomeAssertions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Infrastructure.AgentRuntimes;
using Fravashi.Infrastructure.Backup;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Evidence;
using Fravashi.Infrastructure.Framework;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Integrity;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Infrastructure.Onboarding;
using Fravashi.Infrastructure.Onboarding.Auth;
using Fravashi.Infrastructure.PostOnboarding;
using Fravashi.Infrastructure.PullRequests;
using Fravashi.Infrastructure.Telemetry;
using Fravashi.Infrastructure.Tracker;
using Fravashi.Infrastructure.Updates;
using Fravashi.Infrastructure.Variants;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Repository;
using Fravashi.Infrastructure.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// The container the application actually builds, built.
/// <para>
/// This exists because of a real escape. v0.9.23 shipped a studio that CRASHED ON STARTUP with
/// "No service for type 'GitCliService' has been registered", while 3941 tests passed. Every test that
/// touched the offending type constructed it directly with <c>new</c>, handing it finished objects, so
/// the suite verified a shape the application never builds. The composition root was the one part of the
/// system nothing exercised, and it is the part that decides whether the product starts at all.
/// </para>
/// <para>
/// <b>The teeth are in RESOLVING, not in ValidateOnBuild.</b> That option only walks descriptors whose
/// dependencies it can see, which means constructor-injected ones; it cannot look inside a factory
/// lambda, and this codebase registers nearly everything as <c>sp => new Thing(sp.GetRequiredService...)</c>.
/// The v0.9.23 crash was inside exactly such a lambda, so ValidateOnBuild alone would have missed it and
/// this file would have been theatre. What catches it is asking the container for the services, which
/// forces every lambda to run. ValidateOnBuild is kept because it is free and catches the other shape,
/// and <c>ValidateScopes</c> earns its place separately: it turns a singleton holding a scoped service
/// into a failure, which was the second half of that same bug.
/// </para>
/// </summary>
public class CompositionRootTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "fravashi-tests", Guid.NewGuid().ToString("N"));

    public CompositionRootTests() => Directory.CreateDirectory(_dataDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp folder.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Every registration group Program.cs makes, in its order. This deliberately calls the SAME
    /// extension methods the application calls rather than registering the pieces by hand: a test that
    /// hand-registers cannot notice that the real chain has a hole in it.
    /// </summary>
    private ServiceProvider Build()
    {
        // A REAL host builder rather than a bare ServiceCollection. Program.cs builds one, and the host
        // is what supplies IConfiguration, IHostEnvironment and logging; a hand-built collection is
        // missing them and a test that stubs them one by one is slowly rebuilding the host badly.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        var configuration = builder.Configuration;
        var services = builder.Services;

        // The one thing Program.cs registers INLINE that this chain needs. Stubbed because encrypting a
        // secret is not what this test is about.
        services.AddSingleton<Fravashi.Application.Persistence.ISecretProtector, PassThroughProtector>();

        services.AddFravashiPersistence(new DataDirectory(_dataDirectory), null);
        // EVERY registration group Program.cs makes, in its order, minus the ones that are purely
        // web-host concerns (Razor, Fluent UI, data protection, the HTTP context accessor). The agent
        // session chain was where the crash happened, but nothing else here was guarded either, and a
        // composition root is only as sound as its least-exercised corner.
        services.AddOnboardingProbes();
        services.AddProviderAuth();
        services.AddAgentRuntimes(configuration);
        services.AddFrameworkArtifacts();
        services.AddTrackerIntegration(configuration);
        services.AddNotifications(configuration);
        services.AddUpdateNotifications();
        services.AddSandboxHardening(configuration);
        services.AddProductTelemetry();
        services.AddStudioBackup();
        services.AddIntegrityChecks();
        services.AddPostOnboardingSync();
        services.AddRepositoryExplorer(configuration);
        services.AddGitService();
        services.AddBridgeWorkspaces();
        services.AddAgentSessions(configuration);
        services.AddEvidenceLifecycle();
        services.AddPullRequests();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>Stands in for Data Protection, which this test has no reason to configure.</summary>
    private sealed class PassThroughProtector : Fravashi.Application.Persistence.ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string protectedValue) => protectedValue;
    }

    [Fact]
    public async Task The_registrations_form_a_container_that_can_actually_be_built()
    {
        // ValidateOnBuild walks every registration and resolves its constructor arguments. A missing
        // type fails HERE, with the type named, instead of at the user's first launch with a stack trace
        // in a container log.
        // Disposed asynchronously on purpose: WorkspaceAgentSync implements only IAsyncDisposable, so a
        // synchronous Dispose on this container throws, and a test that used `using var` would report a
        // disposal complaint where a reader expects a registration failure.
        var build = () => Build();

        build.Should().NotThrow();
        await using var provider = Build();
    }

    [Fact]
    public async Task Every_studio_tool_resolves()
    {
        // The tool set is registered as many IStudioTool singletons. Resolving the enumerable is what
        // the tool server does, and it is the call that threw on startup in v0.9.23.
        await using var provider = Build();

        var tools = provider.GetRequiredService<IEnumerable<IStudioTool>>().ToList();

        tools.Should().NotBeEmpty();
        tools.Select(t => t.Name).Should().OnlyHaveUniqueItems("a duplicate tool name would shadow one silently");
    }

    [Fact]
    public async Task Every_registered_studio_tool_has_a_readable_label()
    {
        // Work item #351: a studio tool's transcript row says what it did in words, never only its raw name. A tool
        // added without an entry in the one describer fails here, by name.
        await using var provider = Build();

        var missing = provider.GetRequiredService<IEnumerable<IStudioTool>>()
            .Where(tool => string.IsNullOrWhiteSpace(StudioToolDescriptions.Label(tool.Name, null)))
            .Select(tool => tool.Name)
            .ToList();

        missing.Should().BeEmpty("every studio tool needs a label in StudioToolDescriptions");
    }

    [Fact]
    public async Task Every_registered_studio_tool_has_an_approval_sentence()
    {
        // Work item #351: an approval card says in one sentence what the call will do. Prefixed exactly as the CLI
        // names the tool on the card, so the prefix handling is held too.
        await using var provider = Build();

        var missing = provider.GetRequiredService<IEnumerable<IStudioTool>>()
            .Where(tool => string.IsNullOrWhiteSpace(StudioToolDescriptions.ApprovalSentence(
                Fravashi.Domain.AgentSessions.AgentWorkGate.StudioToolPrefix + tool.Name, null)))
            .Select(tool => tool.Name)
            .ToList();

        missing.Should().BeEmpty("every studio tool needs an approval sentence in StudioToolDescriptions");
    }

    [Fact]
    public async Task Everything_Program_resolves_at_startup_resolves()
    {
        // These are the exact types Program.cs asks for from app.Services AFTER the container is built,
        // which is where the v0.9.23 crash actually surfaced (line 357, LiveSessionRegistry). Resolving
        // them here is what forces their factory lambdas to run, and a factory lambda is the one shape
        // ValidateOnBuild cannot inspect. If this list and Program.cs drift apart, the gap is exactly the
        // place a startup crash can hide again.
        await using var provider = Build();

        provider.GetRequiredService<Fravashi.Application.Persistence.IProviderConnectionStore>().Should().NotBeNull();
        provider.GetRequiredService<Fravashi.Application.Persistence.ISecretStore>().Should().NotBeNull();
        provider.GetRequiredService<Fravashi.Application.Persistence.IConfigurationStore>().Should().NotBeNull();
        provider.GetRequiredService<LiveSessionRegistry>().Should().NotBeNull();
    }

    [Fact]
    public async Task Everything_the_middleware_gates_resolve_per_request_resolves()
    {
        // The gate order in Program.cs (integrity, critical update, VPN, sign-in, onboarding) resolves
        // these from RequestServices on the FIRST request. A missing one would not crash startup: it
        // would make every page 500 instead, which is a worse failure because the container looks
        // healthy. They are scoped, so they are resolved through a scope here, as they are in the app.
        await using var provider = Build();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<Fravashi.Application.Integrity.IIntegrityStatusProvider>().Should().NotBeNull();
        sp.GetRequiredService<Fravashi.Application.Updates.IProductUpdateStatus>().Should().NotBeNull();
        sp.GetRequiredService<Fravashi.Application.Variants.IFeatureFlags>().Should().NotBeNull();
        sp.GetRequiredService<Fravashi.Application.Vpn.IVpnConnectivity>().Should().NotBeNull();
        sp.GetRequiredService<Fravashi.Application.Persistence.IOnboardingState>().Should().NotBeNull();
    }

    [Fact]
    public async Task The_attempt_git_work_resolves_as_a_singleton_without_capturing_a_scoped_service()
    {
        // Backlog 86. SessionGitWork is a singleton and the git seams are scoped, so it must reach them
        // through a scope factory rather than hold them. ValidateScopes turns getting that wrong into a
        // failure; before this test, getting it wrong shipped.
        await using var provider = Build();

        provider.GetRequiredService<SessionGitWork>().Should().NotBeNull();
    }

    [Fact]
    public async Task The_wiki_publisher_resolves_in_a_scope_the_way_the_wiki_page_asks_for_it()
    {
        // Backlog 83. The wiki page injects this, so a missing piece would not stop the product starting:
        // it would make /wiki/{n} fail the moment a document is opened. It is a factory lambda over four
        // other registrations made in two different extension methods, which is the shape item 86 shipped
        // broken.
        await using var provider = Build();
        using var scope = provider.CreateScope();

        var publisher = scope.ServiceProvider.GetRequiredService<Fravashi.Application.Wiki.WikiPublisher>();

        publisher.Provider.Should().Be("Confluence");
        publisher.CanPublish.Should().BeFalse("nothing is connected in this container");
    }
}
