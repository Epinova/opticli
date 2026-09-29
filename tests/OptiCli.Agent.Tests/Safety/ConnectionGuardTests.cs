using Microsoft.Data.SqlClient;
using OptiCli.Agent.Safety;
using OptiCli.Tests.Shared;

namespace OptiCli.Agent.Tests.Safety;

public class ConnectionGuardTests
{
    public static TheoryData<string, string> LocalStrings()
    {
        var data = new TheoryData<string, string>();
        foreach (var (connectionString, server) in ConnectionStringCases.Local)
        {
            data.Add(connectionString, server);
        }
        return data;
    }

    public static TheoryData<string> RemoteStrings() => new(ConnectionStringCases.Remote);

    public static TheoryData<string> MalformedStrings() => new(ConnectionStringCases.Malformed);

    [Theory]
    [MemberData(nameof(LocalStrings))]
    public void Accepts_local_servers(string connectionString, string expectedServer)
    {
        var verdict = ConnectionGuard.Check(connectionString);

        Assert.True(verdict.IsLocal, verdict.Reason);
        Assert.Equal(expectedServer, verdict.Server);
        Assert.Equal("Cms", verdict.Database);
    }

    [Theory]
    [MemberData(nameof(RemoteStrings))]
    [MemberData(nameof(MalformedStrings))]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Refuses_everything_else(string? connectionString)
    {
        var verdict = ConnectionGuard.Check(connectionString);

        Assert.False(verdict.IsLocal);
        Assert.NotNull(verdict.Reason);
    }

    [Theory]
    [MemberData(nameof(LocalStrings))]
    public void Checked_server_is_the_one_SqlClient_would_use(string connectionString, string _)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);

        Assert.Equal(builder.DataSource, ConnectionGuard.Check(connectionString).Server);
    }

    [Theory]
    [MemberData(nameof(RemoteStrings))]
    public void Checked_server_matches_SqlClient_for_refused_strings_too(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);

        Assert.Equal(builder.DataSource, ConnectionGuard.Check(connectionString).Server ?? "");
    }

    [Theory]
    [MemberData(nameof(MalformedStrings))]
    public void Malformed_strings_are_malformed_for_SqlClient_too(string connectionString)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SqlConnectionStringBuilder(connectionString));
        Assert.Throws<FormatException>(() => ConnectionStringParser.Parse(connectionString));
    }

    [Fact]
    public void Parser_agrees_with_SqlClient_on_generated_strings()
    {
        // Fragments chosen to hit quoting, synonyms, duplicates and empty values in every order.
        string[] fragments =
        [
            "Server=localhost", "Server=sql.example.com", "Data Source=127.0.0.1", "Address=sql.example.com",
            "addr = (local)", "Network Address=tcp:localhost,1433", "Server=", "Server='sql.example.com'",
            "Password=\"x;Server=sql.example.com\"", "Password='y;Data Source=localhost'", "Password=\"a\"\"b\"",
            "Database=Cms", "Initial Catalog=Other", "Failover Partner=sql.example.com", "FailoverPartner=.",
            "User Id=app", " ", "",
        ];
        var random = new Random(1234);

        for (var i = 0; i < 5000; i++)
        {
            var parts = Enumerable.Range(0, random.Next(1, 6)).Select(_ => fragments[random.Next(fragments.Length)]);
            var connectionString = string.Join(random.Next(2) == 0 ? ";" : " ; ", parts);

            SqlConnectionStringBuilder builder;
            try
            {
                builder = new SqlConnectionStringBuilder(connectionString);
            }
            catch (ArgumentException)
            {
                // SqlClient won't connect with it, so whatever the guard says is harmless.
                continue;
            }

            var verdict = ConnectionGuard.Check(connectionString);
            Assert.True(builder.DataSource == (verdict.Server ?? ""), $"Server mismatch for: {connectionString}");
            var sqlClientLocal = ConnectionGuard.IsLocalDataSource(builder.DataSource)
                && (string.IsNullOrEmpty(builder.FailoverPartner) || ConnectionGuard.IsLocalDataSource(builder.FailoverPartner));
            Assert.True(sqlClientLocal == verdict.IsLocal, $"Verdict mismatch for: {connectionString}");
        }
    }
}
