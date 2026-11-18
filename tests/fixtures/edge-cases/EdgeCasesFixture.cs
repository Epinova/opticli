// Copied into an Alloy site by setup.sh, for opticli's integration tests. It adds the content types the edge-case
// plan (edge-cases.plan.json) needs, and at startup sets up what a plan can't create: a visitor group, a second site
// whose start page is under the first site's, three sites for the site host tests, an approval sequence, language
// settings, and two CMS projects: "Edge project" (the approval root's draft) and "Edge campaign" (two pages of its own,
// one draft marked Ready to publish and one not). Each is made once, and only once the plan's content exists, so setup.sh restarts the site after applying
// the plan. The sites are made by Cms12Fixture.cs on CMS 12; on CMS 13, Cms13Fixture.cs makes the three hosts sites as
// applications (with start pages of their own below the root) and no nested site, which CMS 13 can't have.
// POST /opticli-fixture/hand-over (loopback only) saves a version again as another user, for the tests of someone else's
// draft (opticli's agent only saves as itself). GET /opticli-fixture/inline-blocks says how the edit UI makes a new block
// in a ContentArea (UIOptions.InlineBlocksInContentAreaEnabled), and POST /opticli-fixture/inline-block adds an inline
// block to a ContentArea through the CMS's API, as another tool may where the edit UI makes none (both loopback only).
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Security.Principal;
using EPiServer.Approvals;
using EPiServer.Approvals.ContentApprovals;
using EPiServer.Data.Entity;
using EPiServer.DataAccess;
using EPiServer.Framework;
using EPiServer.Framework.DataAnnotations;
using EPiServer.Framework.Initialization;
using EPiServer.Personalization.VisitorGroups;
using EPiServer.Security;
using EPiServer.ServiceLocation;
using EPiServer.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace OptiCliEdgeCases;

/// <summary>A page with a local block, so a culture-specific property inside a local block exists.</summary>
[ContentType(GUID = "3B0C4A5E-0D1F-4E5A-9C7B-6A1D2E3F4A01", DisplayName = "Edge page", Description = "opticli edge-case fixture")]
[AvailableContentTypes(Availability.All, IncludeOn = [typeof(Alloy.Models.Pages.StandardPage)])]
public class EdgePage : PageData
{
    [CultureSpecific]
    [Display(Order = 10)]
    public virtual XhtmlString MainBody { get; set; }

    [Display(Order = 20)]
    public virtual ContentArea MainContentArea { get; set; }

    /// <summary>Not culture-specific itself; its Title is.</summary>
    [Display(Order = 30)]
    public virtual EdgeLocalBlock Local { get; set; }
}

[ContentType(GUID = "3B0C4A5E-0D1F-4E5A-9C7B-6A1D2E3F4A02", DisplayName = "Edge local block", AvailableInEditMode = false)]
public class EdgeLocalBlock : BlockData
{
    [CultureSpecific]
    public virtual string Title { get; set; }

    public virtual string Code { get; set; }
}

/// <summary>A block with a ContentArea of its own, for inline blocks inside an inline block.</summary>
[ContentType(GUID = "3B0C4A5E-0D1F-4E5A-9C7B-6A1D2E3F4A04", DisplayName = "Edge container block", Description = "opticli edge-case fixture")]
public class EdgeContainerBlock : BlockData
{
    [Display(Order = 10)]
    public virtual string Heading { get; set; }

    [Display(Order = 20)]
    public virtual ContentArea Items { get; set; }
}

/// <summary>Alloy has no media type for PDF files, which the upload tests use.</summary>
[ContentType(GUID = "3B0C4A5E-0D1F-4E5A-9C7B-6A1D2E3F4A03", DisplayName = "Edge document")]
[MediaDescriptor(ExtensionString = "pdf")]
public class EdgeDocument : MediaData
{
    public virtual string Description { get; set; }
}

[InitializableModule]
[ModuleDependency(typeof(EPiServer.Web.InitializationModule))]
public partial class EdgeCasesSetup : IInitializableModule
{
    /// <summary>The GUIDs the plan gives these items ("guid" on their steps).</summary>
    public static readonly Guid NestedStart = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e01");
    public static readonly Guid ApprovalRoot = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e02");
    public static readonly Guid LanguageRoot = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e03");
    public static readonly Guid VisitorGroup = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e10");
    public static readonly Guid HostsStartA = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e04");
    public static readonly Guid HostsStartB = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e05");
    public static readonly Guid HostsStartC = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e06");

    /// <summary>The "Edge campaign" project's pages: a draft marked Ready to publish, and a draft that isn't.</summary>
    public static readonly Guid CampaignReady = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e07");
    public static readonly Guid CampaignDraft = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e08");

    public const string NestedSiteName = "Edge nested site";
    public const string NestedHost = "edge-nested.localhost";

    /// <summary>
    /// Two sites whose hosts the site host tests change, in a shape `opticli sites primary` and `sites host` can restore
    /// exactly: an https SiteUrl on the primary host, a language host, and the Edit host last.
    /// </summary>
    public const string HostsSiteA = "Edge hosts A";
    public const string HostsSiteB = "Edge hosts B";

    /// <summary>A site whose hosts are all bound to one language (English), with no primary host for every language.</summary>
    public const string HostsSiteC = "Edge hosts C";

    public void Initialize(InitializationEngine context)
    {
        var locate = context.Locate.Advanced;
        try
        {
            EnsureVisitorGroup(locate.GetInstance<IVisitorGroupRepository>());
            var content = locate.GetInstance<IContentRepository>();
            EnsureSites(locate, content);
            if (Find(content, ApprovalRoot) is { } approval)
            {
                EnsureApproval(locate.GetInstance<IApprovalDefinitionRepository>(), approval);
                EnsureProject(locate.GetInstance<ProjectRepository>(), content, approval);
                EnsureCampaign(locate.GetInstance<ProjectRepository>(), content, locate.GetInstance<IContentTypeRepository>(), content.Get<IContent>(approval).ParentLink);
            }
            if (Find(content, LanguageRoot) is { } languages)
            {
                EnsureLanguageSettings(locate.GetInstance<ContentLanguageSettingRepository>(), languages);
            }
        }
        catch (Exception ex)
        {
            // The site should still start; setup.sh reads this line from the log.
            Console.Error.WriteLine($"[edge-cases] setup failed: {ex}");
        }
    }

    public void Uninitialize(InitializationEngine context)
    {
    }

    /// <summary>The nested site and the three hosts sites as site definitions (CMS 12); the hosts sites as applications (CMS 13).</summary>
    static partial void EnsureSites(IServiceProvider locate, IContentRepository content);

    private static ContentReference Find(IContentRepository content, Guid guid) =>
        content.TryGet<IContent>(guid, out var found) && !found.IsDeleted ? found.ContentLink.ToReferenceWithoutVersion() : null;

    private static void EnsureVisitorGroup(IVisitorGroupRepository groups)
    {
        if (groups.Load(VisitorGroup) is not null)
        {
            return;
        }
        // No criteria: nobody matches, which is all a fixture needs.
        groups.Save(new VisitorGroup { Id = VisitorGroup, Name = "Edge visitors", Notes = "opticli edge-case fixture" });
        Console.Error.WriteLine("[edge-cases] created visitor group 'Edge visitors'");
    }

    private static void EnsureApproval(IApprovalDefinitionRepository approvals, ContentReference root)
    {
        if (approvals.GetAsync(root).GetAwaiter().GetResult() is not null)
        {
            return;
        }
        approvals.SaveAsync(new ContentApprovalDefinition
        {
            ContentLink = root,
            IsEnabled = true,
            Steps =
            [
                new ApprovalDefinitionStep("Review", [new ApprovalDefinitionReviewer("WebAdmins", [CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("sv")], ApprovalDefinitionReviewerType.Role)]),
            ],
        }).GetAwaiter().GetResult();
        Console.Error.WriteLine($"[edge-cases] created an approval sequence on {root}");
    }

    /// <summary>A project with the approval root's latest version, which was never published.</summary>
    private static void EnsureProject(ProjectRepository projects, IContentRepository content, ContentReference item)
    {
        if (projects.List().Any(p => p.Name == ProjectName))
        {
            return;
        }
        var project = new Project { Name = ProjectName };
        projects.Save(project);
        var latest = content.Get<IContent>(locateLatest(content, item));
        projects.SaveItems([new ProjectItem(project.ID, latest)]);
        Console.Error.WriteLine($"[edge-cases] created project '{ProjectName}' with {latest.ContentLink}");

        static ContentReference locateLatest(IContentRepository repository, ContentReference link) =>
            ServiceLocator.Current.GetInstance<IContentVersionRepository>().List(link).OrderByDescending(v => v.ContentLink.WorkID).First().ContentLink;
    }

    public const string ProjectName = "Edge project";

    public const string CampaignName = "Edge campaign";

    /// <summary>
    /// A project with ready and not-ready items, for reads: two published pages below the edge-case root (made once, by
    /// their GUIDs), each with a newer draft in the project; the first draft is marked Ready to publish (checked in, as the
    /// edit UI's Ready to Publish does), the second stays a draft.
    /// </summary>
    private static void EnsureCampaign(ProjectRepository projects, IContentRepository content, IContentTypeRepository types, ContentReference parent)
    {
        if (projects.List().Any(p => p.Name == CampaignName))
        {
            return;
        }
        var ready = Draft(CampaignReady, "Campaign page, ready", "<p>Marked Ready to publish in the Edge campaign project.</p>");
        ready = content.Get<IContent>(content.Save(Writable(ready), SaveAction.CheckIn, AccessLevel.NoAccess));
        var draft = Draft(CampaignDraft, "Campaign page, draft", "<p>Still a draft in the Edge campaign project.</p>");
        var project = new Project { Name = CampaignName };
        projects.Save(project);
        projects.SaveItems([new ProjectItem(project.ID, ready) { Category = "default" }, new ProjectItem(project.ID, draft) { Category = "default" }]);
        Console.Error.WriteLine($"[edge-cases] created project '{CampaignName}' with {ready.ContentLink} (ready) and {draft.ContentLink} (draft)");

        // The page (published once), then a new draft of it with the new text.
        IContent Draft(Guid guid, string name, string text)
        {
            if (!content.TryGet<PageData>(guid, out var page) || page.IsDeleted)
            {
                var made = content.GetDefault<PageData>(parent, types.Load("StandardPage").ID);
                made.ContentGuid = guid;
                made.Name = name;
                made["MainBody"] = new XhtmlString("<p>Published.</p>");
                page = content.Get<PageData>(content.Save(made, SaveAction.Publish, AccessLevel.NoAccess));
            }
            var writable = (PageData)page.CreateWritableClone();
            writable["MainBody"] = new XhtmlString(text);
            return content.Get<IContent>(content.Save(writable, SaveAction.CheckOut | SaveAction.ForceNewVersion, AccessLevel.NoAccess));
        }

        static IContent Writable(IContent version) => (IContent)((IReadOnly)version).CreateWritableClone();
    }

    private static void EnsureLanguageSettings(ContentLanguageSettingRepository settings, ContentReference root)
    {
        if (settings.Load(root).Any())
        {
            return;
        }
        // en as is; sv falls back to en, so pages without a Swedish branch still show in Swedish.
        settings.Save(new ContentLanguageSetting(root, "en", null, [], true));
        settings.Save(new ContentLanguageSetting(root, "sv", null, ["en"], true));
        Console.Error.WriteLine($"[edge-cases] created language settings on {root}");
    }
}

/// <summary>
/// <c>POST /opticli-fixture/hand-over?version=&lt;id_version&gt;&amp;user=&lt;name&gt;</c> from the machine itself: saves that
/// version again, unchanged and as the same version, as <c>user</c>, so it becomes that user's draft the way an editor's
/// save makes it (the CMS records the user and clears its cached version list). The tests of someone else's draft use
/// it; opticli's agent only saves as itself. With <c>&amp;ready=1</c> it marks the version Ready to publish as that user
/// instead, as the edit UI's "Ready to Publish" does (a check-in of the same version), for the tests of a project item a
/// colleague marked ready.
/// <para><c>POST /opticli-fixture/approval-comment?required=1</c> (or <c>0</c>), also from the machine itself, turns the
/// approval root's sequence's "require a comment" on (or off), for the tests of a review request that needs one; they turn
/// it off again.</para>
/// <para><c>GET /opticli-fixture/inline-blocks</c> answers <c>inline</c> or <c>shared</c>: how the site's edit UI makes a new
/// block in a ContentArea (<c>UIOptions.InlineBlocksInContentAreaEnabled</c>, which <c>serve</c>'s environment can set with
/// <c>EPiServer__CmsUI__UI__InlineBlocksInContentAreaEnabled</c>), or <c>unknown</c>. The tests of each mode skip on a site
/// in the other.</para>
/// <para><c>POST /opticli-fixture/inline-block?content=&lt;id&gt;&amp;property=&lt;area&gt;&amp;type=&lt;block type&gt;[&amp;name=&lt;name&gt;][&amp;lang=&lt;code&gt;]</c>
/// with a JSON object of values (strings, parsed as the CMS parses them) adds an inline block of that type at the end of the
/// area of the content's latest version, saved as a new draft through the CMS's API, as another tool may put one on a
/// site whose edit UI makes none (CMS 12.20+). Answers the new version (<c>123_456</c>).</para>
/// </summary>
[InitializableModule]
public class OptiCliHandOverFixture : IConfigurableModule
{
    public const string Path = "/opticli-fixture/hand-over";

    public const string CommentPath = "/opticli-fixture/approval-comment";

    public const string ModePath = "/opticli-fixture/inline-blocks";

    public const string InlinePath = "/opticli-fixture/inline-block";

    public void ConfigureContainer(ServiceConfigurationContext context) =>
        context.Services.AddTransient<IStartupFilter, Endpoint>();

    public void Initialize(InitializationEngine context)
    {
    }

    public void Uninitialize(InitializationEngine context)
    {
    }

    private sealed class Endpoint : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path.Equals(CommentPath, StringComparison.OrdinalIgnoreCase))
                {
                    await RequireComment(context);
                    return;
                }
                if (context.Request.Path.Equals(ModePath, StringComparison.OrdinalIgnoreCase) || context.Request.Path.Equals(InlinePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (context.Connection.RemoteIpAddress is not { } from || !IPAddress.IsLoopback(from))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }
                    await (context.Request.Path.Equals(ModePath, StringComparison.OrdinalIgnoreCase) ? Mode(context) : AddInline(context));
                    return;
                }
                if (!context.Request.Path.Equals(Path, StringComparison.OrdinalIgnoreCase))
                {
                    await nextMiddleware();
                    return;
                }
                if (!HttpMethods.IsPost(context.Request.Method) || context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)
                    || !ContentReference.TryParse(context.Request.Query["version"], out var version) || version.WorkID <= 0
                    || context.Request.Query["user"].ToString() is not { Length: > 0 } user)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                var repository = context.RequestServices.GetRequiredService<IContentRepository>();
                var accessor = context.RequestServices.GetRequiredService<IPrincipalAccessor>();
                var principal = accessor.Principal;
                accessor.Principal = new GenericPrincipal(new GenericIdentity(user), ["WebEditors"]);
                try
                {
                    var content = (IContent)((IReadOnly)repository.Get<IContent>(version)).CreateWritableClone();
                    if (context.Request.Query["ready"] == "1")
                    {
                        // What the edit UI's commit sends (ContentDataCommitter): the same version, checked in.
                        repository.Save(content, SaveAction.CheckIn | SaveAction.ForceCurrentVersion, AccessLevel.NoAccess);
                    }
                    else
                    {
                        // The CMS doesn't save an unchanged version's data; ChangedBy is what changes (to the saving user).
                        ((IChangeTrackable)content).ChangedBy = user;
                        repository.Save(content, SaveAction.CheckOut | SaveAction.ForceCurrentVersion, AccessLevel.NoAccess);
                    }
                }
                finally
                {
                    accessor.Principal = principal;
                }
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            });
            next(app);
        };

        /// <summary>How the edit UI makes a new block in a ContentArea, read as the CMS UI reads it (by reflection: the option exists from EPiServer.CMS.UI 12.2x).</summary>
        private static async Task Mode(HttpContext context)
        {
            var type = Type.GetType("EPiServer.Web.UIOptions, EPiServer.Shell", throwOnError: false);
            var option = type?.GetProperty("InlineBlocksInContentAreaEnabled");
            var options = type is null ? null : context.RequestServices.GetService(type);
            var inline = options is null || option is null ? null : option.GetValue(options) as bool?;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(inline switch { true => "inline", false => "shared", _ => "unknown" });
        }

        private static async Task AddInline(HttpContext context)
        {
            var query = context.Request.Query;
            var repository = context.RequestServices.GetRequiredService<IContentRepository>();
            var types = context.RequestServices.GetRequiredService<EPiServer.DataAbstraction.IContentTypeRepository>();
            var inlineProperty = typeof(ContentAreaItem).GetProperty("InlineBlock");
            if (!HttpMethods.IsPost(context.Request.Method) || inlineProperty is null
                || !int.TryParse(query["content"], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || types.Load(query["type"].ToString()) is not { } type
                || query["property"].ToString() is not { Length: > 0 } property)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var values = await System.Text.Json.JsonSerializer.DeserializeAsync<Dictionary<string, string>>(context.Request.Body) ?? [];
            var language = query["lang"].ToString() is { Length: > 0 } code ? CultureInfo.GetCultureInfo(code) : null;
            var versions = context.RequestServices.GetRequiredService<EPiServer.Core.IContentVersionRepository>();
            var content = language is null ? repository.Get<IContent>(new ContentReference(id)) : repository.Get<IContent>(new ContentReference(id), language);
            var latest = versions.List(content.ContentLink.ToReferenceWithoutVersion())
                .Where(v => language is null || v.LanguageBranch == language.Name)
                .Where(v => content is not ILocale { Language: { } own } || v.LanguageBranch == own.Name)
                .OrderByDescending(v => v.ContentLink.WorkID).First();
            var writable = (IContent)((IReadOnly)repository.Get<IContent>(latest.ContentLink)).CreateWritableClone();
            var factory = context.RequestServices.GetRequiredService<EPiServer.Construction.IContentDataFactory<BlockData>>();
            var builder = context.RequestServices.GetRequiredService<EPiServer.Construction.IContentDataBuilder>();
            var block = factory.CreateInstance(type);
            builder.AddProperties(block, new EPiServer.Construction.BuildingContext(type) { SetPropertyValues = true });
            typeof(ContentData).GetProperty("ContentTypeID")?.SetValue(block, type.ID);
            foreach (var (name, value) in values)
            {
                block.Property[name].ParseToSelf(value);
            }
            var item = new ContentAreaItem();
            inlineProperty.SetValue(item, block);
            if (query["name"].ToString() is { Length: > 0 } inlineName)
            {
                // CMS 12: IDictionary<string, object>, null until set; CMS 13: IDictionary<string, string>, made on first read.
                var settings = typeof(ContentAreaItem).GetProperty("RenderSettings")!;
                var current = settings.GetValue(item);
                if (current is null)
                {
                    current = new Dictionary<string, object>();
                    settings.SetValue(item, current);
                }
                switch (current)
                {
                    case IDictionary<string, string> strings:
                        strings["data-inlineblockname"] = inlineName;
                        break;
                    case IDictionary<string, object> objects:
                        objects["data-inlineblockname"] = inlineName;
                        break;
                }
            }
            var area = writable.Property[property].Value as ContentArea is { } existing ? existing.CreateWritableClone() : new ContentArea();
            area.Items.Add(item);
            writable.Property[property].Value = area;
            var saved = repository.Save(writable, SaveAction.Save | SaveAction.ForceNewVersion, AccessLevel.NoAccess);
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(saved.ToString());
        }

        private static async Task RequireComment(HttpContext context)
        {
            var required = context.Request.Query["required"].ToString();
            var repository = context.RequestServices.GetRequiredService<IContentRepository>();
            var approvals = context.RequestServices.GetRequiredService<IApprovalDefinitionRepository>();
            if (!HttpMethods.IsPost(context.Request.Method) || context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)
                || required is not ("0" or "1")
                || !repository.TryGet<IContent>(EdgeCasesSetup.ApprovalRoot, out var root)
                || await approvals.GetAsync(root.ContentLink.ToReferenceWithoutVersion()) is not { } definition)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var writable = definition.CreateWritableClone();
            writable.RequireCommentOnStart = required == "1";
            await approvals.SaveAsync(writable);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
    }
}
