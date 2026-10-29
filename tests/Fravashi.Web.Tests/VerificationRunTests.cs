using System.Text.RegularExpressions;
using Fravashi.Application.Persistence;
using Fravashi.Application.Verification;
using Fravashi.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 226 (plan @69): the verification-run flag is honoured only with the matching window token of the same start,
/// and then turns off the sign-in gate and makes onboarding read as complete for that process and nothing else. No gate
/// gained an exemption: the window-only gate's list is held byte for byte, and Program.cs is read to show that the
/// sign-in gate is the only gate that consults the flag.
/// </summary>
public sealed class VerificationRunTests
{
    private static readonly string Token = new string('7', 32) + new string('a', 32);
    private static readonly string Other = new string('7', 32) + new string('b', 32);

    // ---- the decision ---------------------------------------------------------------------------------------------

    [Fact]
    public void No_flag_is_not_a_verification_run_and_says_nothing()
    {
        var run = VerificationRun.Decide(null, Token, Token);

        Assert.False(run.Present);
        Assert.False(run.Honoured);
        Assert.Null(run.Refusal);
    }

    [Fact]
    public void The_flag_with_the_matching_window_token_of_the_same_start_is_honoured()
    {
        var run = VerificationRun.Decide(Token, Token, Token);

        Assert.True(run.Honoured);
        Assert.Null(run.Refusal);
    }

    public static TheoryData<string?, string?, string?, string> Refused() => new()
    {
        { Token, null, null, "carries no FRAVASHI_WINDOW_TOKEN" },
        { Token, string.Empty, Token, "carries no FRAVASHI_WINDOW_TOKEN" },
        { Token, Other, Other, "does not match" },
        { "yes", "yes", "yes", "not a token the app runner mints" },
        { Token.ToUpperInvariant(), Token.ToUpperInvariant(), Token.ToUpperInvariant(), "not a token the app runner mints" },
        { Token + "0", Token + "0", Token + "0", "not a token the app runner mints" },
        { Token, Token, null, "the window-only gate enforces" },
        { Token, Token, Other, "the window-only gate enforces" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void The_flag_without_the_matching_token_is_refused_and_both_gates_stay(string? flag, string? start, string? enforced, string why)
    {
        var run = VerificationRun.Decide(flag, start, enforced);

        Assert.True(run.Present);
        Assert.False(run.Honoured);
        Assert.Contains(why, run.Refusal, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, run.Refusal!, StringComparison.Ordinal);
        Assert.DoesNotContain(Other, run.Refusal!, StringComparison.Ordinal);
        Assert.True(run.SignInRequired(true));

        var services = OnboardingServices();
        var before = services.ToList();
        run.ApplyTo(services);
        Assert.Equal(before, services.ToList());
    }

    [Fact]
    public void The_process_reads_the_flag_and_the_token_from_its_environment_never_from_configuration()
    {
        // A settings-table row reaches the studio through configuration; it must not be able to switch the flag on.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [VerificationRun.EnvironmentVariable] = Token,
            [WindowOnlyGate.TokenConfigKey] = Token,
        }).Build();

        var run = VerificationRun.FromProcess(configuration);

        // This test process carries neither variable, so whatever configuration says, nothing is honoured.
        Assert.Null(Environment.GetEnvironmentVariable(VerificationRun.EnvironmentVariable));
        Assert.False(run.Present);
        Assert.False(run.Honoured);
    }

    // ---- what it does when honoured -------------------------------------------------------------------------------

    [Fact]
    public async Task Honoured_the_sign_in_gate_admits_without_a_session_and_refused_it_redirects()
    {
        var honoured = VerificationRun.Decide(Token, Token, Token);
        var refused = VerificationRun.Decide(Token, Other, Other);
        var absent = VerificationRun.Decide(null, null, null);

        Assert.True(await SignInGate.AdmitsAsync(() => honoured.SignInRequired(true), () => false, _ => Task.FromResult(false), default));
        Assert.False(await SignInGate.AdmitsAsync(() => refused.SignInRequired(true), () => false, _ => Task.FromResult(false), default));
        Assert.False(await SignInGate.AdmitsAsync(() => absent.SignInRequired(true), () => false, _ => Task.FromResult(false), default));
        Assert.False(honoured.SignInRequired(false));
    }

    [Fact]
    public void Honoured_onboarding_reads_as_complete_for_this_process_and_nothing_is_written()
    {
        var store = new RecordingOnboarding();
        var services = new ServiceCollection();
        services.AddSingleton<IOnboardingState>(store);
        VerificationRun.Decide(Token, Token, Token).ApplyTo(services);

        using var provider = services.BuildServiceProvider();
        var onboarding = provider.GetRequiredService<IOnboardingState>();

        Assert.IsType<VerificationRunOnboardingState>(onboarding);
        Assert.True(onboarding.IsComplete);
        Assert.Equal(new OnboardingStatus(true, "welcome", null, null), onboarding.Get());
        Assert.Empty(store.Writes);
        Assert.False(store.IsComplete, "the store itself still says incomplete: nothing was written to it");
    }

    [Fact]
    public void The_decorator_keeps_a_type_registration_and_resolves_it_from_the_container()
    {
        var services = OnboardingServices();
        VerificationRun.Decide(Token, Token, Token).ApplyTo(services);

        Assert.Single(services, d => d.ServiceType == typeof(IOnboardingState));
        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<IOnboardingState>().IsComplete);
    }

    [Fact]
    public void The_marker_the_design_check_presents_is_what_the_runs_window_gate_admits()
    {
        var marker = WindowMarker.For("http://fravashi-run-r1-relay:8080", Token)!;
        var agent = marker.UserAgent("Mozilla/5.0 (X11; Linux x86_64) Chrome/140.0 Safari/537.36");

        Assert.True(WindowOnlyGate.IsAllowed(agent, Token));
        Assert.False(WindowOnlyGate.IsAllowed(agent, Other)); // another token, such as the live studio's, is not this one
    }

    // ---- the log ------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_log_says_what_was_decided_and_never_carries_a_value()
    {
        var honoured = new ListLogger();
        VerificationRun.Decide(Token, Token, Token).Log(honoured);
        var refused = new ListLogger();
        VerificationRun.Decide(Token, Other, Other).Log(refused);
        var absent = new ListLogger();
        VerificationRun.Decide(null, Token, Token).Log(absent);

        Assert.Contains("verification run (backlog 226)", Assert.Single(honoured.Lines), StringComparison.Ordinal);
        var line = Assert.Single(refused.Lines);
        Assert.Contains("refused", line, StringComparison.Ordinal);
        Assert.Contains("does not match", line, StringComparison.Ordinal);
        Assert.Empty(absent.Lines);
        Assert.All(honoured.Lines.Concat(refused.Lines), l =>
        {
            Assert.DoesNotContain(Token, l, StringComparison.Ordinal);
            Assert.DoesNotContain(Other, l, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(Token, VerificationRun.Decide(Token, Token, Token).ToString(), StringComparison.Ordinal);
    }

    // ---- no gate gained an exemption ----------------------------------------------------------------------------------

    [Fact]
    public void The_window_only_gates_exempt_list_is_byte_for_byte_what_it_was()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Fravashi.Web", "Services", "WindowOnlyGate.cs"));
        var expected = string.Join(
            Newline(source),
            "    public static bool IsExempt(string path) =>",
            "        path.StartsWith(\"/healthz\", StringComparison.OrdinalIgnoreCase)",
            "        || path.StartsWith(\"/api\", StringComparison.OrdinalIgnoreCase)",
            "        || path.StartsWith(\"/auth/callback\", StringComparison.OrdinalIgnoreCase);");

        Assert.Contains(expected, source, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(source, "StartsWith\\(").Count);
        Assert.DoesNotContain("erification", source, StringComparison.Ordinal);

        Assert.True(WindowOnlyGate.IsExempt("/healthz"));
        Assert.True(WindowOnlyGate.IsExempt("/api/onboarding/status"));
        Assert.True(WindowOnlyGate.IsExempt("/auth/callback/x"));
        foreach (var gated in new[] { "/", "/sign-in", "/onboarding", "/verification", "/_blazor", "/_framework/blazor.web.js", "/workitems/1" })
        {
            Assert.False(WindowOnlyGate.IsExempt(gated), gated);
        }
    }

    [Fact]
    public void Of_the_gates_only_the_sign_in_gate_consults_the_flag_and_no_other_web_file_names_it()
    {
        var root = RepositoryRoot();
        // Backlog 282: the five redirecting gates' conditions moved from Program.cs's inline middlewares to
        // StudioGates.cs, so the middleware and the circuit-side guard ask one decision. Program.cs's middlewares
        // consult no flag at all now.
        var program = File.ReadAllText(Path.Combine(root, "src", "Fravashi.Web", "Program.cs"));
        var middlewares = program.Split("app.Use(async (context, next) =>").Skip(1).ToList();
        Assert.True(middlewares.Count >= 2, "the window-only gate and the gate middleware");
        Assert.DoesNotContain(middlewares, m => m.Contains("verificationRun", StringComparison.Ordinal));

        var studioGates = File.ReadAllText(Path.Combine(root, "src", "Fravashi.Web", "Services", "StudioGates.cs"));
        var gates = studioGates.Split("new StudioGate(").Skip(1).ToList();

        Assert.Equal(5, gates.Count); // integrity, critical update, VPN, sign-in and first-run
        var consulting = Assert.Single(gates, g => g.Contains("erificationRun", StringComparison.Ordinal));
        Assert.Contains("SignInGate.AdmitsAsync", consulting, StringComparison.Ordinal);
        Assert.Contains("verificationRun.SignInRequired(session.Required)", consulting, StringComparison.Ordinal);
        Assert.Contains(
            "!services.GetRequiredService<IOnboardingState>().IsComplete",
            Assert.Single(gates, g => g.Contains("IOnboardingState", StringComparison.Ordinal)),
            StringComparison.Ordinal);

        var separator = Path.DirectorySeparatorChar;
        var mentions = Directory.GetFiles(Path.Combine(root, "src", "Fravashi.Web"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
                        && !f.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                        && !f.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("VerificationRun", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["Program.cs", "StudioGates.cs", "VerificationRun.cs"], mentions);
    }

    [Fact]
    public void Nothing_installed_sets_the_flag_the_launcher_the_desktop_host_the_bridge_the_image_and_setup_never_name_it()
    {
        var root = RepositoryRoot();
        var separator = Path.DirectorySeparatorChar;
        var places = new[]
            {
                Path.Combine(root, "src", "Fravashi.Launcher"),
                Path.Combine(root, "src", "Fravashi.Desktop"),
                Path.Combine(root, "src", "Fravashi.Bridge"),
                Path.Combine(root, "setup"),
            }
            .SelectMany(d => Directory.GetFiles(d, "*.*", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                        && !f.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                        && Path.GetExtension(f) is ".cs" or ".json" or ".sh" or ".ps1" or ".iss")
            .Append(Path.Combine(root, "Dockerfile"));

        Assert.DoesNotContain(places, f => File.ReadAllText(f).Contains(VerificationRun.EnvironmentVariable, StringComparison.Ordinal));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static ServiceCollection OnboardingServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOnboardingState, RecordingOnboarding>();
        return services;
    }

    private sealed class RecordingOnboarding : IOnboardingState
    {
        public List<string> Writes { get; } = [];

        public bool IsComplete => false;

        public OnboardingStatus Get() => new(false, "welcome", null, null);

        public void SetCurrentStep(string step) => Writes.Add("step " + step);

        public void MarkComplete() => Writes.Add("complete");

        public void Reset() => Writes.Add("reset");
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private static string Newline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fravashi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (Fravashi.sln) was not found.");
    }
}
