// Copied into the edge-case site by setup.sh, for opticli's `types --orphaned`, `type`, `types remove`,
// `types remove-property` and `types prune` integration tests: what the CMS leaves in the database after code is removed.
// - EdgeRemovedPage: a page type whose class is gone, kept because a page uses it.
// - EdgeTrashedPage: the same, with its only page in the recycle bin. On CMS 13 it is stored as that CMS stores a type
//   made from a class with a GUID: no class on record, only the version of its assembly (Cms13Fixture.cs).
// - EdgeRemovedEmptyPage: a page type whose class is gone and that nothing uses, with a property Teaser whose block type
//   is EdgeRemovedBlock, a block type whose class is gone too. The CMS removes such an empty type when the site starts,
//   so this module makes it again on every start.
// - EdgeRemovedText (one value) and EdgeRemovedEmptyText (none): properties of EdgePage that its class doesn't declare.
// - EdgeAdminPage: a page type made in admin mode (no class on record), which isn't an orphan.
// Made through the CMS's repositories once the plan's language root exists, so setup.sh's second start makes them. The
// removal tests make them again by posting to /opticli-fixture/orphans (loopback only), which runs the same code.
// Property definitions are saved, and the page moved to the recycle bin, with APIs that differ between CMS 12 and 13:
// Cms12Fixture.cs and Cms13Fixture.cs do that (setup.sh copies the one that matches the site).
using System.Net;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.Security;
using EPiServer.ServiceLocation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace OptiCliEdgeCases;

[InitializableModule]
[ModuleDependency(typeof(EdgeCasesSetup))]
public partial class OptiCliOrphansFixture : IConfigurableModule
{
    /// <summary>A page type whose class (<see cref="RemovedClass"/>) doesn't exist.</summary>
    public static readonly Guid RemovedType = Guid.Parse("3B0C4A5E-0D1F-4E5A-9C7B-6A1D2E3F4A10");

    public const string RemovedTypeName = "EdgeRemovedPage";

    public static readonly string RemovedClass = Class("EdgeRemovedPage");

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

        var removed = Type(types, new PageType { GUID = RemovedType, Name = RemovedTypeName, DisplayName = "Edge removed page", ModelTypeString = RemovedClass });
        Page(content, root, removed, RemovedTypePage, "Edge removed-type page");

        var trashed = Type(types, WithoutRecordedClass(new PageType { GUID = TrashedType, Name = "EdgeTrashedPage", DisplayName = "Edge trashed page", ModelTypeString = Class("EdgeTrashedPage") }));
        var trashedPage = Page(content, root, trashed, TrashedTypePage, "Edge trashed-type page");
        if (!content.Get<PageData>(trashedPage).IsDeleted)
        {
            Trash(locate, content, trashedPage);
        }

        var blockType = NewBlockType();
        blockType.GUID = RemovedBlock;
        blockType.Name = "EdgeRemovedBlock";
        blockType.DisplayName = "Edge removed block";
        blockType.ModelTypeString = Class("EdgeRemovedBlock");
        var block = Type(types, blockType);
        var empty = Type(types, new PageType { GUID = EmptyType, Name = "EdgeRemovedEmptyPage", DisplayName = "Edge removed empty page", ModelTypeString = Class("EdgeRemovedEmptyPage") });
        if (empty.PropertyDefinitions.All(p => p.Name != "Teaser"))
        {
            SaveProperty(locate, empty, new PropertyDefinition { Name = "Teaser", EditCaption = "Teaser", ExistsOnModel = true }, block.GUID);
        }

        Type(types, new PageType { GUID = AdminType, Name = "EdgeAdminPage", DisplayName = "Edge admin-mode page", Description = "opticli edge-case fixture: made in admin mode" });

        foreach (var name in new[] { RemovedProperty, EmptyProperty })
        {
            var edgePage = types.Load<EdgePage>();
            if (edgePage.PropertyDefinitions.All(p => p.Name != name))
            {
                SaveProperty(locate, edgePage, new PropertyDefinition { Name = name, EditCaption = name, ExistsOnModel = false }, null);
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

    /// <summary>Saves a new String property (<paramref name="blockType"/> null) or block property on <paramref name="type"/>.</summary>
    private static partial void SaveProperty(IServiceProvider locate, ContentType type, PropertyDefinition property, Guid? blockType);

    /// <summary>Moves a page to the recycle bin, whatever access the startup principal has.</summary>
    private static partial void Trash(IServiceProvider locate, IContentRepository content, ContentReference page);

    /// <summary>A new block type: CMS 12's <c>BlockType</c>; CMS 13 made that obsolete for a content type with that base.</summary>
    private static partial ContentType NewBlockType();

    /// <summary>
    /// CMS 13: a new type as that CMS stores one its model sync made from a class with a GUID (no class on record, the
    /// assembly's version); CMS 12 records the class of every such type, so there it is returned as it is.
    /// </summary>
    private static partial ContentType WithoutRecordedClass(ContentType type);

    /// <summary>
    /// A class of the site's own assembly (Alloy on CMS 12, the CMS 13 template's Alloy13) that doesn't exist, so the source
    /// scan counts the type as the site's.
    /// </summary>
    private static string Class(string name) => $"OptiCliEdgeCases.{name}, {typeof(EdgePage).Assembly.GetName().Name}";

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
