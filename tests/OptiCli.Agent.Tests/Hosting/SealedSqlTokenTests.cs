using Microsoft.Data.SqlClient;
using OptiCli.Agent.Hosting;

namespace OptiCli.Agent.Tests.Hosting;

/// <summary>
/// A sealed run's Microsoft Entra token for the site (<see cref="SealedSqlToken"/>): the site's SqlClient gets a provider that
/// hands out the run's token for the pinned connection's method, and falls back to SqlClient's own provider once the token is
/// about to expire. Process-wide state, so these run alone and put back the provider they found.
/// </summary>
[Collection(HostingCollection.Name)]
public sealed class SealedSqlTokenTests : IDisposable
{
    private const string Entra = "Server=tcp:sql.example.net,1433;Database=cms-prod;Authentication=Active Directory Default;Application Intent=ReadWrite";

    private readonly SqlAuthenticationProvider? _found = SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryDefault);
    private readonly SiteOwn _own = new();

    public SealedSqlTokenTests() => SqlAuthenticationProvider.SetProvider(SqlAuthenticationMethod.ActiveDirectoryDefault, _own);

    public void Dispose() => SqlAuthenticationProvider.SetProvider(SqlAuthenticationMethod.ActiveDirectoryDefault, _found ?? _own);

    private static SqlAuthenticationParameters Parameters() =>
        new(SqlAuthenticationMethod.ActiveDirectoryDefault, "sql.example.net", "cms-prod", "https://database.windows.net/", "https://login.microsoftonline.com/tenant", "", "", Guid.NewGuid(), 30);

    [Fact]
    public async Task The_site_signs_in_with_the_runs_token()
    {
        var log = HostingFixture.Stderr(() => SealedSqlToken.Install("run-token", DateTimeOffset.UtcNow.AddMinutes(50).ToString("O"), Entra));

        var provider = SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryDefault)!;

        Assert.Equal("RunTokenProvider", provider.GetType().Name);
        Assert.Equal("run-token", (await provider.AcquireTokenAsync(Parameters())).AccessToken);
        Assert.Equal(0, _own.Asked);
        Assert.Contains("[opticli] sealed apply: the site signs in to production with the run's Microsoft Entra token", log, StringComparison.Ordinal);
        Assert.DoesNotContain("run-token", log, StringComparison.Ordinal);
        Assert.False(provider.IsSupported(SqlAuthenticationMethod.ActiveDirectoryInteractive));
    }

    [Fact]
    public async Task Once_it_is_about_to_expire_the_sites_own_provider_fetches_one()
    {
        HostingFixture.Stderr(() => SealedSqlToken.Install("run-token", DateTimeOffset.UtcNow.AddSeconds(30).ToString("O"), Entra));

        var log = await HostingFixture.StderrAsync(async () =>
        {
            var token = await SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryDefault)!.AcquireTokenAsync(Parameters());
            Assert.Equal("site-own", token.AccessToken);
        });

        Assert.Equal(1, _own.Asked);
        Assert.Contains("the site fetches its own from now on", log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, Entra)]
    [InlineData("run-token", null)]
    [InlineData("run-token", "Server=sql.example.net;Database=cms-prod;User ID=writer;Password=secret")]
    public void Without_a_token_an_entra_connection_or_a_pin_nothing_changes(string? token, string? connection)
    {
        HostingFixture.Stderr(() => SealedSqlToken.Install(token, null, connection));

        Assert.Same(_own, SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryDefault));
    }

    /// <summary>What SqlClient would ask without opticli: the site's own sign-in.</summary>
    private sealed class SiteOwn : SqlAuthenticationProvider
    {
        public int Asked { get; private set; }

        public override bool IsSupported(SqlAuthenticationMethod authenticationMethod) => authenticationMethod == SqlAuthenticationMethod.ActiveDirectoryDefault;

        public override Task<SqlAuthenticationToken> AcquireTokenAsync(SqlAuthenticationParameters parameters)
        {
            Asked++;
            return Task.FromResult(new SqlAuthenticationToken("site-own", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}
