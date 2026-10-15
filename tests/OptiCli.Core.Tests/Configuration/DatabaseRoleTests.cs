using Microsoft.Data.SqlClient;
using OptiCli.Core.Configuration;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Core.Safety;
using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Configuration;

/// <summary>
/// Environment roles: a role belongs to a server and database, whatever names them; a production database is read
/// when picked for a run, and never the development database, written to or served.
/// </summary>
public class DatabaseRoleTests : IDisposable
{
    private const string Prod = "Server=sql.example.com;Database=Site;User Id=reader;Password=hunter2-prod";
    private const string LocalProd = "Server=localhost,1433;Database=alloy-edge-prod;User Id=sa;Password=hunter2-local";
    private const string LocalDev = "Server=localhost,1433;Database=alloy-edge-dev;User Id=sa;Password=hunter2-dev";

    private readonly SiteFixture _site = new();

    public void Dispose() => _site.Dispose();

    [Theory]
    [InlineData("localhost", "localhost")]
    [InlineData("tcp:LOCALHOST,1433", "localhost")]
    [InlineData("127.0.0.1", "localhost")]
    [InlineData(" (local) ", "localhost")]
    [InlineData(".", "localhost")]
    [InlineData("[::1],1433", "localhost")]
    [InlineData("localhost,1434", "localhost,1434")]
    [InlineData(@"localhost\SQLEXPRESS", @"localhost\sqlexpress")]
    [InlineData(@"localhost\MSSQLSERVER", "localhost")]
    [InlineData(@"localhost\sqlexpress,1433", @"localhost\sqlexpress,1433")]
    [InlineData("tcp:sql.example.com,1433", "sql.example.com")]
    [InlineData("SQL.Example.com.", "sql.example.com")]
    [InlineData(@"(localdb)\MSSQLLocalDB", @"(localdb)\mssqllocaldb")]
    public void Spellings_of_one_server_are_one_key(string server, string expected)
    {
        Assert.Equal(expected, DatabaseKey.NormalizeServer(server));
        Assert.Equal(DatabaseKey.Of(server, "Site"), DatabaseKey.Of(expected, " site "));
    }

    [Theory]
    [InlineData(null, DatabaseRole.Prod, false)]
    [InlineData(DatabaseRole.Local, DatabaseRole.Prod, false)]
    [InlineData(DatabaseRole.Prod, DatabaseRole.Prod, false)]
    [InlineData(DatabaseRole.Prod, DatabaseRole.Test, true)]
    [InlineData(DatabaseRole.Test, DatabaseRole.Dev, true)]
    [InlineData(DatabaseRole.Dev, DatabaseRole.Local, true)]
    public void Only_a_role_weaker_than_the_one_given_lowers_it(DatabaseRole? assigned, DatabaseRole wanted, bool lowers) =>
        Assert.Equal(lowers, DatabaseRoles.Lowers(assigned, wanted));

    [Fact]
    public void A_role_belongs_to_the_server_and_database_whatever_names_them()
    {
        _site.WriteProjectFile("appsettings.json", """{ "ConnectionStrings": { "EPiServerDB": "Server=tcp:127.0.0.1,1433;Database=ALLOY-EDGE-PROD" } }""");
        _site.WriteProjectFile("appsettings.Production.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{Prod}}" } }""");
        WriteLaunchProfile("Server=(local);Database=alloy-edge-prod");
        AddNamed("prod", "Server=tcp:SQL.example.com,1433;Database=site;User Id=reader;Password=x");
        GiveRole(LocalProd, DatabaseRole.Prod);
        GiveRole(Prod, DatabaseRole.Prod);

        var result = Resolve(new ConnectionRequest(Explicit: "Server=.;Database=alloy-edge-prod"),
            new() { ["ConnectionStrings__EPiServerDB"] = "Server=localhost;Database=alloy-edge-prod" });

        Assert.Equal(6, result.Candidates.Count);
        Assert.All(result.Candidates, c => Assert.Equal(DatabaseRole.Prod, c.Role));
    }

    [Fact]
    public void Without_a_role_a_local_database_is_local_and_a_remote_one_has_none()
    {
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{LocalDev}}" } }""");
        _site.WriteProjectFile("appsettings.Production.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{Prod}}" } }""");

        var result = Resolve(new ConnectionRequest());

        Assert.Equal(DatabaseRole.Local, result.Candidates.Single(c => c.Database == "alloy-edge-dev").Role);
        Assert.Null(result.Candidates.Single(c => c.Database == "Site").Role);
        Assert.False(result.IsProduction);
    }

    [Fact]
    public void A_named_database_is_listed_used_only_when_picked_and_picked_by_its_name()
    {
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{LocalDev}}" } }""");
        AddNamed("prod", Prod);

        var result = Resolve(new ConnectionRequest());
        var byName = Resolve(new ConnectionRequest(Database: "PROD"));
        var named = result.Candidates.Single(c => c.Source == ConnectionSource.Named);

        Assert.Equal("alloy-edge-dev", result.Chosen!.Database);
        Assert.Equal(CandidateStatus.Available, named.Status);
        Assert.Equal("prod", named.Name);
        Assert.Matches("^[0-9a-f]{6}$", named.Id!);
        Assert.Equal("prod", result.Choices().Single(c => c.Id == named.Id).Name);
        Assert.Equal("Site", byName.Chosen!.Database);
        Assert.Equal(ConnectionSource.Named, byName.Chosen.Source);
        Assert.Equal("Site", Resolve(new ConnectionRequest(Database: named.Id)).Chosen!.Database);
    }

    [Fact]
    public void Production_is_read_for_a_run_with_a_warning_and_never_counts_as_development()
    {
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{LocalDev}}" } }""");
        AddNamed("prod", LocalProd);
        GiveRole(LocalProd, DatabaseRole.Prod);

        var result = Resolve(new ConnectionRequest(Database: "prod"));

        Assert.Null(result.Failure);
        Assert.True(result.IsProduction);
        Assert.False(result.IsDevelopment);
        Assert.True(result.Require().IsProduction);
        Assert.Contains("production database 'alloy-edge-prod'", Assert.Single(result.Warnings()), StringComparison.Ordinal);
        Assert.False(Resolve(new ConnectionRequest()).Require().IsProduction);
    }

    [Fact]
    public void A_saved_choice_that_is_production_is_refused()
    {
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{LocalDev}}" } }""");
        _site.WriteProjectFile("appsettings.Production.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{Prod}}" } }""");
        Save(Resolve(new ConnectionRequest()).Candidates.Single(c => c.Database == "Site"));
        GiveRole(Prod, DatabaseRole.Prod);

        var result = Resolve(new ConnectionRequest());

        AssertProdRefusal(result.Failure, "chosen with `opticli db use`");
        Assert.Null(result.Chosen);
        Assert.Null(result.Development);
        Assert.Contains("opticli db forget", result.Failure!.Hint, StringComparison.Ordinal);
        Assert.Equal("alloy-edge-dev", Resolve(new ConnectionRequest(Database: "alloy-edge-dev")).Chosen!.Database);
    }

    [Fact]
    public void A_configured_connection_that_is_production_is_refused()
    {
        _site.WriteUserConfig($$"""{ "projects": { "{{_site.ProjectPath.Replace("\\", "\\\\")}}": { "connection": "{{LocalProd}}" } } }""");
        GiveRole(LocalProd, DatabaseRole.Prod);

        AssertProdRefusal(Resolve(new ConnectionRequest()).Failure, "\"connection\" in the opticli user config");
    }

    [Theory]
    [InlineData(LocalProd)]
    [InlineData(Prod)]
    public void A_development_default_that_is_production_is_refused_not_used_or_offered(string connection)
    {
        _site.WriteProjectFile("appsettings.Development.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{connection}}" } }""");
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{LocalDev}}" } }""");
        GiveRole(connection, DatabaseRole.Prod);

        var result = Resolve(new ConnectionRequest());

        AssertProdRefusal(result.Failure, "the site's Development configuration");
        Assert.Equal("alloy-edge-dev", Resolve(new ConnectionRequest(Database: "alloy-edge-dev")).Chosen!.Database);
    }

    [Fact]
    public void An_exported_connection_string_that_is_production_is_refused_as_the_default()
    {
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{LocalDev}}" } }""");
        GiveRole(LocalProd, DatabaseRole.Prod);

        var result = Resolve(new ConnectionRequest(), new() { ["ConnectionStrings__EPiServerDB"] = "Server=127.0.0.1;Database=alloy-edge-prod" });

        AssertProdRefusal(result.Failure, "environment (ConnectionStrings__EPiServerDB)");
    }

    [Fact]
    public void Picked_for_a_run_in_any_way_production_stays_production()
    {
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{LocalDev}}" } }""");
        _site.WriteProjectFile("appsettings.Production.json", """{ "ConnectionStrings": { "EPiServerDB": "Server=localhost;Database=alloy-edge-prod" } }""");
        WriteLaunchProfile("Server=127.0.0.1,1433;Database=alloy-edge-prod");
        AddNamed("prod", LocalProd);
        GiveRole(LocalProd, DatabaseRole.Prod);
        var id = Resolve(new ConnectionRequest()).Candidates.First(c => c.Source == ConnectionSource.AppSettingsEnvironment).Id;

        var picks = new[]
        {
            Resolve(new ConnectionRequest(Database: "prod")),
            Resolve(new ConnectionRequest(Database: id)),
            Resolve(new ConnectionRequest(Database: "alloy-edge-prod")),
            Resolve(new ConnectionRequest(Explicit: "Server=tcp:localhost,1433;Database=Alloy-Edge-Prod")),
            Resolve(new ConnectionRequest(), new() { ["OPTICLI_DB"] = "Data Source=.;Initial Catalog=alloy-edge-prod" }),
            Resolve(new ConnectionRequest(Profile: "Web")),
        };

        Assert.All(picks, result =>
        {
            Assert.Null(result.Failure);
            Assert.True(result.IsProduction);
            Assert.True(result.Require().IsProduction);
        });
    }

    [Fact]
    public void Roles_that_cant_be_read_stop_every_database()
    {
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{LocalDev}}" } }""");
        _site.WriteUserConfig("""{ "roles": [ { "server": "localhost", "database": "alloy-edge-prod", "role": "production" } ] }""");

        var result = Resolve(new ConnectionRequest(Explicit: LocalDev));

        Assert.Null(result.Chosen);
        Assert.Contains("no known role", Assert.IsType<UsageException>(result.Failure).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Roles_and_named_databases_are_saved_beside_the_rest_of_the_user_config()
    {
        var file = _site.Environment().UserConfigFile;
        _site.WriteUserConfig($$"""{ "other": 1, "projects": { "{{_site.ProjectPath.Replace("\\", "\\\\")}}": { "port": 5200 } } }""");
        var key = DatabaseKey.Of("tcp:SQL.example.com,1433", "Site")!;

        UserConfig.SaveNamedDatabase(file, _site.ProjectPath, new NamedDatabase("prod", Prod));
        UserConfig.SaveNamedDatabase(file, _site.ProjectPath, new NamedDatabase("PROD", Prod + ";Encrypt=True"));
        UserConfig.SaveRole(file, new RoleEntry(key, DatabaseRole.Test, DateTimeOffset.UtcNow, "add"));
        UserConfig.SaveRole(file, new RoleEntry(key, DatabaseRole.Prod, DateTimeOffset.UtcNow, "role"));
        var settings = UserConfig.ForProject(file, _site.ProjectPath)!;
        var roles = UserConfig.Roles(file);

        Assert.Equal(5200, settings.Port);
        Assert.Equal("PROD", Assert.Single(settings.Databases!).Name);
        var role = Assert.Single(roles);
        Assert.Equal(new DatabaseKey("sql.example.com", "site"), role.Key);
        Assert.Equal(DatabaseRole.Prod, role.Role);
        Assert.Contains("\"other\": 1", File.ReadAllText(file), StringComparison.Ordinal);

        Assert.True(UserConfig.RemoveNamedDatabase(file, _site.ProjectPath, "prod"));
        Assert.False(UserConfig.RemoveNamedDatabase(file, _site.ProjectPath, "prod"));
        Assert.Null(UserConfig.ForProject(file, _site.ProjectPath)!.Databases);
        Assert.Single(UserConfig.Roles(file));
    }

    [Fact]
    public void A_production_connection_asks_for_a_read_only_intent_unless_the_string_sets_one()
    {
        var remote = Resolved(Prod);
        var setsIt = Resolved(Prod + ";ApplicationIntent=ReadWrite");
        var shared = ConnectionResolver.Resolve(new ConnectionRequest(Explicit: "Server=sql.example.com;Database=Shared"), null, _site.Environment()).Require();

        Assert.Equal(ApplicationIntent.ReadOnly, CmsDatabase.Builder(remote).ApplicationIntent);
        Assert.Equal(ApplicationIntent.ReadWrite, CmsDatabase.Builder(setsIt).ApplicationIntent);
        Assert.False(shared.IsProduction);
        Assert.Equal(ApplicationIntent.ReadWrite, CmsDatabase.Builder(shared).ApplicationIntent);
    }

    [Fact]
    public async Task Nothing_that_runs_the_site_takes_a_production_connection()
    {
        var production = Resolved(LocalProd);
        var store = StateStore.For(_site.Environment(), _site.ProjectPath);

        var agent = await Assert.ThrowsAsync<RefusedException>(() => AgentProbe.ConnectAsync(store, production, CancellationToken.None));
        var site = Assert.Throws<RefusedException>(() => SiteEnvironment.Build("agent.dll", "token", 5199, "EPiServerDB", production));
        var approved = Assert.Throws<RefusedException>(() => SiteEnvironment.Build("agent.dll", "token", 5199, "EPiServerDB", null, approvedRemote: production));

        Assert.All(new[] { agent, site, approved }, refusal => AssertProdRefusal(refusal, "production database"));
    }

    private VerifiedConnectionString Resolved(string production)
    {
        GiveRole(production, DatabaseRole.Prod);
        return ConnectionResolver.Resolve(new ConnectionRequest(Explicit: production), null, _site.Environment()).Require();
    }

    private static void AssertProdRefusal(OptiCliException? failure, string text)
    {
        var refused = Assert.IsType<RefusedException>(failure);
        Assert.Contains(text, refused.Message, StringComparison.Ordinal);
        Assert.Equal(ProductionDatabase.Reason, Core.Output.JsonOutput.ToNode(refused.Details)!["reason"]!.GetValue<string>());
    }

    private void GiveRole(string connection, DatabaseRole role)
    {
        var verdict = ConnectionSafety.Check(connection);
        UserConfig.SaveRole(_site.Environment().UserConfigFile, new RoleEntry(DatabaseKey.Of(verdict.Server, verdict.Database)!, role, DateTimeOffset.UtcNow, "role"));
    }

    private void AddNamed(string name, string connection) =>
        UserConfig.SaveNamedDatabase(_site.Environment().UserConfigFile, _site.ProjectPath, new NamedDatabase(name, connection));

    private void Save(ConnectionCandidate candidate) =>
        UserConfig.SaveDatabase(_site.Environment().UserConfigFile, _site.ProjectPath, SavedDatabase.From(candidate, SavedDatabase.ViaCommand, DateTimeOffset.UtcNow));

    private void WriteLaunchProfile(string connection) =>
        _site.WriteProjectFile("Properties/launchSettings.json", $$"""
            { "profiles": { "Web": { "commandName": "Project", "environmentVariables": { "ConnectionStrings:EPiServerDB": "{{connection}}" } } } }
            """);

    private ConnectionResolution Resolve(ConnectionRequest request, Dictionary<string, string>? variables = null) =>
        ConnectionResolver.Resolve(request, _site.Project(), _site.Environment(variables));
}
