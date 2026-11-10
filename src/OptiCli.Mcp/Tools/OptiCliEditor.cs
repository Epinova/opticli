using System.Globalization;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using OptiCli.Cms;
using OptiCli.Cms.Content;
using OptiCli.Cms.Operations;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// <see cref="IOptiCliEditor"/>: the public face of <see cref="McpEditor"/> and a content operation as the editor
/// (<see cref="CmsCaller.Editor"/>), so a site's tool reads with the editor's access rights as the module's own do.
/// </summary>
/// <remarks>
/// Made from <see cref="IHttpContextAccessor"/>, as <see cref="McpEditor.From"/> is, never from the scope it is resolved
/// in: a site whose own MCP server is stateful makes a scope per MCP request, not the HTTP request's.
/// </remarks>
internal sealed class OptiCliEditor : IOptiCliEditor
{
    private readonly McpEditor _editor;
    private readonly HttpContext _context;
    private readonly CmsCall _call;

    private OptiCliEditor(McpEditor editor, HttpContext context, OptiCliMcpOptions site)
    {
        _editor = editor;
        _context = context;
        _call = editor.Call(site);
    }

    /// <exception cref="McpException">Not a request of the module's own MCP server, for a signed-in editor (<see cref="McpEditor.From"/>).</exception>
    public static OptiCliEditor From(IServiceProvider services)
    {
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        var editor = McpEditor.From(accessor);
        return new OptiCliEditor(editor, accessor.HttpContext!, services.GetRequiredService<IOptions<OptiCliMcpOptions>>().Value);
    }

    public string Name => _editor.Name;

    public IReadOnlyList<string> Roles => _editor.Roles;

    public IReadOnlyList<string> Scopes => _editor.Scopes;

    public bool HasScope(string scope) => _editor.Scopes.Contains(scope, StringComparer.Ordinal);

    public T Load<T>(ContentReference link, string? lang = null, bool latest = false) where T : IContent
    {
        if (ContentReference.IsNullOrEmpty(link))
        {
            throw new OptiCliToolException(AgentErrorCodes.NotFound, "No such content.");
        }
        if (latest && link.WorkID > 0)
        {
            throw new ArgumentException($"{link} is a version: load it, or the latest version of {link.ToReferenceWithoutVersion()}, not both.", nameof(latest));
        }
        IContent content;
        try
        {
            // get_content's own load: the same language rules, the same not_found for what the editor can't read.
            content = ReadOperation.Load(_call, link.ToString(), new ReadRequest(lang, latest ? ReadQuery.Latest : null));
        }
        catch (AgentException e)
        {
            throw new OptiCliToolException(e.Code, e.Message, e.Hint);
        }
        catch (ContentNotFoundException)
        {
            throw new OptiCliToolException(AgentErrorCodes.NotFound, CmsCall.NotFound(link).Message);
        }
        return content is T typed
            ? typed
            : throw new OptiCliToolException(AgentErrorCodes.Usage, $"Content {link.ID} is a {TypeName(content)}, not a {typeof(T).Name}.");
    }

    public bool CanRead(IContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return _call.CanRead(content);
    }

    public IReadOnlyList<IContent> Readable(IEnumerable<ContentReference> links, string? lang = null)
    {
        ArgumentNullException.ThrowIfNull(links);
        var loader = _call.Service<IContentLoader>();
        var versions = _call.Service<IContentVersionRepository>();
        var language = string.IsNullOrWhiteSpace(lang) ? null : Culture(lang.Trim());
        var options = new LoaderOptions { language is null ? LanguageLoaderOption.MasterLanguage() : LanguageLoaderOption.FallbackWithMaster(language) };
        var readable = new List<IContent>();
        foreach (var link in links)
        {
            if (ContentReference.IsNullOrEmpty(link))
            {
                continue;
            }
            IContent? content;
            if (link.WorkID > 0)
            {
                // A version belongs to one language branch: load it in that one. The CMS loads a version id of other
                // content as that content, so the id is compared too.
                content = versions.Load(link) is { } version && version.ContentLink.ID == link.ID
                    && loader.TryGet(link, string.IsNullOrEmpty(version.LanguageBranch) ? new LoaderOptions() : new LoaderOptions { LanguageLoaderOption.Specific(CultureInfo.GetCultureInfo(version.LanguageBranch)) }, out IContent found)
                    && found.ContentLink.ID == link.ID
                        ? found
                        : null;
            }
            else
            {
                content = loader.TryGet(link, options, out IContent found) ? found : null;
            }
            if (content is not null && _call.CanRead(content))
            {
                readable.Add(content);
            }
        }
        return readable;
    }

    public IReadOnlyList<T> Readable<T>(IEnumerable<T> contents) where T : IContent
    {
        ArgumentNullException.ThrowIfNull(contents);
        return contents.Where(c => c is not null && _call.CanRead(c)).ToList();
    }

    public ContentLinkSummary Describe(IContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!_call.CanRead(content))
        {
            throw new OptiCliToolException(AgentErrorCodes.NotFound, CmsCall.NotFound(content.ContentLink).Message);
        }
        var summary = ContentSummaries.Describe(content, _call.Service<IContentTypeRepository>());
        return new ContentLinkSummary
        {
            Ref = summary.Ref,
            Id = summary.Id,
            Version = summary.Version,
            Guid = summary.Guid,
            Name = summary.Name,
            Type = summary.Type,
            Language = summary.Language,
            Status = summary.Status,
            Parent = summary.Parent,
            Url = ResultUrls.PublicUrl(summary, _context),
            EditUrl = ResultUrls.EditUrl(summary, _context),
        };
    }

    private string TypeName(IContent content) => _call.Service<IContentTypeRepository>().Load(content.ContentTypeID)?.Name ?? content.GetType().Name;

    private static CultureInfo Culture(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            throw new OptiCliToolException(AgentErrorCodes.Usage, $"'{code}' is not a language code.");
        }
    }
}
