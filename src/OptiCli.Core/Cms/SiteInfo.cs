using System.Text.Json.Serialization;

namespace OptiCli.Core.Cms;

/// <summary>
/// A site: on CMS 12 a site definition (<c>tblSiteDefinition</c>), on CMS 13 an application (<c>tblApplication</c>) in the
/// same shape, with what only an application has added at the end.
/// </summary>
/// <param name="Id"><c>tblSiteDefinition.pkID</c>, or <c>tblApplication.pkID</c> on CMS 13.</param>
/// <param name="Guid">The site definition's GUID; null on CMS 13, where an application has none (it is known by <see cref="Application"/>).</param>
/// <param name="Name">The site's name; on CMS 13 the application's display name.</param>
/// <param name="Url">The site URL (<c>SiteUrl</c>); on CMS 13 what the CMS gives as the application's URL: its first primary host, else its first default one.</param>
/// <param name="Application">CMS 13: the application's name (<c>tblApplication.Name</c>), the key the CMS knows it by; null on CMS 12.</param>
/// <param name="ApplicationType">CMS 13: <c>inProcessWebsite</c> (the site renders itself) or <c>website</c> (a separate front end); null on CMS 12.</param>
/// <param name="IsDefault">
/// CMS 13: the application that answers every host no application has (CMS 12's <c>*</c> host, which CMS 13 doesn't
/// allow); null on CMS 12.
/// </param>
public sealed record SiteInfo(
    int Id,
    Guid? Guid,
    string Name,
    string? Url,
    string? StartPage,
    string? StartPageName,
    string? MasterLanguage,
    string? AssetsRoot,
    IReadOnlyList<HostInfo> Hosts,
    string? Application = null,
    string? ApplicationType = null,
    bool? IsDefault = null)
{
    /// <summary>How the site agent is told which site: the application's name on CMS 13, the GUID on CMS 12.</summary>
    [JsonIgnore]
    public string Key => Application ?? Guid?.ToString("D") ?? Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <param name="Language">The language the host is for (<c>tblHostDefinition.Language</c>, CMS 13 <c>tblApplicationHost.Locale</c>); null for every language.</param>
/// <param name="Https">Whether the CMS links to it with https; null when unset (CMS 12 only: CMS 13 always says).</param>
public sealed record HostInfo(string Name, HostType Type, string? Language, bool? Https);

/// <summary>
/// Values of <c>tblHostDefinition.Type</c> (EPiServer.Web.HostDefinitionType). CMS 13's <c>ApplicationHostType</c> numbers
/// its types differently and has two more (<see cref="Preview"/>, <see cref="Media"/>); <see cref="SiteReader"/> maps them.
/// </summary>
public enum HostType
{
    /// <summary>CMS 13: <c>Default</c>.</summary>
    Undefined = 0,
    Primary = 1,
    RedirectPermanent = 2,
    RedirectTemporary = 3,
    Edit = 4,

    /// <summary>CMS 13 only: where the edit UI previews content.</summary>
    Preview = 5,

    /// <summary>CMS 13 only: where media is served from.</summary>
    Media = 6,
}
