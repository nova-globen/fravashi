// RETIRED to v2. Decided, 2026-09-22: Codex and Copilot are not part of this version and support for
// them is a v2 feature. The decision, everything that was measured about these two CLIs, and the
// read-containment question it parks are all in docs/v2.md, section 1 ("Codex and Copilot runtimes").
//
// The whole file is disabled rather than deleted, behind a symbol nothing defines. It is kept because the
// protocol it speaks was read off the pinned binary's own generated schema and bundled code (docs/v2.md
// Appendix B), and because a live Codex turn proved part of it against a real signed-in CLI. Re-deriving
// either in v2 would cost far more than carrying inert text here.
//
// Before restoring any of it, read docs/v2.md section 1.2 (the uid question, which is what stands in front of
// offering these runtimes at all) and 1.5 (a real, unfixed defect in CodexSessionRunner).
#if V2_CODEX_COPILOT

using System.Collections.Concurrent;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.AgentRuntimes;
using Fravashi.Infrastructure.Sessions;
using Xunit;
using Xunit.Abstractions;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Runs only when <c>FRAVASHI_CODEX_CLI</c> names a real Codex binary AND <c>FRAVASHI_CODEX_LIVE=1</c>,
/// because it takes real turns on somebody's account. PATH is never searched, for the reason the other live
/// tier gives: a personal install is not the version the product pins. The CLI has to be signed in already
/// under the HOME this runs with; no credential is passed by this test or read by it.
/// </summary>
internal sealed class LiveCodexCliFactAttribute : FactAttribute
{
    public const string PathEnvVar = "FRAVASHI_CODEX_CLI";
    public const string LiveEnvVar = "FRAVASHI_CODEX_LIVE";

    /// <summary>
    /// The model to ask for, when the CLI's own default is not one the signed-in account may use. Met on the
    /// first live run, 2026-09-21: the pinned CLI defaulted to a model the signed-in API project had no access
    /// to, and the turn ended in a 403 before any tool was tried.
    /// </summary>
    public const string ModelEnvVar = "FRAVASHI_CODEX_MODEL";

    public LiveCodexCliFactAttribute()
    {
        if (Path is null)
        {
            Skip = $"No real Codex CLI to test against. Set {PathEnvVar} to the pinned binary "
                + "(in the studio container it is /data/.npm-global/bin/codex).";
        }
        else if (!string.Equals(Environment.GetEnvironmentVariable(LiveEnvVar), "1", StringComparison.Ordinal))
        {
            Skip = $"Set {LiveEnvVar}=1 to run the live Codex tier. It takes real turns, so it needs a signed-in "
                + "CLI, network access, and it costs money.";
        }
    }

    public static string? Path
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(PathEnvVar);
            return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim()) ? configured.Trim() : null;
        }
    }
}

/// <summary>
/// The live proof <c>docs/plans/codex-copilot-runners.md</c> asks for before Codex may be offered (backlog
/// 66): one real turn in which a file write PARKS for approval and a denial HOLDS, then the same ask approved
/// and the file there. Every recording the other Codex tests use is constructed from the schema; this is the
/// only place the runner meets the real CLI, so what it prints is evidence and is written to the test output.
/// </summary>
public sealed class CodexCliLiveTests(ITestOutputHelper output)
{
    private static readonly TimeSpan TurnBudget = TimeSpan.FromMinutes(4);

    [LiveCodexCliFact]
    public async Task A_file_write_parks_for_approval_a_denial_holds_and_an_approval_lands()
    {
        var copy = Path.Combine(Path.GetTempPath(), "fravashi-codex-live", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        await File.WriteAllTextAsync(Path.Combine(copy, "README.md"), "A scratch folder for a live test.\n");
        var target = Path.Combine(copy, "proof.txt");

        var session = AgentSessionId.New();
        using var broker = new AgentApprovalBroker(timeout: TurnBudget);
        broker.RegisterSession(session, copy);

        var approve = false;
        var prompts = new ConcurrentQueue<PendingToolApproval>();
        broker.Requested += pending =>
        {
            prompts.Enqueue(pending);
            output.WriteLine($"ASKED   tool={pending.ToolName} detail={pending.Detail} warning={pending.Warning}");
            broker.Answer(pending.RequestId, approve, approve ? "approved by the live test" : "denied by the live test");
        };
        broker.Refused += refusal => output.WriteLine($"REFUSED tool={refusal.ToolName} reason={refusal.Reason}");

        var runner = new CodexSessionRunner(broker, launcher: new AgentCliLauncher(LiveCodexCliFactAttribute.Path!, []));
        var turns = new SemaphoreSlim(0);
        var events = new ConcurrentQueue<AgentEvent>();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(12));

        var run = Task.Run(async () =>
        {
            await foreach (var e in runner.RunAsync(
                new AgentSessionRequest(
                    session, "live", copy,
                    "Create a file named proof.txt in the current folder containing exactly the word hello. "
                    + "Do it with a tool now; do not ask me anything first.",
                    Model: Environment.GetEnvironmentVariable(LiveCodexCliFactAttribute.ModelEnvVar) is { Length: > 0 } model
                        ? model
                        : null),
                lifetime.Token))
            {
                events.Enqueue(e);
                output.WriteLine($"EVENT   {Describe(e)}");
                if (e is TurnCompleted or SessionEnded)
                {
                    turns.Release();
                }
            }
        });

        try
        {
            // ---- turn one: every ask is denied ------------------------------------------------------
            (await turns.WaitAsync(TurnBudget)).Should().BeTrue("the first turn should end within its budget");

            prompts.Should().NotBeEmpty(
                "a file write has to PARK for approval; a turn that ended without asking either wrote unattended "
                + "or never tried, and the events above say which");
            File.Exists(target).Should().BeFalse("every ask was denied, so nothing may have been written");

            if (events.Any(e => e is SessionEnded))
            {
                return; // the denial ended the session rather than the turn: measured, and said in the output
            }

            // ---- turn two: the same ask, approved ---------------------------------------------------
            approve = true;
            var before = prompts.Count;
            (await runner.SendAsync(
                session,
                "Try again now: create proof.txt containing exactly the word hello.",
                lifetime.Token)).Should().BeTrue();

            (await turns.WaitAsync(TurnBudget)).Should().BeTrue("the second turn should end within its budget");

            prompts.Count.Should().BeGreaterThan(before, "an approved change still asks, every time");
            File.Exists(target).Should().BeTrue("the write was approved");
            (await File.ReadAllTextAsync(target)).Should().Contain("hello");
        }
        finally
        {
            runner.Stop(session);
            await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));
            output.WriteLine($"SUMMARY prompts={prompts.Count} events={events.Count} file={File.Exists(target)}");
            try
            {
                Directory.Delete(copy, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string Describe(AgentEvent e) => e switch
    {
        MessageEmitted m => $"message {m.Role}: {Short(m.Text)}",
        PartialText => "partial text",
        ToolCallStarted t => $"tool started {t.ToolName}: {Short(t.Detail)}",
        ToolCallRefused r => $"tool refused {r.ToolName}",
        _ => e.GetType().Name,
    };

    private static string Short(string text) =>
        text.Length <= 160 ? text.ReplaceLineEndings(" ") : text[..160].ReplaceLineEndings(" ") + "...";
}
#endif
