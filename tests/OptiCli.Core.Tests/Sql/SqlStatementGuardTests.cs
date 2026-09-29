using OptiCli.Core.Errors;
using OptiCli.Core.Sql;

namespace OptiCli.Core.Tests.Sql;

public class SqlStatementGuardTests
{
    [Theory]
    [InlineData("SELECT TOP 3 pkID FROM tblContent")]
    [InlineData("select c.pkID, ct.Name from dbo.tblContent c join tblContentType ct on ct.pkID = c.fkContentTypeID;")]
    [InlineData("WITH x AS (SELECT pkID FROM tblContent) SELECT COUNT(*) FROM x;;")]
    [InlineData("SELECT 'DELETE FROM tblContent; DROP TABLE x' AS text, N'-- not a comment' AS t2")]
    [InlineData("SELECT [Update], \"Insert\" FROM tblContent -- UPDATE in a comment\n")]
    [InlineData("SELECT /* nested /* EXEC */ comment */ dbo.tblContent.pkID FROM dbo.tblContent")]
    [InlineData("SELECT t.* FROM sys.tables t")]
    [InlineData("SELECT DB_NAME()")]
    [InlineData("SELECT 1.5, 2e10, 3.0E-2, 0x1F, .5 FROM tblContent WHERE pkID > 10")]
    public void Allows_single_read_only_selects(string sql)
    {
        SqlStatementGuard.Check(sql, includePersonalData: false);
    }

    [Theory]
    [InlineData("UPDATE tblContent SET Deleted = 1", "starts with")]
    [InlineData("  delete from tblContent", "starts with")]
    [InlineData("SELECT 1; DELETE FROM tblContent", "more than one statement")]
    [InlineData("SELECT 1; SELECT 2", "more than one statement")]
    [InlineData("WITH x AS (SELECT 1 AS a) DELETE FROM tblContent", "DELETE")]
    [InlineData("SELECT * INTO copy FROM tblContent", "INTO")]
    [InlineData("SELECT 1 EXEC('DROP TABLE x')", "EXEC")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=remote;', 'SELECT 1')", "OPENROWSET")]
    [InlineData("SELECT * FROM OPENQUERY(remote, 'SELECT 1')", "OPENQUERY")]
    [InlineData("SELECT xp_cmdshell", "procedure")]
    [InlineData("SELECT * FROM otherdb.dbo.tblContent", "another database")]
    [InlineData("SELECT * FROM [other-db].[dbo].[tblContent]", "another database")]
    [InlineData("SELECT * FROM otherdb..tblContent", "another database")]
    [InlineData("SELECT * FROM remote.otherdb.dbo.tblContent", "another database")]
    [InlineData("SELECT name FROM sys.databases", "catalog")]
    [InlineData("SELECT * FROM sys.dm_exec_sessions", "catalog")]
    [InlineData("SELECT 1COMMIT SELECT 1DELETE FROM tblContent", "runs straight into")]
    [InlineData("SELECT 1e1EXEC('DROP TABLE x')", "runs straight into")]
    [InlineData("SELECT 0x1FUNION SELECT 1", "runs straight into")]
    [InlineData("SELECT 1 FROM tblContent EXEC [xp_cmdshell] 'whoami'", "EXEC")]
    [InlineData("SELECT [xp_cmdshell]", "procedure")]
    [InlineData("SELECT name FROM sys.[databases ]", "catalog")]
    [InlineData("SELECT name FROM sys.sql_logins", "catalog")]
    [InlineData("SELECT 1 /* unterminated", "unterminated")]
    [InlineData("SELECT 'unterminated", "unterminated")]
    [InlineData("", "empty")]
    [InlineData("-- only a comment", "empty")]
    public void Refuses_everything_else(string sql, string reason)
    {
        var refused = Assert.Throws<RefusedException>(() => SqlStatementGuard.Check(sql, includePersonalData: true));

        Assert.Contains(reason, refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT * FROM tblXFormData")]
    [InlineData("SELECT * FROM dbo.tblBigTable")]
    [InlineData("SELECT * FROM [VW_FormData_Contact]")]
    [InlineData("SELECT * FROM [tblBigTable ]")]
    [InlineData("SELECT Email FROM AspNetUsers")]
    [InlineData("SELECT * FROM aspnet_Membership")]
    [InlineData("SELECT u.Email FROM tblContent c JOIN \"tblSynchedUser\" u ON 1 = 1")]
    public void Personal_data_tables_need_the_flag(string sql)
    {
        var refused = Assert.Throws<RefusedException>(() => SqlStatementGuard.Check(sql, includePersonalData: false));
        Assert.Contains("personal data", refused.Message);
        Assert.Contains("--include-personal-data", refused.Hint);

        SqlStatementGuard.Check(sql, includePersonalData: true);
    }

    [Fact]
    public void Names_are_read_as_dotted_chains()
    {
        var names = SqlStatementGuard.Names(SqlTokenizer.Tokenize("SELECT a.b, [x y].\"z\", db..t, t.* FROM s.o")).Select(n => string.Join("|", n)).ToList();

        Assert.Equal(["SELECT", "a|b", "x y|z", "db||t", "t", "FROM", "s|o"], names);
    }
}
