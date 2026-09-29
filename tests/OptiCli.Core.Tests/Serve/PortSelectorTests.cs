using OptiCli.Core.Errors;
using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Serve;

public class PortSelectorTests
{
    [Fact]
    public void Prefers_the_configured_port_then_the_default()
    {
        Assert.Equal(5250, PortSelector.Select(null, 5250, _ => true));
        Assert.Equal(PortSelector.DefaultPort, PortSelector.Select(null, null, _ => true));
    }

    [Fact]
    public void Falls_back_to_the_first_free_port_in_the_range()
    {
        var taken = new HashSet<int> { 5199, 5200, 5201 };

        Assert.Equal(5202, PortSelector.Select(null, null, port => !taken.Contains(port)));
        // A taken configured port outside the range still falls back into the range.
        Assert.Equal(5199, PortSelector.Select(null, 8080, port => port != 8080));
    }

    [Fact]
    public void An_explicit_port_is_used_as_given_or_refused_when_taken()
    {
        Assert.Equal(6000, PortSelector.Select(6000, 5250, _ => true));
        Assert.Throws<UsageException>(() => PortSelector.Select(6000, null, _ => false));
        Assert.Throws<UsageException>(() => PortSelector.Select(0, null, _ => true));
    }

    [Fact]
    public void A_full_range_is_an_error()
    {
        Assert.Throws<UsageException>(() => PortSelector.Select(null, null, _ => false));
    }

    [Fact]
    public void Probing_sees_a_listening_socket()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        Assert.False(PortSelector.IsFree(port));
    }
}
