using AwesomeAssertions;
using Xunit;

namespace Fravashi.Domain.Tests;

public class ServiceHostTests
{
    [Fact]
    public void A_build_that_names_no_host_is_production()
    {
        // The test run is a plain build, so this is also what a dev studio and every unnamed build get.
        ServiceHost.Current.Should().Be(ServiceHost.Production);
        ServiceHost.Issuer.Should().Be("https://nova-globen.se");
        ServiceHost.BrokerBaseUrl.Should().Be("https://nova-globen.se/fravashi/api");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_build_value_is_production(string? built) =>
        ServiceHost.Resolve(built).Should().Be(ServiceHost.Production);

    [Theory]
    [InlineData("example.test")]
    [InlineData("staging.example.test")]
    [InlineData(" example.test ")]
    public void A_bare_host_name_is_taken(string built) =>
        ServiceHost.Resolve(built).Should().Be(built.Trim());

    [Theory]
    [InlineData("https://example.test")]
    [InlineData("example.test/fravashi")]
    [InlineData("example.test:8443")]
    [InlineData("Example.test")]
    [InlineData("localhost")]
    [InlineData(".example.test")]
    [InlineData("example.test.")]
    [InlineData("-example.test")]
    public void Anything_but_a_bare_host_name_fails_the_start(string built)
    {
        var resolve = () => ServiceHost.Resolve(built);
        resolve.Should().Throw<InvalidOperationException>().WithMessage("*not a lower-case host name*");
    }
}
