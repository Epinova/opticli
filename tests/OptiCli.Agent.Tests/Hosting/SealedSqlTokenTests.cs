using OptiCli.Agent.Hosting;

namespace OptiCli.Agent.Tests.Hosting;

/// <summary>
/// A sealed run's Microsoft Entra token for the site (<see cref="SealedSqlToken"/>). These tests run on SqlClient 7, where
/// SqlClient's provider base class lives in another assembly than the agent compiles against: the site then fetches its own
/// token, and must still start. (On SqlClient 5 and 6, what CMS 12 and 13 sites bring, the provider is installed; checked on a
/// real site.)
/// </summary>
[Collection(HostingCollection.Name)]
public sealed class SealedSqlTokenTests
{
    private const string Entra = "Server=tcp:sql.example.net,1433;Database=cms-prod;Authentication=Active Directory Default;Application Intent=ReadWrite";

    [Fact]
    public void On_a_sqlclient_it_cant_extend_the_site_still_starts_and_says_so()
    {
        var log = HostingFixture.Stderr(() => SealedSqlToken.Install("run-token", DateTimeOffset.UtcNow.AddMinutes(50).ToString("O"), Entra));

        Assert.Contains("[opticli] sealed apply:", log, StringComparison.Ordinal);
        Assert.DoesNotContain("run-token", log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, Entra)]
    [InlineData("run-token", null)]
    public void Without_a_token_or_a_pinned_connection_nothing_happens(string? token, string? connection)
    {
        Assert.Equal("", HostingFixture.Stderr(() => SealedSqlToken.Install(token, null, connection)));
    }
}
