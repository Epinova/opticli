using Microsoft.Data.SqlClient;
using OptiCli.Core.Errors;
using OptiCli.Core.Safety;

namespace OptiCli.Core.Tests.Safety;

public class ConnectionSafetyTests
{
    public static TheoryData<string, string> LocalStrings => new()
    {
        { "Server=localhost;Database=Cms", "localhost" },
        { "Server=LOCALHOST;Database=Cms", "LOCALHOST" },
        { "Server=127.0.0.1;Database=Cms", "127.0.0.1" },
        { "Server=::1;Database=Cms", "::1" },
        { "Server=[::1],1433;Database=Cms", "[::1],1433" },
        { "Server=.;Database=Cms", "." },
        { @"Server=.\SQLEXPRESS;Database=Cms", @".\SQLEXPRESS" },
        { "Server=(local);Database=Cms", "(local)" },
        { @"Server=(localdb)\MSSQLLocalDB;Database=Cms", @"(localdb)\MSSQLLocalDB" },
        { @"Data Source=(LocalDb)\MSSQLLocalDB;Initial Catalog=Cms;Integrated Security=True", @"(LocalDb)\MSSQLLocalDB" },
        { "Server=tcp:localhost,1433;Database=Cms", "tcp:localhost,1433" },
        { "Server=localhost, 1433;Database=Cms", "localhost, 1433" },
        { @"Server=localhost\SQL2022,1433;Database=Cms", @"localhost\SQL2022,1433" },
        { "Data Source=localhost;Initial Catalog=Cms", "localhost" },
        { "Address=localhost;Database=Cms", "localhost" },
        { "Addr=127.0.0.1;Database=Cms", "127.0.0.1" },
        { "Network Address=localhost;Database=Cms", "localhost" },
        { "Server='localhost';Database=Cms", "localhost" },
        { "Server=\"localhost\";Database=Cms", "localhost" },
        { "  server = localhost ; database = Cms ", "localhost" },
        // Duplicates: SqlClient uses the last value, so the local one here is what it would connect to.
        { "Server=sql.example.com;Server=localhost;Database=Cms", "localhost" },
        { "Server=sql.example.com;Data Source=localhost;Database=Cms", "localhost" },
        { "Server=localhost;Failover Partner=(local);Database=Cms", "localhost" },
    };

    public static TheoryData<string> RemoteStrings => new()
    {
        "Server=sql.example.invalid;Database=x;User Id=a;Password=b",
        "Server=localhost.example.com;Database=Cms",
        "Server=127.0.0.1.nip.io;Database=Cms",
        "Server=localhost.;Database=Cms",
        "Server=10.0.0.5;Database=Cms",
        "Server=127.0.0.2;Database=Cms",
        "Server=tcp:sql.example.com,1433;Database=Cms",
        "Server=tcp:db.database.windows.net,1433;Database=Cms;Authentication=Active Directory Default",
        // Last duplicate wins, including across synonyms.
        "Server=localhost;Server=sql.example.com;Database=Cms",
        "Data Source=localhost;Server=sql.example.com;Database=Cms",
        "Server=localhost;Address=sql.example.com;Database=Cms",
        // Other protocols and malformed ports are refused rather than interpreted.
        @"Server=np:\\sql.example.com\pipe\sql\query;Database=Cms",
        "Server=np:localhost;Database=Cms",
        "Server=admin:localhost;Database=Cms",
        "Server=lpc:.;Database=Cms",
        "Server=localhost,abc;Database=Cms",
        "Server=localhost,;Database=Cms",
        "Server=localhost:1433;Database=Cms",
        @"Server=localhost\;Database=Cms",
        @"Server=(localdb)\;Database=Cms",
        // Cyrillic 'о' look-alike.
        "Server=lоcalhost;Database=Cms",
        // SqlClient fails over to the partner silently.
        "Server=localhost;Failover Partner=sql.example.com;Database=Cms",
        "Server=;Database=Cms",
        "Database=Cms",
    };

    [Theory]
    [MemberData(nameof(LocalStrings))]
    public void Accepts_local_servers(string connectionString, string expectedServer)
    {
        var verdict = ConnectionSafety.Check(connectionString);

        Assert.True(verdict.IsLocal, verdict.Reason);
        Assert.Equal(expectedServer, verdict.Server);
        Assert.Equal("Cms", verdict.Database);
    }

    [Theory]
    [MemberData(nameof(RemoteStrings))]
    public void Refuses_everything_else(string connectionString)
    {
        var verdict = ConnectionSafety.Check(connectionString);

        Assert.False(verdict.IsLocal);
        Assert.NotNull(verdict.Reason);
        Assert.Throws<RefusedException>(() => ConnectionSafety.Verify(connectionString));
    }

    [Theory]
    [MemberData(nameof(LocalStrings))]
    public void Checked_server_is_the_one_SqlClient_would_use(string connectionString, string _)
    {
        // Constructing a SqlConnection parses the string without opening anything.
        using var connection = new SqlConnection(connectionString);

        Assert.Equal(connection.DataSource, ConnectionSafety.Check(connectionString).Server);
    }

    [Theory]
    [MemberData(nameof(RemoteStrings))]
    public void Checked_server_matches_SqlClient_for_refused_strings_too(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);

        Assert.Equal(connection.DataSource, ConnectionSafety.Check(connectionString).Server ?? "");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("this is not a connection string")]
    [InlineData("Server=localhost;NoSuchKeyword=1")]
    public void Invalid_strings_are_refused_not_thrown(string? connectionString)
    {
        var verdict = ConnectionSafety.Check(connectionString);

        Assert.False(verdict.IsLocal);
        Assert.False(verdict.IsValid);
        Assert.Throws<RefusedException>(() => ConnectionSafety.Verify(connectionString));
    }

    [Fact]
    public void Verify_normalises_duplicates_so_the_connected_string_is_the_checked_one()
    {
        var verified = ConnectionSafety.Verify("Server=sql.example.com;Server=localhost;Database=Cms;User Id=app;Password=secret");

        Assert.Equal("localhost", verified.Server);
        Assert.Equal("Cms", verified.Database);
        Assert.DoesNotContain("sql.example.com", verified.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("localhost", new SqlConnectionStringBuilder(verified.Value).DataSource);
    }

    [Fact]
    public void Verified_string_never_prints_its_password()
    {
        var verified = ConnectionSafety.Verify("Server=localhost;Database=Cms;User Id=app;Password=secret");

        Assert.DoesNotContain("secret", verified.ToString(), StringComparison.Ordinal);
        Assert.Contains("localhost", verified.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Network_library_off_windows_is_refused_not_thrown()
    {
        // SqlClient knows the keyword but supports it only on Windows; elsewhere it threw NotSupportedException (internal).
        const string connectionString = "Server=localhost,1433;Database=Cms;Network Library=DBMSSOCN";
        var verdict = ConnectionSafety.Check(connectionString);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(verdict.IsLocal, verdict.Reason);
            return;
        }
        Assert.False(verdict.IsValid);
        Assert.Contains("Network Library", verdict.Reason, StringComparison.Ordinal);
        Assert.Throws<RefusedException>(() => ConnectionSafety.Verify(connectionString));
        Assert.Throws<RefusedException>(() => ConnectionSafety.Approve(connectionString));
    }

    [Fact]
    public async Task A_named_pipe_off_windows_is_refused_not_thrown()
    {
        // np: is never local, but a remote one may be read from (--db); off Windows SqlClient has no named pipes at all.
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var verified = ConnectionSafety.Approve(@"Server=np:\\sql.example.invalid\pipe\sql\query;Database=Cms;Connect Timeout=1");

        var error = await Assert.ThrowsAsync<RefusedException>(() => OptiCli.Core.Data.CmsDatabase.OpenAsync(verified, CancellationToken.None));
        Assert.StartsWith(ConnectionSafety.UnusableHere, error.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionSafety.ProtocolHint, error.Hint);
    }
}
