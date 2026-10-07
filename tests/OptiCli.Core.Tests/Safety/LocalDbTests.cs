using Microsoft.Data.SqlClient;
using OptiCli.Core.Cms;
using OptiCli.Core.Configuration;
using OptiCli.Core.Data;

namespace OptiCli.Core.Tests.Safety;

/// <summary>
/// Runs only where SQL Server Express LocalDB is installed: Windows with it (GitHub's windows-latest image has it as a
/// Visual Studio component; CI makes sure). With <c>OPTICLI_REQUIRE_LOCALDB=1</c> (set by the Windows CI job) it never
/// skips, so a runner without LocalDB fails instead of passing silently.
/// </summary>
public sealed class LocalDbFactAttribute : FactAttribute
{
    public const string Instance = @"(localdb)\MSSQLLocalDB";

    public const string RequireVariable = "OPTICLI_REQUIRE_LOCALDB";

    public LocalDbFactAttribute()
    {
        if (!Installed() && !Required)
        {
            Skip = $"Needs SQL Server Express LocalDB (Windows only); {RequireVariable}=1 makes it fail instead.";
        }
    }

    public static bool Required => Environment.GetEnvironmentVariable(RequireVariable) is "1" or "true";

    /// <summary>For a required run: fails with why, instead of a connection error, when LocalDB isn't there.</summary>
    public static void RequireInstalled() =>
        Assert.True(Installed(), $"{RequireVariable} is set, but SQL Server Express LocalDB isn't installed here (no 'Installed Versions' under HKLM\\SOFTWARE\\Microsoft\\Microsoft SQL Server Local DB).");

    public static bool Installed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        using var versions = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions");
        return versions?.GetSubKeyNames().Length > 0;
    }
}

/// <summary>
/// The CMS templates' unchanged LocalDB connection string, for real: a database file made through LocalDB in a site's
/// <c>App_Data</c>, then found and read through opticli's own connection resolution, <c>|DataDirectory|</c> and all.
/// </summary>
public class LocalDbTests : IDisposable
{
    private readonly SiteFixture _site = new();

    private readonly string _name = "opticli_localdb_" + Guid.NewGuid().ToString("N")[..12];

    public void Dispose()
    {
        try
        {
            SqlConnection.ClearAllPools();
            if (OperatingSystem.IsWindows())
            {
                Master($"""
                    IF DB_ID(N'{_name}') IS NOT NULL
                    BEGIN
                        ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        DROP DATABASE [{_name}];
                    END
                    """);
            }
        }
        catch (SqlException)
        {
            // Not attached, or LocalDB is gone: the files go with the fixture.
        }
        _site.Dispose();
    }

    private static void Master(string sql)
    {
        using var connection = new SqlConnection($"Data Source={LocalDbFactAttribute.Instance};Initial Catalog=master;Integrated Security=True;Connect Timeout=60");
        connection.Open();
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.ExecuteNonQuery();
    }

    [LocalDbFact]
    public async Task The_templates_connection_string_reads_the_sites_App_Data_database()
    {
        LocalDbFactAttribute.RequireInstalled();
        var appData = Path.Combine(_site.ProjectPath, "App_Data");
        Directory.CreateDirectory(appData);
        var file = Path.Combine(appData, $"{_name}.mdf");
        // A database file with the few objects DatabaseInfoReader reads, detached as a template site's is before its first start.
        Master($"CREATE DATABASE [{_name}] ON (NAME = N'{_name}', FILENAME = N'{file}') LOG ON (NAME = N'{_name}_log', FILENAME = N'{Path.Combine(appData, $"{_name}_log.ldf")}')");
        Master($"""
            EXEC (N'USE [{_name}]; EXEC (N''CREATE PROCEDURE dbo.sp_DatabaseVersion AS RETURN 8023'');
                  CREATE TABLE dbo.tblContentType (pkID int NOT NULL);
                  CREATE TABLE dbo.tblContent (pkID int NOT NULL, Deleted bit NOT NULL);
                  INSERT INTO dbo.tblContent VALUES (1, 0), (2, 1);');
            """);
        SqlConnection.ClearAllPools();
        Master($"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; EXEC sp_detach_db N'{_name}';");

        var template = $@"Data Source=(LocalDb)\MSSQLLocalDB;AttachDbFilename=|DataDirectory|\{_name}.mdf;Initial Catalog={_name};Integrated Security=True;Connect Timeout=60";
        _site.WriteProjectFile("appsettings.Development.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{template.Replace(@"\", @"\\", StringComparison.Ordinal)}}" } }""");

        var resolution = ConnectionResolver.Resolve(new ConnectionRequest(), _site.Project(), _site.Environment());
        Assert.Null(resolution.Failure);
        var verified = resolution.Require();
        Assert.True(verified.IsLocal);

        await using var db = await CmsDatabase.OpenAsync(verified, CancellationToken.None);
        var info = await DatabaseInfoReader.ReadAsync(db, CancellationToken.None);

        Assert.Equal(_name, info.Database, ignoreCase: true);
        Assert.Equal(8023, info.SchemaVersion);
        Assert.Equal(1, info.ContentItems);
        Assert.Equal(12, (await db.SchemaAsync(CancellationToken.None)).Major);
    }
}
