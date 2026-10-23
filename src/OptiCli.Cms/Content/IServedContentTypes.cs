namespace OptiCli.Cms.Content;

/// <summary>
/// The MIME type the site serves a file as, by its name, as its media URLs get it: the CMS hands media to ASP.NET Core's
/// static files, which go by the URL's extension (the site's <c>StaticFileOptions.ContentTypeProvider</c>). The MCP
/// module provides it from ASP.NET Core, which the content operations don't reference.
/// </summary>
internal interface IServedContentTypes
{
    /// <returns>The MIME type; null for a name whose extension has none.</returns>
    string? For(string fileName);
}
