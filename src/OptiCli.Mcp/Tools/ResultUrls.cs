using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPiServer.Core;
using EPiServer.Web.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// Where the content a write names is: its public URL (<c>url</c>), and where the editor reviews the version in the CMS
/// (<c>editUrl</c>), added to every write result. Only the MCP module adds them, so the agent's protocol, which the CLI
/// shares, stays as it is.
/// </summary>
/// <remarks>
/// The public URL is the one <c>get_content</c> gives, with the site's host (<see cref="PublicUrls"/>); for a draft of
/// content that was never published, where it will be once it is. The edit URL comes from the CMS UI's own
/// <see cref="EditUrlResolver"/>, as its search results and reports build theirs:
/// <c>/EPiServer/CMS/?language=en#context=epi.cms.contentdata:///123_456</c>, on the site's edit host when it has one,
/// which takes care of where the shell is mapped. A relative URL is made absolute with the MCP request's origin, the
/// host the editor connected the assistant to.
/// </remarks>
internal static class ResultUrls
{
    public const string Public = "url";

    public const string Edit = "editUrl";

    /// <summary><paramref name="result"/> as JSON, with <see cref="Public"/> and <see cref="Edit"/> added when the content has them.</summary>
    /// <param name="wholeContent">Link to the content rather than the version the result names (a discarded version is gone).</param>
    public static string Serialize<T>(T result, ContentSummary? content, HttpContext context, bool wholeContent = false)
    {
        var json = JsonSerializer.SerializeToNode(result, AgentJson.Options)!.AsObject();
        if (PublicUrl(content, context) is { } url)
        {
            json[Public] = url;
        }
        if (EditUrl(content, context, wholeContent) is { } editUrl)
        {
            json[Edit] = editUrl;
        }
        return json.ToJsonString(AgentJson.Options);
    }

    /// <returns>Null for no content (a dry-run create), or content without a URL (a block, a folder).</returns>
    public static string? PublicUrl(ContentSummary? content, HttpContext context) =>
        content is null || !ContentReference.TryParse(content.Ref, out var link) || context.RequestServices.GetService<IUrlResolver>() is not { } urls
            ? null
            : PublicUrls.Of(urls, link, content.Language, absolute: true);

    /// <returns>Null for no content (a dry-run create), or when the site has no CMS edit UI.</returns>
    public static string? EditUrl(ContentSummary? content, HttpContext context, bool wholeContent = false)
    {
        if (content is null || !ContentReference.TryParse(content.Ref, out var link) || context.RequestServices.GetService<EditUrlResolver>() is not { } resolver)
        {
            return null;
        }
        var arguments = new EditUrlArguments { ForceEditHost = true };
        if (!string.IsNullOrEmpty(content.Language))
        {
            arguments.Language = CultureInfo.GetCultureInfo(content.Language);
        }
        var url = resolver.GetEditViewUrl(wholeContent ? link.ToReferenceWithoutVersion() : link, arguments)?.ToString();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
            ? url
            : $"{context.Request.Scheme}://{context.Request.Host}{(url.StartsWith('/') ? "" : "/")}{url}";
    }
}
