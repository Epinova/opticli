using System.Globalization;
using System.Security.Claims;
using EPiServer;
using EPiServer.Core;
using EPiServer.Security;
using EPiServer.Web;
using EPiServer.Web.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace OptiCli.Mcp.Preview;

/// <summary>Where the CMS previews a version, for an editor who may read it: what a preview link renders.</summary>
internal interface IPreviewVersions
{
    /// <returns>
    /// The CMS's preview URL of <paramref name="version"/> in <paramref name="language"/> (<see cref="ContextMode.Preview"/>,
    /// as <c>previewUrl</c> has it); null when the version doesn't exist in that language, is in the recycle bin, isn't a
    /// page or a block, or <paramref name="editor"/> can't read it.
    /// </returns>
    string? PreviewUrl(ContentReference version, string? language, ClaimsPrincipal editor);
}

/// <summary>The CMS's own: the version as the content loader has it, its access rights, and the URL resolver's preview URL.</summary>
/// <remarks>The CMS's services are looked up when used, so the module's own services build without a CMS (its tests).</remarks>
internal sealed class CmsPreviewVersions(IServiceProvider services) : IPreviewVersions
{
    public string? PreviewUrl(ContentReference version, string? language, ClaimsPrincipal editor)
    {
        var loader = services.GetRequiredService<IContentLoader>();
        var found = string.IsNullOrEmpty(language)
            ? loader.TryGet<IContent>(version, out var content)
            : loader.TryGet(version, CultureInfo.GetCultureInfo(language), out content);
        // The version itself, in the link's language; the loader checks no access rights, so they are checked here.
        if (!found || content is not (PageData or BlockData) || content.IsDeleted
            || (!string.IsNullOrEmpty(language) && content is ILocale { Language: { } actual } && !string.Equals(actual.Name, language, StringComparison.OrdinalIgnoreCase))
            || (content is ISecurable securable && !securable.GetSecurityDescriptor().HasAccess(editor, AccessLevel.Read)))
        {
            return null;
        }
        // Never with AppendPreviewToken: the CMS's token signs its holder in as the editor (PreviewTokens).
        return services.GetRequiredService<IUrlResolver>().GetUrl(version, language, new UrlResolverArguments { ContextMode = ContextMode.Preview });
    }
}
