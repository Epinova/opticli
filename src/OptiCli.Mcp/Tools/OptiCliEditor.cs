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
    private readonly IHttpContextAccessor _accessor;
    private readonly HttpContext _context;
    private readonly CmsCall _call;

    private OptiCliEditor(McpEditor editor, IHttpContextAccessor accessor, OptiCliMcpOptions site)
    {
        _editor = editor;
        _accessor = accessor;
        _context = accessor.HttpContext!;
        _call = editor.Call(site);
    }

    /// <summary>
    /// The content operation as the editor, for the request this was made for only. The CMS checks access with the
    /// principal of whatever request runs, so a tool that kept this past its call (a singleton, a cache, a background
    /// task) would read with another request's rights, or none.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another request than the one this was made for, or none.</exception>
    private CmsCall Call
    {
        get
        {
            if (!ReferenceEquals(_accessor.HttpContext, _context))
            {
                throw new InvalidOperationException(
                    "IOptiCliEditor was used outside the MCP tool call it was made for. Take it in the tool class's constructor or the tool method, and use it only during that call: never keep it in a singleton, a cache or a background task.");
            }
            return _call;
        }
    }

    /// <exception cref="McpException">Not a request of the module's own MCP server, for a signed-in editor (<see cref="McpEditor.From"/>).</exception>
    public static OptiCliEditor From(IServiceProvider services)
    {
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        var editor = McpEditor.From(accessor);
        return new OptiCliEditor(editor, accessor, services.GetRequiredService<IOptions<OptiCliMcpOptions>>().Value);
    }

    public string Name => _editor.Name;

    public IReadOnlyList<string> Roles => _editor.Roles;

    public IReadOnlyList<string> Scopes => _editor.Scopes;

    public bool HasScope(string scope) => _editor.Scopes.Contains(scope, StringComparer.Ordinal);

    public T Load<T>(ContentReference link, string? lang = null, bool latest = false) where T : IContentData
    {
        var call = Call;
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
            content = ReadOperation.Load(call, link.ToString(), new ReadRequest(lang, latest ? ReadQuery.Latest : null));
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
        return Call.CanRead(content);
    }

    public IReadOnlyList<IContent> Readable(IEnumerable<ContentReference> links, string? lang = null)
    {
        ArgumentNullException.ThrowIfNull(links);
        var loader = Call.Service<IContentLoader>();
        var versions = Call.Service<IContentVersionRepository>();
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
            if (content is not null && Call.CanRead(content))
            {
                readable.Add(content);
            }
        }
        return readable;
    }

    public IReadOnlyList<T> Readable<T>(IEnumerable<T> contents) where T : IContent
    {
        ArgumentNullException.ThrowIfNull(contents);
        return contents.Where(c => c is not null && Call.CanRead(c)).ToList();
    }

    public ContentDescription Describe(IContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var call = Call;
        if (!call.CanRead(content))
        {
            // Without the id: it came from the site's code (a search index, say), not from the assistant, so naming it
            // would tell which hidden item matched.
            throw new OptiCliToolException(AgentErrorCodes.NotFound, "No such content.");
        }
        var summary = ContentSummaries.Describe(content, call.Service<IContentTypeRepository>());
        return new ContentDescription
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

    private string TypeName(IContent content) => Call.Service<IContentTypeRepository>().Load(content.ContentTypeID)?.Name ?? content.GetType().Name;

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
