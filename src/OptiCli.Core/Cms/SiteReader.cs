using OptiCli.Core.Data;

namespace OptiCli.Core.Cms;

public static class SiteReader
{
    private const string SitesSql = """
        SELECT s.pkID, s.UniqueId, s.Name, s.SiteUrl, s.StartPage, s.SiteAssetsRoot,
               cl.Name AS StartPageName, RTRIM(lb.LanguageID) AS MasterLanguage
        FROM tblSiteDefinition s
        LEFT JOIN tblContent c ON c.pkID = TRY_CAST(s.StartPage AS int)
        LEFT JOIN tblContentLanguage cl ON cl.fkContentID = c.pkID AND cl.fkLanguageBranchID = c.fkMasterLanguageBranchID
        LEFT JOIN tblLanguageBranch lb ON lb.pkID = c.fkMasterLanguageBranchID
        ORDER BY s.pkID
        """;

    private const string HostsSql = """
        SELECT h.fkSiteID, h.Name, h.Type, RTRIM(h.Language) AS Language, h.Https
        FROM tblHostDefinition h
        ORDER BY h.fkSiteID, h.pkID
        """;

    public static async Task<IReadOnlyList<SiteInfo>> ListAsync(CmsDatabase db, CancellationToken cancellationToken)
    {
        var hosts = await db.QueryAsync(HostsSql, r => (
            SiteId: r.GetInt32("fkSiteID"),
            Host: new HostInfo(
                r.GetString("Name"),
                (HostType)r.GetInt32("Type"),
                NullIfEmpty(r.GetStringOrNull("Language")),
                r.GetBooleanOrNull("Https"))), cancellationToken);

        var hostsBySite = hosts.ToLookup(h => h.SiteId, h => h.Host);

        return await db.QueryAsync(SitesSql, r =>
        {
            var id = r.GetInt32("pkID");
            return new SiteInfo(
                id,
                r.GetGuid("UniqueId"),
                r.GetString("Name"),
                r.GetStringOrNull("SiteUrl"),
                r.GetStringOrNull("StartPage"),
                r.GetStringOrNull("StartPageName"),
                r.GetStringOrNull("MasterLanguage"),
                r.GetStringOrNull("SiteAssetsRoot"),
                hostsBySite[id].ToList());
        }, cancellationToken);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
