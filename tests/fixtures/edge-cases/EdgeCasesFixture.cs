// Copied into an Alloy site by setup.sh, for opticli's integration tests. It adds the content types the edge-case
// plan (edge-cases.plan.json) needs, and at startup sets up what a plan can't create: a visitor group, a second site
// whose start page is under the first site's, an approval sequence and language settings. Each is made once, and only
// once the plan's content exists, so setup.sh restarts the site after applying the plan.
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using EPiServer.Approvals;
using EPiServer.Approvals.ContentApprovals;
using EPiServer.Framework;
using EPiServer.Framework.DataAnnotations;
using EPiServer.Framework.Initialization;
using EPiServer.Personalization.VisitorGroups;
using EPiServer.ServiceLocation;
using EPiServer.Web;

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

/// <summary>Alloy has no media type for PDF files, which the upload tests use.</summary>
[ContentType(GUID = "3B0C4A5E-0D1F-4E5A-9C7B-6A1D2E3F4A03", DisplayName = "Edge document")]
[MediaDescriptor(ExtensionString = "pdf")]
public class EdgeDocument : MediaData
{
    public virtual string Description { get; set; }
}

[InitializableModule]
[ModuleDependency(typeof(EPiServer.Web.InitializationModule))]
public class EdgeCasesSetup : IInitializableModule
{
    /// <summary>The GUIDs the plan gives these items ("guid" on their steps).</summary>
    public static readonly Guid NestedStart = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e01");
    public static readonly Guid ApprovalRoot = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e02");
    public static readonly Guid LanguageRoot = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e03");
    public static readonly Guid VisitorGroup = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e10");

    public const string NestedSiteName = "Edge nested site";
    public const string NestedHost = "edge-nested.localhost";

    public void Initialize(InitializationEngine context)
    {
        var locate = context.Locate.Advanced;
        try
        {
            EnsureVisitorGroup(locate.GetInstance<IVisitorGroupRepository>());
            var content = locate.GetInstance<IContentRepository>();
            if (Find(content, NestedStart) is { } nested)
            {
                EnsureNestedSite(locate.GetInstance<ISiteDefinitionRepository>(), nested);
            }
            if (Find(content, ApprovalRoot) is { } approval)
            {
                EnsureApproval(locate.GetInstance<IApprovalDefinitionRepository>(), approval);
                EnsureProject(locate.GetInstance<ProjectRepository>(), content, approval);
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

    private static void EnsureNestedSite(ISiteDefinitionRepository sites, ContentReference start)
    {
        if (sites.List().Any(s => s.Name == NestedSiteName))
        {
            return;
        }
        var site = new SiteDefinition
        {
            Name = NestedSiteName,
            StartPage = start,
            SiteUrl = new Uri($"http://{NestedHost}/"),
            Hosts = [new HostDefinition { Name = NestedHost }],
        };
        sites.Save(site);
        Console.Error.WriteLine($"[edge-cases] created site '{NestedSiteName}' with start page {start}");
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
