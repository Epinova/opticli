using Microsoft.Data.SqlClient;
using OptiCli.Core.Configuration;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Core.Safety;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Safety;

/// <summary>
/// The CMS templates' development connection string: LocalDB with <c>AttachDbFilename=|DataDirectory|\Name.mdf</c>, which
/// the site's <c>Startup</c> expands to its <c>App_Data</c>. opticli expands it the same way before it connects, and
/// gives the site the string as configured.
/// </summary>
public class DataDirectoryTests : IDisposable
{
    /// <summary>As <c>dotnet new epi-alloy-mvc</c> writes it to appsettings.Development.json.</summary>
    internal const string Template = @"Data Source=(LocalDb)\MSSQLLocalDB;AttachDbFilename=|DataDirectory|\Alloy13.mdf;Initial Catalog=Alloy13;Integrated Security=True;Connect Timeout=30";

    private readonly SiteFixture _site = new();

    public void Dispose() => _site.Dispose();

    private static string Attached(string connectionString) => new SqlConnectionStringBuilder(connectionString).AttachDBFilename;

    [Theory]
    [InlineData(@"|DataDirectory|\Alloy13.mdf")]
    [InlineData("|DataDirectory|/Alloy13.mdf")]
    [InlineData("|datadirectory|Alloy13.mdf")]
    public void The_token_becomes_the_data_directory(string file)
    {
        var root = Path.Combine(_site.ProjectPath, "App_Data");

        var resolved = ConnectionSafety.ResolveDataDirectory($"Data Source=(LocalDb)\\MSSQLLocalDB;AttachDbFilename={file};Initial Catalog=Alloy13", root);

        Assert.Equal(Path.Combine(root, "Alloy13.mdf"), Attached(resolved));
        Assert.Equal("Alloy13", new SqlConnectionStringBuilder(resolved).InitialCatalog);
    }

    [Fact]
    public void A_nested_file_stays_nested()
    {
        var root = Path.Combine(_site.ProjectPath, "App_Data");

        var resolved = ConnectionSafety.ResolveDataDirectory(@"Server=(localdb)\MSSQLLocalDB;AttachDbFilename=|DataDirectory|\db\Site.mdf", root);

        Assert.Equal(Path.Combine(root, "db", "Site.mdf"), Attached(resolved));
    }

    [Theory]
    [InlineData("Server=localhost;Database=Cms;Integrated Security=True")]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;AttachDbFilename=C:\data\Site.mdf")]
    public void A_string_without_the_token_is_left_alone(string connectionString)
    {
        Assert.Equal(connectionString, ConnectionSafety.ResolveDataDirectory(connectionString, "/somewhere/App_Data"));
        Assert.Equal(connectionString, ConnectionSafety.ResolveDataDirectory(connectionString, null));
    }

    [Fact]
    public void Without_a_project_the_token_is_refused()
    {
        var error = Assert.Throws<RefusedException>(() => ConnectionSafety.ResolveDataDirectory(Template, null));

        Assert.StartsWith(ConnectionSafety.UnusableHere, error.Message, StringComparison.Ordinal);
        Assert.Contains("--project", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_outside_the_data_directory_is_refused()
    {
        Assert.Throws<RefusedException>(() => ConnectionSafety.ResolveDataDirectory(
            @"Server=(localdb)\MSSQLLocalDB;AttachDbFilename=|DataDirectory|\..\..\other\Site.mdf", Path.Combine(_site.ProjectPath, "App_Data")));
    }

    [Fact]
    public void The_data_directory_is_the_projects_App_Data()
    {
        Assert.Equal(Path.Combine(_site.ProjectPath, "App_Data"), ConnectionResolution.DataDirectoryOf(_site.ProjectPath));
        Assert.Null(ConnectionResolution.DataDirectoryOf(null));
    }

    [Fact]
    public void LocalDb_stays_local_and_is_chosen_from_the_templates_settings()
    {
        _site.WriteProjectFile("appsettings.Development.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{Template.Replace(@"\", @"\\", StringComparison.Ordinal)}}" } }""");

        var result = ConnectionResolver.Resolve(new ConnectionRequest(), _site.Project(), _site.Environment());

        Assert.Null(result.Failure);
        Assert.Equal(ConnectionSource.AppSettingsDevelopment, result.Chosen!.Source);
        Assert.True(result.Chosen.IsLocal);
        Assert.Equal("Alloy13", result.Chosen.Database);
        var verified = result.Require();
        Assert.True(verified.IsLocal);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Combine(_site.ProjectPath, "App_Data", "Alloy13.mdf"), Attached(verified.Value));
        }
        else
        {
            // LocalDB doesn't exist here, so nothing is expanded: opening it is refused (below) before anything else.
            Assert.Equal(Attached(Template), Attached(verified.Value));
        }
        // The site expands |DataDirectory| itself, as configured.
        Assert.Equal(Attached(Template), Attached(verified.ForSite));
    }

    [Fact]
    public void The_site_gets_the_string_as_configured()
    {
        var verified = ConnectionSafety.Approve(Template, Path.Combine(_site.ProjectPath, "App_Data"));

        var variables = SiteEnvironment.Build("/tools/agent/OptiCli.Agent.dll", "token", 5199, "EPiServerDB", verified).ToDictionary(v => v.Key, v => v.Value);

        Assert.Equal(Attached(Template), Attached(variables[AgentProtocol.DatabaseVariable]));
        Assert.Equal(Attached(Template), Attached(variables["ConnectionStrings__EPiServerDB"]));
    }

    [Fact]
    public async Task Away_from_Windows_LocalDB_is_refused_with_a_hint_before_connecting()
    {
        var unusable = ConnectionSafety.Unusable(Template);
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(unusable);
            return;
        }
        Assert.StartsWith(ConnectionSafety.UnusableHere, unusable, StringComparison.Ordinal);
        Assert.Null(ConnectionSafety.Unusable("Server=localhost;Database=Cms"));

        var error = await Assert.ThrowsAsync<RefusedException>(() => CmsDatabase.OpenAsync(ConnectionSafety.Approve(Template, null), CancellationToken.None));
        Assert.Contains("LocalDB", error.Message, StringComparison.Ordinal);
        Assert.Contains("opticli doctor", error.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"(localdb)\MSSQLLocalDB", true)]
    [InlineData(@"(LocalDb)\ProjectsV13", true)]
    [InlineData(@"tcp:(localdb)\MSSQLLocalDB", true)]
    [InlineData("localhost", false)]
    [InlineData(@".\SQLEXPRESS", false)]
    public void LocalDb_data_sources_are_recognised(string dataSource, bool localDb) =>
        Assert.Equal(localDb, ConnectionSafety.IsLocalDb(dataSource));
}
