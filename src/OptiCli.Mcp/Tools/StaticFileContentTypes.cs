using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using OptiCli.Cms.Content;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// The content types the CMS serves media with (<see cref="IServedContentTypes"/>): its blob handler runs ASP.NET Core's
/// static files with the site's <see cref="StaticFileOptions.ContentTypeProvider"/>, or the default one without it.
/// </summary>
internal sealed class StaticFileContentTypes(IOptions<StaticFileOptions> options) : IServedContentTypes
{
    private readonly IContentTypeProvider _provider = options.Value.ContentTypeProvider ?? new FileExtensionContentTypeProvider();

    public string? For(string fileName) => _provider.TryGetContentType(fileName, out var type) ? type : null;
}
