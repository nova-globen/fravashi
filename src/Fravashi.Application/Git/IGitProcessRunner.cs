namespace Fravashi.Application.Git;

/// <summary>The outcome of one git invocation: its exit code and captured streams.</summary>
/// <param name="ExitCode">The process exit code; 0 is success. A start failure is reported as -1.</param>
/// <param name="StdOut">Captured standard output.</param>
/// <param name="StdErr">Captured standard error (git writes progress and errors here).</param>
public sealed record GitProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// A seam over the git command line so the clone/fetch operations are unit-testable without a real git binary
/// (a fake can record the arguments and environment, prove no credential leaks onto the command line, and
/// simulate the working-copy filesystem). The single real implementation shells out to <c>git</c>.
/// <para>
/// The credential is <b>never</b> passed as an argument or embedded in the URL - it travels only through the
/// <paramref name="environment"/> (the <c>GIT_CONFIG_*</c> runtime-config vars carrying an
/// <c>http.extraHeader</c>), exactly like the onboarding wiki round-trip probe. That keeps the token off the
/// command line and out of <c>.git/config</c>.
/// </para>
/// </summary>
public interface IGitProcessRunner
{
    /// <summary>
    /// Runs <c>git</c> with <paramref name="arguments"/> in <paramref name="workingDirectory"/>, injecting
    /// <paramref name="environment"/>. <paramref name="onStderrLine"/> receives each stderr line as it arrives
    /// (git streams <c>--progress</c> there), so a caller can surface live progress.
    /// </summary>
    Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        Action<string>? onStderrLine,
        CancellationToken cancellationToken,
        params string[] arguments);
}
