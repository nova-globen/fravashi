using Fravashi.Infrastructure.Sessions;

namespace Fravashi.Web.Services;

/// <summary>
/// Backlog 205: <see cref="LiveSessionRegistry.ResumeInterruptedConversations"/> (backlog 156) starts a CLI
/// child, so it must not run while <c>Program.cs</c> is still building the pipeline - that child's first call
/// to the studio's tool server would race Kestrel's bind. <see cref="IHostApplicationLifetime.ApplicationStarted"/>
/// fires only after the host has started every hosted service, including the one that binds and starts the
/// server, so registering the call there guarantees the tool server is already accepting connections first.
/// <see cref="LiveSessionRegistry.RecoverInterruptedToolCalls"/> (backlog 84) stays out of this: it starts no
/// CLI and only touches the ledger and the store, and it has to run before this, so it keeps running
/// synchronously in <c>Program.cs</c>, before even that.
/// </summary>
public static class StudioStartupResume
{
    public static void RunAfterStart(IHostApplicationLifetime lifetime, LiveSessionRegistry registry) =>
        lifetime.ApplicationStarted.Register(() => registry.ResumeInterruptedConversations());
}
