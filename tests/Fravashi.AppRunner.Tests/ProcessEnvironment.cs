using Xunit;

namespace Fravashi.AppRunner.Tests;

/// <summary>
/// Backlog 226: tests that set this process's <c>FRAVASHI_WINDOW_TOKEN</c> (to prove a run's minted token wins over an
/// inherited one) share one collection, so they never run at the same time and restore each other's value wrongly.
/// </summary>
[CollectionDefinition(Collection)]
public sealed class ProcessEnvironment
{
    public const string Collection = "process environment";
}
