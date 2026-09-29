namespace OptiCli.Core.Cms;

public sealed record SiteInfo(
    int Id,
    Guid Guid,
    string Name,
    string? Url,
    string? StartPage,
    string? StartPageName,
    string? MasterLanguage,
    string? AssetsRoot,
    IReadOnlyList<HostInfo> Hosts);

public sealed record HostInfo(string Name, HostType Type, string? Language, bool? Https);

/// <summary>Values of <c>tblHostDefinition.Type</c> (EPiServer.Web.HostDefinitionType).</summary>
public enum HostType
{
    Undefined = 0,
    Primary = 1,
    RedirectPermanent = 2,
    RedirectTemporary = 3,
    Edit = 4,
}
