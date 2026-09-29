using OptiCli.Core.Data;

namespace OptiCli.Core.Cms;

public static class DatabaseInfoReader
{
    // sp_DatabaseVersion only does `RETURN <n>`; CMS uses it to decide whether to upgrade the schema.
    private const string Sql = """
        DECLARE @schemaVersion int;
        EXEC @schemaVersion = dbo.sp_DatabaseVersion;
        SELECT DB_NAME() AS DatabaseName,
               @schemaVersion AS SchemaVersion,
               CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS SqlServerVersion,
               (SELECT COUNT(*) FROM tblContentType) AS ContentTypes,
               (SELECT COUNT(*) FROM tblContent WHERE Deleted = 0) AS ContentItems
        """;

    public static async Task<DatabaseInfo> ReadAsync(CmsDatabase db, CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync(Sql, r => new DatabaseInfo(
            r.GetString("DatabaseName"),
            r.GetInt32OrNull("SchemaVersion"),
            r.GetStringOrNull("SqlServerVersion"),
            r.GetInt32("ContentTypes"),
            r.GetInt32("ContentItems")), cancellationToken);
        return rows[0];
    }
}
