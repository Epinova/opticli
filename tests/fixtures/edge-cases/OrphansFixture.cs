// Copied into the edge-case site by setup.sh, for opticli's `types --orphaned` and `type` integration tests: what the
// CMS leaves in the database after code is removed. A content type whose class is gone, kept because a page uses it,
// and a property of EdgePage that isn't in its class any more, kept because it has a value. Made through the CMS's
// repositories once the plan's language root exists, so setup.sh's second start makes them.
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.Security;
using EPiServer.ServiceLocation;
using EPiServer.SpecializedProperties;

namespace OptiCliEdgeCases;

[InitializableModule]
[ModuleDependency(typeof(EdgeCasesSetup))]
public class OptiCliOrphansFixture : IInitializableModule
{
    /// <summary>A page type whose class (<see cref="RemovedClass"/>) doesn't exist.</summary>
    public static readonly Guid RemovedType = Guid.Parse("3B0C4A5E-0D1F-4E5A-9C7B-6A1D2E3F4A10");

    public const string RemovedTypeName = "EdgeRemovedPage";

    public const string RemovedClass = "OptiCliEdgeCases.EdgeRemovedPage, Alloy";

    /// <summary>A String property on <see cref="EdgePage"/> that its class doesn't declare.</summary>
    public const string RemovedProperty = "EdgeRemovedText";

    public static readonly Guid RemovedTypePage = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e20");

    public static readonly Guid RemovedPropertyPage = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e21");

    public void Initialize(InitializationEngine context)
    {
        var locate = context.Locate.Advanced;
        try
        {
            var content = locate.GetInstance<IContentRepository>();
            if (!content.TryGet<PageData>(EdgeCasesSetup.LanguageRoot, out var root))
            {
                return;
            }
            var types = locate.GetInstance<IContentTypeRepository>();
            if (types.Load(RemovedType) is null)
            {
                types.Save(new PageType
                {
                    GUID = RemovedType,
                    Name = RemovedTypeName,
                    DisplayName = "Edge removed page",
                    Description = "opticli edge-case fixture: its class was removed from the code",
                    ModelTypeString = RemovedClass,
                });
            }
            if (!content.TryGet<IContent>(RemovedTypePage, out _))
            {
                var page = content.GetDefault<PageData>(root.ContentLink, types.Load(RemovedType)!.ID);
                page.Name = "Edge removed-type page";
                page.ContentGuid = RemovedTypePage;
                content.Save(page, SaveAction.Publish, AccessLevel.NoAccess);
            }

            var edgePage = types.Load<EdgePage>();
            if (edgePage.PropertyDefinitions.All(p => p.Name != RemovedProperty))
            {
                locate.GetInstance<IPropertyDefinitionRepository>().Save(new PropertyDefinition
                {
                    ContentTypeID = edgePage.ID,
                    Name = RemovedProperty,
                    EditCaption = "Removed text",
                    Type = locate.GetInstance<IPropertyDefinitionTypeRepository>().Load(typeof(PropertyString)),
                    ExistsOnModel = false,
                });
            }
            if (!content.TryGet<IContent>(RemovedPropertyPage, out _))
            {
                var page = content.GetDefault<EdgePage>(root.ContentLink);
                page.Name = "Edge removed-property page";
                page.ContentGuid = RemovedPropertyPage;
                page.Property[RemovedProperty].Value = "Kept after its property was removed from the code";
                content.Save(page, SaveAction.Publish, AccessLevel.NoAccess);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[edge-cases] setup failed: {ex}");
        }
    }

    public void Uninitialize(InitializationEngine context)
    {
    }
}
