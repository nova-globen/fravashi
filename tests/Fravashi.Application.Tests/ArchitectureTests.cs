using System.Reflection;
using Fravashi.Application.Wiki;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// Project-graph constraints: the application layer depends on the domain only - never on Infrastructure,
/// Web, or ServiceDefaults. Keeps ports clean of adapter/host concerns.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly ApplicationAssembly = typeof(IWikiSyncService).Assembly;

    [Fact]
    public void Application_references_domain()
    {
        ApplicationAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Should()
            .Contain("Fravashi.Domain");
    }

    [Fact]
    public void Application_has_no_infrastructure_web_or_host_dependency()
    {
        var referenced = ApplicationAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        foreach (var forbidden in new[] { "Fravashi.Infrastructure", "Fravashi.Web", "Fravashi.ServiceDefaults" })
        {
            referenced.Should().NotContain(forbidden);
        }
    }
}
