using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPiServer.Core;
using EPiServer.Web.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// Where the editor reviews a write in the CMS: the version's URL in the edit UI, added to every write result as
/// <c>editUrl</c>. Only the MCP module adds it, so the agent's protocol, which the CLI shares, stays as it is.
/// </summary>
/// <remarks>
/// The URL comes from the CMS UI's own <see cref="EditUrlResolver"/>, as its search results and reports build theirs:
/// <c>/EPiServer/CMS/?language=en#context=epi.cms.contentdata:///123_456</c>, on the site's edit host when it has one,
/// which takes care of where the shell is mapped. A relative URL is made absolute with the MCP request's origin, the
/// host the editor connected the assistant to.
/// </remarks>
internal static class EditUrls
{
    public const string Property = "editUrl";

    /// <summary><paramref name="result"/> as JSON, with <see cref="Property"/> added when the content has an edit URL.</summary>
    /// <param name="wholeContent">Link to the content rather than the version the result names (a discarded version is gone).</param>
    public static string Serialize<T>(T result, ContentSummary? content, HttpContext context, bool wholeContent = false)
    {
        var json = JsonSerializer.SerializeToNode(result, AgentJson.Options)!.AsObject();
        if (For(content, context, wholeContent) is { } url)
        {
            json[Property] = url;
        }
        return json.ToJsonString(AgentJson.Options);
    }

    /// <returns>Null for no content (a dry-run create), or when the site has no CMS edit UI.</returns>
    public static string? For(ContentSummary? content, HttpContext context, bool wholeContent = false)
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
