using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPiServer;
using EPiServer.Core;
using EPiServer.Web;
using EPiServer.Web.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiCli.Cms.Content;
using OptiCli.Mcp.Preview;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// Where the content a result names is: its public URL (<c>url</c>), where the editor reviews the version in the CMS
/// (<c>editUrl</c>), and where the version shows as visitors would see it once published (<c>previewUrl</c>), added to
/// write results (and <c>previewUrl</c> to <c>get_content</c>). Only the MCP module adds them, so the agent's protocol,
/// which the CLI shares, stays as it is.
/// </summary>
/// <remarks>
/// <para>
/// The public URL is the one <c>get_content</c> gives, with the site's host (<see cref="PublicUrls"/>); for a draft of
/// content that was never published, where it will be once it is. The edit URL comes from the CMS UI's own
/// <see cref="EditUrlResolver"/>, as its search results and reports build theirs:
/// <c>/EPiServer/CMS/?language=en#context=epi.cms.contentdata:///123_456</c>, on the site's edit host when it has one,
/// which takes care of where the shell is mapped. A relative URL is made absolute with the MCP request's origin, the
/// host the editor connected the assistant to.
/// </para>
/// <para>
/// The preview URL is the CMS's own preview of the version (<see cref="ContextMode.Preview"/>), the page the edit UI
/// shows around its editing tools, without them: <c>/EPiServer/CMS/Content/en/about-us,,123_456/?epieditmode=false</c>.
/// It needs a signed-in editor, as the edit UI does, so it is on the edit URL's host, where the editor signs in. It is
/// left out for a version visitors already see (<c>url</c> shows it), a version that isn't saved (a dry run) or no
/// longer exists (a discarded one), and for media and folders, which have no page of their own to preview.
/// </para>
/// <para>
/// Unless the site turned it off (<see cref="OptiCliMcpOptions.AllowPreviewLinks"/>), the same version also gets
/// <c>assistantPreviewUrl</c>: a short-lived link of the assistant's own, which opens without a sign-in
/// (<see cref="PreviewLinks"/>), and <c>assistantPreviewExpires</c>, when it stops working.
/// </para>
/// </remarks>
internal static class ResultUrls
{
    public const string Public = "url";

    public const string Edit = "editUrl";

    public const string Preview = "previewUrl";

    public const string AssistantPreview = "assistantPreviewUrl";

    public const string AssistantPreviewExpires = "assistantPreviewExpires";

    public const string ProjectUrl = "projectUrl";

    public const string ProjectNote = "projectNote";

    /// <summary>
    /// Where a project is in a site with only the Projects gadget (<see cref="ProjectEditorUi.Gadget"/>), in place of a
    /// <see cref="ProjectUrl"/>: there, the edit UI's project link opens an empty overview.
    /// </summary>
    public const string GadgetNote = "This site's CMS has no project mode, so there is no link to the project: the user opens the Projects gadget in the CMS edit UI (Add Gadgets in a pane, if it isn't there yet) and picks the project. Its Publish button puts every unpublished item of the project live, ready or not.";

    /// <summary>What the tool descriptions say about <see cref="AssistantPreview"/>, whether or not the site allows it.</summary>
    public const string AssistantPreviewNote = "Unless this site turned them off (whoami: allowPreviewLinks), assistantPreviewUrl is a short-lived link to the same preview that you may fetch yourself, without a sign-in.";

    /// <summary>
    /// <paramref name="result"/> as JSON, with <see cref="Public"/>, <see cref="Edit"/> and (with
    /// <paramref name="preview"/>) <see cref="Preview"/> added when the content has them.
    /// </summary>
    /// <param name="wholeContent">Link to the content rather than the version the result names (a discarded version is gone).</param>
    /// <param name="preview">The result names a version that was saved: link to its preview, unless it is published.</param>
    public static string Serialize<T>(T result, ContentSummary? content, HttpContext context, bool wholeContent = false, bool preview = false)
    {
        var json = JsonSerializer.SerializeToNode(result, AgentJson.Options)!.AsObject();
        if (PublicUrl(content, context) is { } url)
        {
            json[Public] = url;
        }
        var editUrl = EditUrl(content, context, wholeContent);
        if (editUrl is not null)
        {
            json[Edit] = editUrl;
        }
        if (preview && !wholeContent && content is not null && PreviewUrl(content.Ref, content.Language, content.Status, context, editUrl) is { } previewUrl)
        {
            json[Preview] = previewUrl;
            AddAssistantPreview(json, content.Ref, content.Language, context);
        }
        // A save into a project (WriteResult.Project): where the editor finds the project.
        if (json["project"] is JsonObject project && project["id"] is JsonValue id && id.TryGetValue<int>(out var projectId))
        {
            AddProjectLink(project, projectId, project["editorUi"]?.GetValue<string>(), context);
        }
        return json.ToJsonString(AgentJson.Options);
    }

    /// <summary>
    /// A project tool's result as JSON: where the project is in the edit UI (<see cref="ProjectUrl"/>, or
    /// <see cref="ProjectNote"/>), and for each item its <see cref="Edit"/> URL, plus its <see cref="Public"/> one when the
    /// project's publish put it live.
    /// </summary>
    public static string SerializeProject(ProjectResult result, HttpContext context)
    {
        var json = JsonSerializer.SerializeToNode(result, AgentJson.Options)!.AsObject();
        AddProjectLink(json, result.Project.Id, result.EditorUi, context);
        foreach (var list in new[] { "items", "published", "scheduled", "failed", "notReady", "alreadyPublished" })
        {
            AddItemUrls(json[list] as JsonArray, context, publicUrl: list == "published" && !result.DryRun);
        }
        return json.ToJsonString(AgentJson.Options);
    }

    /// <inheritdoc cref="SerializeProject(ProjectResult, HttpContext)"/>
    public static string SerializeProject(ProjectDetails details, HttpContext context)
    {
        var json = JsonSerializer.SerializeToNode(details, AgentJson.Options)!.AsObject();
        AddProjectLink(json, details.Project.Id, details.EditorUi, context);
        AddItemUrls(json["items"] as JsonArray, context, publicUrl: false);
        return json.ToJsonString(AgentJson.Options);
    }

    /// <summary><c>list_projects</c>' result: each project with its <see cref="ProjectUrl"/>, or one <see cref="ProjectNote"/> for all.</summary>
    public static string SerializeProjects(ProjectList list, HttpContext context)
    {
        var json = JsonSerializer.SerializeToNode(list, AgentJson.Options)!.AsObject();
        if (list.EditorUi == ProjectEditorUi.Gadget)
        {
            json[ProjectNote] = GadgetNote;
        }
        else if (json["projects"] is JsonArray projects)
        {
            foreach (var project in projects.OfType<JsonObject>())
            {
                if (project["id"] is JsonValue id && id.TryGetValue<int>(out var projectId))
                {
                    AddProjectLink(project, projectId, list.EditorUi, context);
                }
            }
        }
        return json.ToJsonString(AgentJson.Options);
    }

    /// <summary>
    /// The edit UI's link to project <paramref name="id"/>, where project mode is on (<see cref="ProjectEditorUi.ProjectMode"/>):
    /// <c>&lt;edit UI root&gt;#context=epi.cms.project:///&lt;id&gt;</c>, the root as the CMS's own <see cref="EditUrlResolver"/>
    /// gives it for <see cref="Edit"/> URLs, on the site's edit host. It opens the project overview, and makes the project
    /// the editor's current one in the edit UI. Null elsewhere: with only the gadget the link opens an empty overview.
    /// </summary>
    public static string? ProjectLink(int id, string? editorUi, HttpContext context)
    {
        return id <= 0 || editorUi != ProjectEditorUi.ProjectMode || EditUrl(ContentReference.RootPage, null, context) is not { } edit
            ? null
            : ProjectLinkOn(edit, id);
    }

    /// <summary>The link to project <paramref name="id"/> in the edit UI that <paramref name="editUrl"/> (any content's edit URL) opens.</summary>
    internal static string ProjectLinkOn(string editUrl, int id)
    {
        var root = editUrl[..(editUrl.IndexOfAny(['?', '#']) is var cut and >= 0 ? cut : editUrl.Length)];
        return string.Create(CultureInfo.InvariantCulture, $"{root}#context=epi.cms.project:///{id}");
    }

    /// <summary>Adds <see cref="ProjectUrl"/> to <paramref name="json"/> where project mode is on, <see cref="ProjectNote"/> where only the gadget is.</summary>
    private static void AddProjectLink(JsonObject json, int id, string? editorUi, HttpContext context)
    {
        if (ProjectLink(id, editorUi, context) is { } url)
        {
            json[ProjectUrl] = url;
        }
        else if (editorUi == ProjectEditorUi.Gadget && id > 0)
        {
            json[ProjectNote] = GadgetNote;
        }
    }

    /// <summary>Adds each item's <see cref="Edit"/> URL (and <see cref="Public"/>, with <paramref name="publicUrl"/>), from its <c>content</c>.</summary>
    private static void AddItemUrls(JsonArray? items, HttpContext context, bool publicUrl)
    {
        foreach (var item in items?.OfType<JsonObject>() ?? [])
        {
            // A version that no longer exists has no name or GUID, and nothing to open.
            if (item["content"]?.Deserialize<ContentSummary>(AgentJson.Options) is not { Guid: var guid } content || guid == Guid.Empty)
            {
                continue;
            }
            if (publicUrl && PublicUrl(content, context) is { } url)
            {
                item[Public] = url;
            }
            if (EditUrl(content, context) is { } editUrl)
            {
                item[Edit] = editUrl;
            }
        }
    }

    /// <summary><c>get_content</c>'s result, with <see cref="Preview"/> added when the version it read isn't published.</summary>
    /// <param name="projects">The projects that hold a version of the content, added as <c>projects</c>; none when null.</param>
    public static string Serialize(ContentItem item, HttpContext context, IReadOnlyList<ContentProject>? projects = null)
    {
        var json = JsonSerializer.SerializeToNode(item, AgentJson.Options)!.AsObject();
        if (projects is { Count: > 0 })
        {
            json["projects"] = JsonSerializer.SerializeToNode(projects, AgentJson.Options);
        }
        // The item's ref names the content; the version it read is its version.
        var version = ContentReference.TryParse(item.Ref, out var link) && item.Version is { } id
            ? new ContentReference(link.ID, id, link.ProviderName).ToString()
            : null;
        if (item.Kind is "page" or "block" && PreviewUrl(version, item.Language, item.Status, context) is { } previewUrl)
        {
            json[Preview] = previewUrl;
            AddAssistantPreview(json, version!, item.Language, context);
        }
        return json.ToJsonString(AgentJson.Options);
    }

    /// <returns>Null for no content (a dry-run create), or content without a URL (a block, a folder).</returns>
    public static string? PublicUrl(ContentSummary? content, HttpContext context) =>
        content is null || !ContentReference.TryParse(content.Ref, out var link) || context.RequestServices.GetService<IUrlResolver>() is not { } urls
            ? null
            : PublicUrls.Of(urls, link, content.Language, absolute: true);

    /// <returns>Null for no content (a dry-run create), or when the site has no CMS edit UI.</returns>
    public static string? EditUrl(ContentSummary? content, HttpContext context, bool wholeContent = false) =>
        content is null || !ContentReference.TryParse(content.Ref, out var link)
            ? null
            : EditUrl(wholeContent ? link.ToReferenceWithoutVersion() : link, content.Language, context);

    private static string? EditUrl(ContentReference link, string? language, HttpContext context)
    {
        if (context.RequestServices.GetService<EditUrlResolver>() is not { } resolver)
        {
            return null;
        }
        var arguments = new EditUrlArguments { ForceEditHost = true };
        if (!string.IsNullOrEmpty(language))
        {
            arguments.Language = CultureInfo.GetCultureInfo(language);
        }
        return Absolute(resolver.GetEditViewUrl(link, arguments)?.ToString(), RequestOrigin(context));
    }

    /// <summary>The CMS's preview of the version <paramref name="reference"/> names (<see cref="ResultUrls"/>' remarks).</summary>
    /// <param name="status">The version's status (<see cref="ContentSummary.Status"/>): none for a published one.</param>
    /// <param name="editUrl">The result's edit URL, whose host the preview is on; worked out when not given.</param>
    /// <returns>Null when the version shouldn't or can't be previewed, or the site has no preview URL for it.</returns>
    internal static string? PreviewUrl(string? reference, string? language, string? status, HttpContext context, string? editUrl = null)
    {
        if (!Previews(reference, status) || !ContentReference.TryParse(reference, out var link)
            || context.RequestServices.GetService<IUrlResolver>() is not { } urls
            || context.RequestServices.GetService<IContentLoader>() is not { } loader)
        {
            return null;
        }
        // The version itself, in its language: a page or a block (which the edit UI previews with the site's preview
        // template), not media or a folder.
        var found = string.IsNullOrEmpty(language)
            ? loader.TryGet<IContent>(link, out var content)
            : loader.TryGet(link, CultureInfo.GetCultureInfo(language), out content);
        if (!found || content is not (PageData or BlockData))
        {
            return null;
        }
        var url = urls.GetUrl(link, language, new UrlResolverArguments { ContextMode = ContextMode.Preview });
        return Absolute(url, Origin(editUrl ?? EditUrl(link, language, context)) ?? RequestOrigin(context));
    }

    /// <summary>
    /// Adds the assistant's own link to the version (<see cref="AssistantPreview"/>, <see cref="PreviewLinks"/>) and when
    /// it expires (<see cref="AssistantPreviewExpires"/>, ISO 8601 UTC), beside its <see cref="Preview"/>, when the site
    /// allows preview links (the default): for the editor and connection of the MCP request.
    /// </summary>
    private static void AddAssistantPreview(JsonObject json, string version, string? language, HttpContext context)
    {
        if (context.RequestServices.GetService<IOptions<OptiCliMcpOptions>>()?.Value is not { AllowPreviewLinks: true }
            || context.RequestServices.GetService<PreviewLinks>() is not { } links
            || McpEditor.Of(context) is not { GrantId.Length: > 0, Name.Length: > 0 } editor)
        {
            return;
        }
        var (url, expires) = links.Issue(context.Request, editor.GrantId, editor.Name, version, language);
        json[AssistantPreview] = url;
        json[AssistantPreviewExpires] = expires.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whether a version gets a preview link: one that is saved (a version ref, <c>123_456</c>) and not the one visitors
    /// see, which <see cref="Public"/> shows.
    /// </summary>
    internal static bool Previews(string? reference, string? status) =>
        ContentReference.TryParse(reference, out var link) && link.WorkID > 0
        && !string.Equals(status, "published", StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="url"/> as it is when absolute (http or https), otherwise on <paramref name="origin"/>.</summary>
    /// <returns>Null for no URL.</returns>
    internal static string? Absolute(string? url, string origin)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
            ? url
            : $"{origin}{(url.StartsWith('/') ? "" : "/")}{url}";
    }

    /// <summary>The scheme and host of an absolute URL; null for a relative one or none.</summary>
    internal static string? Origin(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" ? absolute.GetLeftPart(UriPartial.Authority) : null;

    /// <summary>The MCP request's origin: the host the editor connected the assistant to.</summary>
    private static string RequestOrigin(HttpContext context) => $"{context.Request.Scheme}://{context.Request.Host}";
}
