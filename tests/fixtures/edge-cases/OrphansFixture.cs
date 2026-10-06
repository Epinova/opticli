// Copied into the edge-case site by setup.sh, for opticli's `types --orphaned`, `type`, `types remove`,
// `types remove-property` and `types prune` integration tests: what the CMS leaves in the database after code is removed.
// - EdgeRemovedPage: a page type whose class is gone, kept because a page uses it.
// - EdgeTrashedPage: the same, with its only page in the recycle bin.
// - EdgeRemovedEmptyPage: a page type whose class is gone and that nothing uses, with a property Teaser whose block type
//   is EdgeRemovedBlock, a block type whose class is gone too. The CMS removes such an empty type when the site starts,
//   so this module makes it again on every start.
// - EdgeRemovedText (one value) and EdgeRemovedEmptyText (none): properties of EdgePage that its class doesn't declare.
// - EdgeAdminPage: a page type made in admin mode (no class on record), which isn't an orphan.
// Made through the CMS's repositories once the plan's language root exists, so setup.sh's second start makes them. The
// removal tests make them again by posting to /opticli-fixture/orphans (loopback only), which runs the same code.
using System.Net;
using System.Security.Principal;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.Security;
using EPiServer.ServiceLocation;
using EPiServer.SpecializedProperties;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace OptiCliEdgeCases;

[InitializableModule]
[ModuleDependency(typeof(EdgeCasesSetup))]
public class OptiCliOrphansFixture : IConfigurableModule
{
    /// <summary>A page type whose class (<see cref="RemovedClass"/>) doesn't exist.</summary>
    public static readonly Guid RemovedType = Guid.Parse("3B0C4A5E-0D1F-4E5A-9C7B-6A1D2E3F4A10");

    public const string RemovedTypeName = "EdgeRemovedPage";

    public const string RemovedClass = "OptiCliEdgeCases.EdgeRemovedPage, Alloy";

    /// <summary>A page type whose class is gone, whose only page is in the recycle bin.</summary>
    public static readonly Guid TrashedType = Guid.Parse("3B0C4A5E-0D1F-4E5A-9C7B-6A1D2E3F4A11");

    /// <summary>A page type whose class is gone and that no content uses.</summary>
    public static readonly Guid EmptyType = Guid.Parse("3B0C4A5E-0D1F-4E5A-9C7B-6A1D2E3F4A12");

    /// <summary>A block type whose class is gone, the type of <see cref="EmptyType"/>'s Teaser property.</summary>
    public static readonly Guid RemovedBlock = Guid.Parse("3B0C4A5E-0D1F-4E5A-9C7B-6A1D2E3F4A13");

    /// <summary>A page type made in admin mode.</summary>
    public static readonly Guid AdminType = Guid.Parse("3B0C4A5E-0D1F-4E5A-9C7B-6A1D2E3F4A14");

    /// <summary>A String property on <see cref="EdgePage"/> that its class doesn't declare, with a value.</summary>
    public const string RemovedProperty = "EdgeRemovedText";

    /// <summary>A String property on <see cref="EdgePage"/> that its class doesn't declare, without values.</summary>
    public const string EmptyProperty = "EdgeRemovedEmptyText";

    public const string RemovedPropertyValue = "Kept after its property was removed from the code";

    public static readonly Guid RemovedTypePage = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e20");

    public static readonly Guid RemovedPropertyPage = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e21");

    public static readonly Guid TrashedTypePage = Guid.Parse("6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e22");

    public const string ReseedPath = "/opticli-fixture/orphans";

    public void ConfigureContainer(ServiceConfigurationContext context) =>
        context.Services.AddTransient<IStartupFilter, ReseedEndpoint>();

    public void Initialize(InitializationEngine context)
    {
        // The removal tests' records go to the site's own App_Data, not the developer's real record file.
        Environment.SetEnvironmentVariable("OPTICLI_REMOVALS_FILE",
            Path.Combine(context.Locate.Advanced.GetInstance<IWebHostEnvironment>().ContentRootPath, "App_Data", "opticli-removals.jsonl"));
        try
        {
            Seed(context.Locate.Advanced);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[edge-cases] setup failed: {ex}");
        }
    }

    public void Uninitialize(InitializationEngine context)
    {
    }

    /// <summary>Makes whatever of the fixture is missing; changes nothing that is there.</summary>
    public static void Seed(IServiceProvider locate)
    {
        var content = locate.GetInstance<IContentRepository>();
        if (!content.TryGet<PageData>(EdgeCasesSetup.LanguageRoot, out var root))
        {
            return;
        }
        var types = locate.GetInstance<IContentTypeRepository>();
        var properties = locate.GetInstance<IPropertyDefinitionRepository>();
        var propertyTypes = locate.GetInstance<IPropertyDefinitionTypeRepository>();

        var removed = Type(types, new PageType { GUID = RemovedType, Name = RemovedTypeName, DisplayName = "Edge removed page", ModelTypeString = RemovedClass });
        Page(content, root, removed, RemovedTypePage, "Edge removed-type page");

        var trashed = Type(types, new PageType { GUID = TrashedType, Name = "EdgeTrashedPage", DisplayName = "Edge trashed page", ModelTypeString = "OptiCliEdgeCases.EdgeTrashedPage, Alloy" });
        var trashedPage = Page(content, root, trashed, TrashedTypePage, "Edge trashed-type page");
        if (!content.Get<PageData>(trashedPage).IsDeleted)
        {
            // Moving to the recycle bin checks Delete access, which no one has during startup.
            var accessor = locate.GetInstance<IPrincipalAccessor>();
            var principal = accessor.Principal;
            accessor.Principal = new GenericPrincipal(new GenericIdentity("opticli-fixture"), ["Administrators", "WebAdmins"]);
            try
            {
                content.MoveToWastebasket(trashedPage, "opticli-fixture");
            }
            finally
            {
                accessor.Principal = principal;
            }
        }

        var block = Type(types, new BlockType { GUID = RemovedBlock, Name = "EdgeRemovedBlock", DisplayName = "Edge removed block", ModelTypeString = "OptiCliEdgeCases.EdgeRemovedBlock, Alloy" });
        var empty = Type(types, new PageType { GUID = EmptyType, Name = "EdgeRemovedEmptyPage", DisplayName = "Edge removed empty page", ModelTypeString = "OptiCliEdgeCases.EdgeRemovedEmptyPage, Alloy" });
        if (empty.PropertyDefinitions.All(p => p.Name != "Teaser"))
        {
            properties.Save(new PropertyDefinition
            {
                ContentTypeID = empty.ID,
                Name = "Teaser",
                EditCaption = "Teaser",
                Type = propertyTypes.LoadByBlockType(block.GUID),
                ExistsOnModel = true,
            });
        }

        Type(types, new PageType { GUID = AdminType, Name = "EdgeAdminPage", DisplayName = "Edge admin-mode page", Description = "opticli edge-case fixture: made in admin mode" });

        var edgePage = types.Load<EdgePage>();
        foreach (var name in new[] { RemovedProperty, EmptyProperty })
        {
            if (edgePage.PropertyDefinitions.All(p => p.Name != name))
            {
                properties.Save(new PropertyDefinition
                {
                    ContentTypeID = edgePage.ID,
                    Name = name,
                    EditCaption = name,
                    Type = propertyTypes.Load(typeof(PropertyString)),
                    ExistsOnModel = false,
                });
            }
        }
        if (!content.TryGet<EdgePage>(RemovedPropertyPage, out var valuePage))
        {
            var page = content.GetDefault<EdgePage>(root.ContentLink);
            page.Name = "Edge removed-property page";
            page.ContentGuid = RemovedPropertyPage;
            page.Property[RemovedProperty].Value = RemovedPropertyValue;
            content.Save(page, SaveAction.Publish, AccessLevel.NoAccess);
        }
        else if (valuePage.Property[RemovedProperty]?.Value is null)
        {
            // The property was removed (and made again above): its value went with it.
            var page = (EdgePage)valuePage.CreateWritableClone();
            page.Property[RemovedProperty].Value = RemovedPropertyValue;
            content.Save(page, SaveAction.Publish, AccessLevel.NoAccess);
        }
    }

    private static ContentType Type(IContentTypeRepository types, ContentType type)
    {
        if (types.Load(type.GUID) is null)
        {
            type.Description ??= "opticli edge-case fixture: its class was removed from the code";
            types.Save(type);
        }
        return types.Load(type.GUID)!;
    }

    private static ContentReference Page(IContentRepository content, PageData root, ContentType type, Guid guid, string name)
    {
        if (content.TryGet<IContent>(guid, out var existing))
        {
            return existing.ContentLink;
        }
        var page = content.GetDefault<PageData>(root.ContentLink, type.ID);
        page.Name = name;
        page.ContentGuid = guid;
        return content.Save(page, SaveAction.Publish, AccessLevel.NoAccess);
    }

    /// <summary><c>POST /opticli-fixture/orphans</c> from the machine itself: <see cref="Seed"/>, so the removal tests can run again.</summary>
    private sealed class ReseedEndpoint : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (!context.Request.Path.Equals(ReseedPath, StringComparison.OrdinalIgnoreCase))
                {
                    await nextMiddleware();
                    return;
                }
                if (!HttpMethods.IsPost(context.Request.Method) || context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                Seed(context.RequestServices);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            });
            next(app);
        };
    }
}
