using EPiServer.Data;
using Microsoft.Extensions.Configuration;
using OptiCli.Agent.Hosting;
using static OptiCli.Agent.Tests.Hosting.HostingFixture;

namespace OptiCli.Agent.Tests.Hosting;

[Collection(HostingCollection.Name)]
public class DatabasePinTests
{
    [Fact]
    public void A_local_connection_string_is_allowed_and_reported_as_verified()
    {
        var stderr = Stderr(() => DatabasePin.EnsureAllowed(Settings(), "EPiServerDB", Local));

        Assert.Contains("EPiServerDB verified local: server 'localhost', database 'ExampleDb'", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Remote)]
    [InlineData(null)]
    [InlineData("")]
    public void Anything_but_a_local_database_stops_the_site_without_an_approval(string? connectionString)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DatabasePin.EnsureAllowed(Settings(), "EPiServerDB", connectionString));

        Assert.StartsWith("[opticli] Refusing to start: connection string 'EPiServerDB' is not local.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_approved_remote_database_is_allowed()
    {
        var settings = Settings(approvedRemote: RemoteApproval);

        var stderr = Stderr(() => DatabasePin.EnsureAllowed(settings, "EPiServerDB", Remote));
        var ex = Assert.Throws<InvalidOperationException>(() => DatabasePin.EnsureAllowed(settings, "EPiServerDB", OtherRemote));

        Assert.Contains("verified as the approved remote development database: server 'tcp:dev.example.net,1433'", stderr, StringComparison.Ordinal);
        Assert.Contains("is not local nor the approved development database", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pinned_connection_must_be_exactly_the_pin()
    {
        var settings = Settings(pinned: Local);

        Stderr(() => DatabasePin.EnsureAllowed(settings, "EPiServerDB", Local));
        var ex = Assert.Throws<InvalidOperationException>(() => DatabasePin.EnsureAllowed(settings, "EPiServerDB", OtherLocal));
        // Ordinal: even a difference in case is another string than the one the CLI verified.
        Assert.Throws<InvalidOperationException>(() => DatabasePin.EnsureAllowed(settings, "EPiServerDB", Local.ToUpperInvariant()));

        Assert.Contains("something in the site overrode the pinned connection string 'EPiServerDB' (now server '(localdb)\\MSSQLLocalDB', database 'OtherDb')", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cms_connection_is_the_default_name_ignoring_case_else_the_first()
    {
        var options = new DataAccessOptions { DefaultConnectionStringName = "EPiServerDB" };
        Assert.Null(DatabasePin.Resolve(options));

        options.ConnectionStrings.Add(new ConnectionStringOptions { Name = "Other", ConnectionString = OtherLocal });
        Assert.Equal("Other", DatabasePin.Resolve(options)!.Name);

        options.ConnectionStrings.Add(new ConnectionStringOptions { Name = "episerverdb", ConnectionString = Local });
        Assert.Equal("episerverdb", DatabasePin.Resolve(options)!.Name);
    }

    [Fact]
    public void Other_remote_connection_strings_only_warn()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:EPiServerDB"] = OtherRemote,
            ["ConnectionStrings:Search"] = OtherRemote,
            ["ConnectionStrings:Cache"] = Local,
            ["ConnectionStrings:Approved"] = Remote,
            ["ConnectionStrings:Storage"] = "UseDevelopmentStorage=true",
        }).Build();

        var stderr = Stderr(() => DatabasePin.WarnAboutOtherRemoteStrings(Settings(approvedRemote: RemoteApproval), configuration, "episerverdb"));

        Assert.Equal(
            ["[opticli] warning: connection string 'Search' points at non-local server 'tcp:prod.example.net,1433'."],
            stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
