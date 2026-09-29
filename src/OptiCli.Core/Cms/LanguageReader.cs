using OptiCli.Core.Data;

namespace OptiCli.Core.Cms;

public static class LanguageReader
{
    private const string LanguagesSql = """
        SELECT lb.pkID, RTRIM(lb.LanguageID) AS Code, lb.Name, lb.Enabled, lb.SortIndex, lb.URLSegment,
               (SELECT COUNT(*) FROM tblContentLanguage cl WHERE cl.fkLanguageBranchID = lb.pkID) AS ContentItems
        FROM tblLanguageBranch lb
        ORDER BY lb.SortIndex, lb.LanguageID
        """;

    private const string MasterLanguagesSql = """
        SELECT c.fkMasterLanguageBranchID AS LanguageId, s.Name
        FROM tblSiteDefinition s
        JOIN tblContent c ON c.pkID = TRY_CAST(s.StartPage AS int)
        ORDER BY s.pkID
        """;

    public static async Task<IReadOnlyList<LanguageInfo>> ListAsync(CmsDatabase db, CancellationToken cancellationToken)
    {
        var masters = await db.QueryAsync(MasterLanguagesSql, r => (LanguageId: r.GetInt32("LanguageId"), Site: r.GetString("Name")), cancellationToken);
        var sitesByLanguage = masters.ToLookup(m => m.LanguageId, m => m.Site);

        return await db.QueryAsync(LanguagesSql, r =>
        {
            var id = r.GetInt32("pkID");
            return new LanguageInfo(
                id,
                r.GetString("Code"),
                r.GetStringOrNull("Name"),
                r.GetBoolean(r.GetOrdinal("Enabled")),
                r.GetInt32("SortIndex"),
                r.GetStringOrNull("URLSegment"),
                r.GetInt32("ContentItems"),
                sitesByLanguage[id].ToList());
        }, cancellationToken);
    }
}
