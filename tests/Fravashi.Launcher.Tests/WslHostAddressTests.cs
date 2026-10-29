using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

public sealed class WslHostAddressTests
{
    [Fact]
    public void Resolve_PicksTheUpWslInterface()
    {
        var address = WslHostAddress.Resolve(() =>
        [
            ("Ethernet", true, ["10.0.0.10"]),
            ("vEthernet (WSL (Hyper-V firewall))", true, ["172.23.160.1"]),
        ]);

        address.Should().Be("172.23.160.1");
    }

    [Fact]
    public void Resolve_IgnoresDownInterfaces()
    {
        var address = WslHostAddress.Resolve(() =>
        [
            ("vEthernet (WSL)", false, ["172.23.160.1"]),
        ]);

        address.Should().BeNull();
    }

    [Fact]
    public void Resolve_ReturnsNullWhenNoWslInterfaceExists()
    {
        var address = WslHostAddress.Resolve(() =>
        [
            ("Ethernet", true, ["10.0.0.10"]),
            ("Loopback", true, ["127.0.0.1"]),
        ]);

        address.Should().BeNull();
    }

    [Fact]
    public void Resolve_SkipsWslInterfacesWithoutIpv4()
    {
        var address = WslHostAddress.Resolve(() =>
        [
            ("vEthernet (WSL)", true, []),
        ]);

        address.Should().BeNull();
    }
}
