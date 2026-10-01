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
    [InlineData("SELECT TOP 1 1 AS a --x\r\nFROM tblContent")]
    [InlineData("SELECT TOP 1 1 AS a --x\nFROM tblContent\r\nWHERE\tpkID > 1")]
    [InlineData("SELECT 'a\rb\u0001' AS text, [odd\rname] FROM tblContent")]
    [InlineData("SELECT o.name, c.name FROM sys.objects o JOIN sys.columns c ON c.object_id = o.object_id")]
    [InlineData("SELECT sys.tables.name FROM sys.tables JOIN sys.schemas s ON s.schema_id = sys.tables.schema_id")]
    [InlineData("SELECT * FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id JOIN sys.types t ON 1 = 1")]
    [InlineData("SELECT * FROM sys.foreign_keys, sys.key_constraints, sys.default_constraints, sys.check_constraints")]
    [InlineData("SELECT m.definition FROM sys.procedures p JOIN sys.sql_modules m ON m.object_id = p.object_id")]
    [InlineData("SELECT * FROM sys.partitions p JOIN sys.allocation_units a ON a.container_id = p.partition_id")]
    [InlineData("SELECT * FROM SYS.ALL_COLUMNS, [sys].[all_objects], sys.extended_properties, sys.views")]
    [InlineData("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.COLUMNS")]
    [InlineData("SELECT dbo.fn_Example(1)")]
    [InlineData("SELECT t.Data.value('(/a)[1]', 'int') FROM tblExample t")]
    [InlineData("SELECT x.n.value('.', 'nvarchar(50)') FROM tblExample AS t CROSS APPLY t.Data.nodes('/a') AS x(n)")]
    [InlineData("SELECT Data.query('/a') FROM tblExample WHERE tblExample.Data.exist('/a') = 1")]
    [InlineData("SELECT u.Data.value('.', 'int') FROM tblA t, tblB u")]
    [InlineData("SELECT d.Data.value('.', 'int') FROM (SELECT Data FROM tblExample) d")]
    [InlineData("SELECT t.pkID FROM tblExample t WHERE EXISTS (SELECT 1 FROM tblOther o WHERE t.Data.exist('/a') = 1)")]
    [InlineData("SELECT dbo.tblExample.Data.value('.', 'int') FROM dbo.tblExample")]
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
    [InlineData("SELECT TOP 1 1 AS a --x\rFROM sys.dm_exec_sessions", "carriage return")]
    [InlineData("SELECT TOP 1 1 AS a --x\rFROM tblContent", "carriage return")]
    [InlineData("SELECT 1 AS a /* x\r */", "carriage return")]
    [InlineData("SELECT 1 AS a\r", "carriage return")]
    [InlineData("SELECT 1 AS a --x\u0085, 2 AS b", "U+0085")]
    [InlineData("SELECT 1 AS a\v", "U+000B")]
    [InlineData("SELECT 1 AS a\f", "U+000C")]
    [InlineData("SELECT\u00001", "U+0000")]
    [InlineData("SELECT 1 /* \u001e */", "U+001E")]
    [InlineData("SELECT 1 AS a --x\u2028, 2 AS b", "U+2028")]
    [InlineData("SELECT 1 COMMIT", "COMMIT")]
    [InlineData("SELECT 1 /* x */ commit", "COMMIT")]
    [InlineData("SELECT 1 AS a -- x\nCOMMIT", "COMMIT")]
    [InlineData("SELECT 1 AS a -- x\r\nCOMMIT TRANSACTION", "COMMIT")]
    [InlineData("SELECT 1 ROLLBACK TRAN", "ROLLBACK")]
    [InlineData("SELECT 1 SAVE TRANSACTION x", "SAVE")]
    [InlineData("SELECT 1 BEGIN TRAN", "BEGIN")]
    [InlineData("SELECT * FROM fn_dblog(NULL, NULL)", "system function")]
    [InlineData("SELECT * FROM ::fn_dblog(NULL, NULL)", "system function")]
    [InlineData("SELECT * FROM sys.fn_dblog(NULL, NULL)", "system function")]
    [InlineData("SELECT * FROM fn_trace_gettable('x', default)", "system function")]
    [InlineData("SELECT * FROM sys.fn_xe_file_target_read_file('x', NULL, NULL, NULL)", "system function")]
    [InlineData("SELECT * FROM [sys].[fn_get_audit_file]('x', default, default)", "system function")]
    [InlineData("SELECT * FROM sys.sysprocesses", "compatibility view")]
    [InlineData("SELECT * FROM sysprocesses", "compatibility view")]
    [InlineData("SELECT name FROM dbo.sysobjects", "compatibility view")]
    [InlineData("SELECT * FROM sys.configurations", "server-wide")]
    [InlineData("SELECT * FROM sys.server_permissions", "server-wide")]
    [InlineData("SELECT * FROM sys.server_principals", "server-wide")]
    [InlineData("SELECT * FROM sys.master_files", "server-wide")]
    [InlineData("SELECT * FROM sys.database_files", "not one of the catalog views")]
    [InlineData("SELECT * FROM sys.objects o JOIN sys.event_notifications e ON 1 = 1", "not one of the catalog views")]
    [InlineData("SELECT TOP 1 1 AS a FROM sys.ｄｍ_exec_sessions", "server-wide")]
    [InlineData("SELECT * FROM ｓｙｓ.configurations", "server-wide")]
    [InlineData("SELECT * FROM sys.ｆｎ_dblog(NULL, NULL)", "system function")]
    [InlineData("SELECT ｘｐ_cmdshell", "procedure")]
    [InlineData("SELECT otherdb.dbo.value('.', 'int')", "another database")]
    [InlineData("SELECT otherdb.dbo.value('.', 'int') AS otherdb FROM tblExample", "another database")]
    [InlineData("SELECT 1 AS a FROM tblExample x UNION SELECT x.y.value('.', 'int')", "another database")]
    [InlineData("SELECT x.y.value('.', 'int'), (SELECT 1 FROM tblExample x) AS b", "another database")]
    [InlineData("SELECT t.Data.VALUE('.', 'int') FROM tblExample t", "another database")]
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

    [Fact]
    public void Xml_methods_are_not_part_of_a_name()
    {
        var names = SqlStatementGuard.Names(SqlTokenizer.Tokenize("SELECT t.Data.value('.', 'int'), Data.query('/a'), u.Data.value FROM tblExample t")).Select(n => string.Join("|", n)).ToList();

        Assert.Equal(["SELECT", "t|Data", "Data", "u|Data|value", "FROM", "tblExample", "t"], names);
    }

    [Fact]
    public void Personal_data_tables_match_regardless_of_width()
    {
        var refused = Assert.Throws<RefusedException>(() => SqlStatementGuard.Check("SELECT * FROM ｔblBigTable", includePersonalData: false));

        Assert.Contains("personal data", refused.Message);
    }
}
