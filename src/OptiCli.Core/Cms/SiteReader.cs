using OptiCli.Core.Data;

namespace OptiCli.Core.Cms;

/// <summary>
/// The sites: CMS 12's site definitions, or, on CMS 13 (<see cref="CmsSchema.Applications"/>), its applications in the
/// same shape. CMS 13 leaves <c>tblSiteDefinition</c> empty on a new database and stale after an upgrade, so it is never
/// read there.
/// </summary>
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

    // Only the application types the CMS loads (ApplicationDB skips others). Its assets root counts only when the
    // application is resourceable; otherwise it uses the global assets, which is what CMS 12 shows as such a site's
    // assets root (found by name, as CmsModel finds it).
    private static readonly string ApplicationsSql = $"""
        SELECT a.pkID, a.Name, a.DisplayName, a.Type, a.RoutingEntryPoint, CONVERT(bit, a.IsDefault) AS IsDefault,
               CASE WHEN a.IsResourceable = 1 AND a.fkAssetsRootID IS NOT NULL THEN a.fkAssetsRootID ELSE (
                   SELECT TOP 1 g.pkID FROM tblContent g JOIN tblContentLanguage gl ON gl.fkContentID = g.pkID
                   WHERE g.fkParentID = (SELECT MIN(pkID) FROM tblContent WHERE fkParentID IS NULL) AND gl.Name = 'SysGlobalAssets'
                   ORDER BY g.pkID) END AS AssetsRoot,
               cl.Name AS StartPageName, RTRIM(lb.LanguageID) AS MasterLanguage
        FROM tblApplication a
        LEFT JOIN tblContent c ON c.pkID = TRY_CAST(a.RoutingEntryPoint AS int)
        LEFT JOIN tblContentLanguage cl ON cl.fkContentID = c.pkID AND cl.fkLanguageBranchID = c.fkMasterLanguageBranchID
        LEFT JOIN tblLanguageBranch lb ON lb.pkID = c.fkMasterLanguageBranchID
        WHERE a.Type IN ({ApplicationTypes.InProcessWebsite}, {ApplicationTypes.Website})
        ORDER BY a.pkID
        """;

    private const string ApplicationHostsSql = """
        SELECT h.fkApplicationID, h.Authority, h.Type, RTRIM(h.Locale) AS Locale, CONVERT(bit, h.UseSecureConnection) AS UseSecureConnection
        FROM tblApplicationHost h
        ORDER BY h.fkApplicationID, h.pkID
        """;

    public static async Task<IReadOnlyList<SiteInfo>> ListAsync(CmsDatabase db, CancellationToken cancellationToken)
    {
        if ((await db.SchemaAsync(cancellationToken)).Applications)
        {
            var applicationHosts = await db.QueryAsync(ApplicationHostsSql, r => new ApplicationHostRow(
                r.GetInt32("fkApplicationID"),
                r.GetString("Authority"),
                r.GetInt32("Type"),
                r.GetStringOrNull("Locale"),
                r.GetBooleanOrNull("UseSecureConnection") ?? false), cancellationToken);
            var applications = await db.QueryAsync(ApplicationsSql, r => new ApplicationRow(
                r.GetInt32("pkID"),
                r.GetString("Name"),
                r.GetStringOrNull("DisplayName"),
                r.GetInt32("Type"),
                r.GetStringOrNull("RoutingEntryPoint"),
                r.GetInt32OrNull("AssetsRoot"),
                r.GetBooleanOrNull("IsDefault") ?? false,
                r.GetStringOrNull("StartPageName"),
                r.GetStringOrNull("MasterLanguage")), cancellationToken);
            return FromApplications(applications, applicationHosts);
        }

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

    /// <summary>A row of <c>tblApplication</c>, with its start page's name and master language.</summary>
    /// <param name="Type"><see cref="ApplicationTypes"/>.</param>
    /// <param name="AssetsRoot">Its own assets folder when it is resourceable, else the global assets folder.</param>
    internal sealed record ApplicationRow(int Id, string Name, string? DisplayName, int Type, string? EntryPoint, int? AssetsRoot, bool IsDefault, string? StartPageName, string? MasterLanguage);

    /// <summary>A row of <c>tblApplicationHost</c>.</summary>
    /// <param name="Type">An <c>EPiServer.Applications.ApplicationHostType</c> value.</param>
    internal sealed record ApplicationHostRow(int ApplicationId, string Authority, int Type, string? Locale, bool Https);

    /// <summary>
    /// CMS 13 applications as sites: the display name is the name, the routing entry point the start page, the locale the
    /// host's language. The URL is the CMS's (<c>IRoutableApplication.Url</c>): the first primary host, else the first
    /// default one, with https when the host says so.
    /// </summary>
    internal static IReadOnlyList<SiteInfo> FromApplications(IReadOnlyList<ApplicationRow> applications, IReadOnlyList<ApplicationHostRow> hosts)
    {
        var hostsByApplication = hosts.ToLookup(h => h.ApplicationId);
        return applications.Select(a =>
        {
            var own = hostsByApplication[a.Id].ToList();
            var urlHost = own.FirstOrDefault(h => h.Type == ApplicationHostTypes.Primary) ?? own.FirstOrDefault(h => h.Type == ApplicationHostTypes.Default);
            return new SiteInfo(
                a.Id,
                null,
                string.IsNullOrWhiteSpace(a.DisplayName) ? a.Name : a.DisplayName,
                urlHost is null ? null : $"{(urlHost.Https ? "https" : "http")}://{urlHost.Authority}/",
                NullIfEmpty(a.EntryPoint),
                a.StartPageName,
                a.MasterLanguage,
                a.AssetsRoot?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                own.Select(h => new HostInfo(h.Authority, ApplicationHostTypes.ToHostType(h.Type), NullIfEmpty(h.Locale), h.Https)).ToList(),
                a.Name,
                ApplicationTypes.Name(a.Type),
                a.IsDefault);
        }).ToList();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary><c>tblApplication.Type</c> values the CMS loads (<c>ApplicationDB.ApplicationType</c>).</summary>
public static class ApplicationTypes
{
    /// <summary>A site that renders itself (<c>InProcessWebsite</c>).</summary>
    public const int InProcessWebsite = 1;

    /// <summary>A separate front end, e.g. headless (<c>Website</c>).</summary>
    public const int Website = 2;

    public static string Name(int type) => type switch
    {
        InProcessWebsite => "inProcessWebsite",
        Website => "website",
        _ => $"type{type}",
    };
}

/// <summary><c>tblApplicationHost.Type</c> values (<c>EPiServer.Applications.ApplicationHostType</c>), and their CMS 12 equivalents.</summary>
public static class ApplicationHostTypes
{
    public const int Default = 0;
    public const int Primary = 1;
    public const int Preview = 2;
    public const int RedirectPermanent = 3;
    public const int RedirectTemporary = 4;
    public const int Edit = 5;
    public const int Media = 6;

    /// <summary>As <see cref="HostType"/>: <c>Default</c> is CMS 12's undefined host; an unknown value counts as one too.</summary>
    public static HostType ToHostType(int type) => type switch
    {
        Primary => HostType.Primary,
        Preview => HostType.Preview,
        RedirectPermanent => HostType.RedirectPermanent,
        RedirectTemporary => HostType.RedirectTemporary,
        Edit => HostType.Edit,
        Media => HostType.Media,
        _ => HostType.Undefined,
    };
}
