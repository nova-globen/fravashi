using System.Runtime.CompilerServices;

namespace Fravashi.Infrastructure.Tests;

/// <summary>
/// What this test assembly must not inherit from whoever started it. A run inside an agent session (the self-hosting
/// trials run the whole suite from one) inherits that session's <c>CLAUDE_CONFIG_DIR</c>, and the stand-in CLI the
/// session-runner tests start writes its transcripts under it: a fixture named <c>fake-cli-session-id</c> landed in a
/// REAL session's config home on 2026-09-29 (backlog 169). No test means to use an inherited one; a test that wants a
/// config home sets its own on the runner it builds.
/// </summary>
internal static class TestProcessEnvironment
{
#pragma warning disable CA2255 // A module initializer in a test assembly is the one place that runs before every test.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void ForgetAnInheritedAgentConfigHome() =>
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
}
