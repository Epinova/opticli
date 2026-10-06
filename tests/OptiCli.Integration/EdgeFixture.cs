using OptiCli.Core.Content;

namespace OptiCli.Integration;

/// <summary>
/// Content of the edge-case site (tests/fixtures/edge-cases) that tests use by GUID: the same GUIDs as in its plan and
/// EdgeCasesFixture.cs. Tests that need it return early on a site without it.
/// </summary>
internal static class EdgeFixture
{
    /// <summary>A page with an approval sequence (one step, role WebAdmins) that its descendants inherit.</summary>
    public static readonly Guid ApprovalRoot = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e02");

    /// <summary>The start page of the second site, which is below the first site's start page.</summary>
    public static readonly Guid NestedStart = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e01");

    /// <summary>A page with language settings: Swedish falls back to English below it.</summary>
    public static readonly Guid LanguageRoot = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e03");

    /// <summary>
    /// Two sites whose hosts the site host tests change and restore: <see cref="HostsSiteA"/> has a primary host, a
    /// Swedish primary host on a production-like name and an Edit host; <see cref="HostsSiteB"/> one primary host.
    /// </summary>
    public const string HostsSiteA = "Edge hosts A";

    public const string HostsSiteB = "Edge hosts B";

    /// <summary>A site whose hosts are all for English (hosts-c.localhost primary, alt.hosts-c.localhost undefined, https), no primary for every language.</summary>
    public const string HostsSiteC = "Edge hosts C";

    /// <summary>The visitor group the personalized page's ContentArea uses ("Edge visitors").</summary>
    public static readonly Guid VisitorGroup = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e10");

    /// <summary>
    /// Whether the site can take a PDF upload: the edge-case fixture's EdgeDocument, since Alloy (CMS 12's and CMS 13's
    /// template alike) has no media type for .pdf.
    /// </summary>
    public static bool TakesPdf(SiteUnderTest site) => site.Session.Model.Types.Any(t => t.Name == "EdgeDocument");

    /// <returns>The content id, or null when the site has no such content (not the edge-case site).</returns>
    public static async Task<ContentHeader?> FindAsync(SiteUnderTest site, Guid guid, CancellationToken cancellationToken)
    {
        var ids = await ContentHeaderReader.IdsByGuidsAsync(site.Session.Db, [guid], cancellationToken);
        return ids.TryGetValue(guid, out var id) ? await ContentHeaderReader.ByIdAsync(site.Session.Db, id, cancellationToken) : null;
    }
}
