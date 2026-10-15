using OptiCli.Core.Data;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Cms;

/// <summary>
/// Whether the login opticli connects with could write to the CMS's content: what <c>doctor</c> checks for a production
/// database, whose saved login should only read.
/// </summary>
public static class WriteAccessReader
{
    // db_owner covers dbo and sysadmin; INSERT/UPDATE on tblContent covers a grant outside the fixed roles.
    private const string Sql = """
        SELECT CAST(IS_ROLEMEMBER('db_owner') AS int) AS DbOwner,
               CAST(IS_ROLEMEMBER('db_datawriter') AS int) AS DataWriter,
               HAS_PERMS_BY_NAME('dbo.tblContent', 'OBJECT', 'INSERT') AS CanInsert,
               HAS_PERMS_BY_NAME('dbo.tblContent', 'OBJECT', 'UPDATE') AS CanUpdate
        """;

    /// <returns>The roles or permissions that let it write (empty when none do); null when the check itself failed or was denied.</returns>
    public static async Task<IReadOnlyList<string>?> ReadAsync(CmsDatabase db, CancellationToken cancellationToken)
    {
        try
        {
            var row = (await db.QueryAsync(Sql, r => new
            {
                DbOwner = r.GetInt32OrNull("DbOwner"),
                DataWriter = r.GetInt32OrNull("DataWriter"),
                CanInsert = r.GetInt32OrNull("CanInsert"),
                CanUpdate = r.GetInt32OrNull("CanUpdate"),
            }, cancellationToken)).Single();
            var found = new List<string>();
            if (row.DbOwner == 1)
            {
                found.Add("db_owner");
            }
            if (row.DataWriter == 1)
            {
                found.Add("db_datawriter");
            }
            if (row.CanInsert == 1)
            {
                found.Add("INSERT on tblContent");
            }
            if (row.CanUpdate == 1)
            {
                found.Add("UPDATE on tblContent");
            }
            return found;
        }
        catch (OptiCliException)
        {
            return null;
        }
    }
}
