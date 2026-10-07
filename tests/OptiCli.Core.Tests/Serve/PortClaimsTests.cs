using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Serve;

/// <summary>Two serves starting at the same moment don't pick the same free port.</summary>
public class PortClaimsTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void A_port_another_running_process_claimed_is_taken_and_select_moves_on()
    {
        var claims = new PortClaims(_root.Path);

        Assert.True(claims.TryClaim(5199, 1001, _ => true, Now));
        Assert.False(claims.TryClaim(5199, 1002, _ => true, Now));
        // The same process may claim it again (a second probe, the https port).
        Assert.True(claims.TryClaim(5199, 1001, _ => true, Now));

        var other = new PortClaims(_root.Path);
        Assert.Equal(5200, PortSelector.Select(null, null, port => other.TryClaim(port, 1002, _ => true, Now)));
    }

    [Fact]
    public void A_claim_whose_process_is_gone_or_that_is_old_is_taken_over()
    {
        var claims = new PortClaims(_root.Path);
        Assert.True(claims.TryClaim(5199, 1001, _ => true, Now));

        Assert.True(claims.TryClaim(5199, 1002, id => id != 1001, Now));
        Assert.False(claims.TryClaim(5199, 1003, _ => true, Now));
        Assert.True(claims.TryClaim(5199, 1003, _ => true, Now + PortClaims.MaxAge + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void A_folder_that_cant_be_used_claims_nothing_and_blocks_nothing()
    {
        var file = Path.Combine(_root.Path, "not-a-folder");
        File.WriteAllText(file, "x");

        Assert.True(new PortClaims(file).TryClaim(5199, 1001, _ => true, Now));
    }

    [Fact]
    public void The_probe_claims_only_ports_that_are_free()
    {
        var claims = new PortClaims(_root.Path);
        var probe = claims.FreeAndClaimed(port => port != 5199);

        Assert.False(probe(5199));
        Assert.True(probe(5200));
        Assert.False(File.Exists(Path.Combine(_root.Path, "5199")));
        Assert.True(File.Exists(Path.Combine(_root.Path, "5200")));
    }
}
