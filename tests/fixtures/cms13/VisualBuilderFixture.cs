// Copied into the CMS 13 test sites by setup.sh (tests/fixtures/cms13). It adds a small set of Visual Builder content
// types and, on POST /opticli-fixture/visual-builder from the machine itself, makes sample content with the CMS's own
// API, so opticli can be checked against what the CMS stores:
// - VbExperience (an experience) with two inline sections (VbSection), rows, columns and elements: VbTextElement
//   (rich text) and VbLinkElement (a content reference and a URL), one of them a shared block rather than inline.
// - Two display templates (one for sections, one for elements) and display settings on the composition's nodes.
// - IVbHeading, a contract both element types implement.
// - A blueprint made from the experience, and a content variation of it.
// GET on the same path reports what the CMS's API returns for that content (IContentLoader, the composition mapper,
// versions, blueprints, display templates), as JSON. Everything is made once; posting again only fills in what's missing.
// The experience is allowed under Alloy's start page, so the site project must be the one setup.sh makes (Alloy13), and
// on a new database the fixture is added only after Alloy's content import, which resets the start page type's settings.
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using EPiServer.Applications;
using EPiServer.DataAccess;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.Security;
using EPiServer.ServiceLocation;
using EPiServer.VisualBuilder;
using EPiServer.VisualBuilder.Compositions;
using EPiServer.Web.Mvc;
using EPiServer.Web.Routing;
using Microsoft.AspNetCore.Mvc;

namespace OptiCliCms13;

/// <summary>A contract: both element types have a Heading.</summary>
[ContentType(GUID = "5C1D7A40-2B3E-4F60-8A71-9D0E1F2A3B01", DisplayName = "VB heading contract", Description = "opticli CMS 13 fixture")]
public interface IVbHeading : IContentData
{
    string Heading { get; set; }
}

[ContentType(GUID = "5C1D7A40-2B3E-4F60-8A71-9D0E1F2A3B02", DisplayName = "VB experience", Description = "opticli CMS 13 fixture")]
[AvailableContentTypes(IncludeOn = [typeof(Alloy13.Models.Pages.StartPage)])]
public class VbExperience : ExperienceData
{
    [CultureSpecific]
    [Display(Order = 10)]
    public virtual string Summary { get; set; }
}

[ContentType(GUID = "5C1D7A40-2B3E-4F60-8A71-9D0E1F2A3B03", DisplayName = "VB section", Description = "opticli CMS 13 fixture",
    CompositionBehaviors = [CompositionBehavior.SectionEnabledKey])]
public class VbSection : SectionData
{
}

[ContentType(GUID = "5C1D7A40-2B3E-4F60-8A71-9D0E1F2A3B04", DisplayName = "VB text element", Description = "opticli CMS 13 fixture",
    CompositionBehaviors = [CompositionBehavior.ElementEnabledKey])]
public class VbTextElement : BlockData, IVbHeading
{
    [CultureSpecific]
    [Display(Order = 10)]
    public virtual string Heading { get; set; }

    [CultureSpecific]
    [Display(Order = 20)]
    public virtual XhtmlString Body { get; set; }
}

[ContentType(GUID = "5C1D7A40-2B3E-4F60-8A71-9D0E1F2A3B05", DisplayName = "VB link element", Description = "opticli CMS 13 fixture",
    CompositionBehaviors = [CompositionBehavior.ElementEnabledKey])]
public class VbLinkElement : BlockData, IVbHeading
{
    [CultureSpecific]
    [Display(Order = 10)]
    public virtual string Heading { get; set; }

    [Display(Order = 20)]
    public virtual ContentReference Target { get; set; }

    [Display(Order = 30)]
    public virtual Url Link { get; set; }
}

/// <summary>Renders an experience as plain text from the CMS's composition, so a request shows the site can render it.</summary>
public class VbExperienceController(ICompositionMapper compositions) : PageController<VbExperience>
{
    public IActionResult Index(VbExperience currentPage)
    {
        var lines = new List<string> { currentPage.Name, currentPage.Summary ?? "" };
        Write(compositions.ToComposition(currentPage), 0, lines);
        return Content(string.Join("\n", lines), "text/plain");
    }

    private static void Write(CompositionNode node, int depth, List<string> lines)
    {
        var text = node is ComponentNode { Component: IVbHeading heading } ? $" \"{heading.Heading}\"" : "";
        lines.Add($"{new string(' ', depth * 2)}{node.NodeType} {node.Type}{text}");
        if (node is StructureNode structure)
        {
            foreach (var child in structure.Nodes)
            {
                Write(child, depth + 1, lines);
            }
        }
    }
}

[InitializableModule]
[ModuleDependency(typeof(EPiServer.Web.InitializationModule))]
public class VisualBuilderFixture : IConfigurableModule
{
    public const string Path = "/opticli-fixture/visual-builder";

    public static readonly Guid Experience = Guid.Parse("7a1c2e3d-5b6f-4a70-9c81-0d2e3f4a5b01");

    public static readonly Guid SharedElement = Guid.Parse("7a1c2e3d-5b6f-4a70-9c81-0d2e3f4a5b02");

    public const string ExperienceName = "Visual Builder fixture";

    public const string SectionTemplate = "vbSection";

    public const string ElementTemplate = "vbElement";

    public const string BlueprintName = "VB fixture blueprint";

    public const string VariationKey = "vbFixtureVariation";

    /// <summary>The endpoint runs as an anonymous visitor, who can't read the blueprints.</summary>
    private static readonly ListBlueprintOptions AllBlueprints = new() { RequiredAccess = AccessLevel.NoAccess };

    public void ConfigureContainer(ServiceConfigurationContext context) =>
        context.Services.AddTransient<IStartupFilter, Endpoint>();

    public void Initialize(InitializationEngine context)
    {
    }

    public void Uninitialize(InitializationEngine context)
    {
    }

    /// <summary>Makes whatever of the fixture content is missing, and returns what it did.</summary>
    public static async Task<List<string>> EnsureAsync(IServiceProvider services)
    {
        var done = new List<string>();
        var content = services.GetRequiredService<IContentRepository>();
        EnsureTemplates(services.GetRequiredService<IDisplayTemplateRepository>(), done);

        var start = services.GetRequiredService<IApplicationRepository>().GetDefault()?.EntryPoint
            ?? throw new InvalidOperationException("The site has no default application; request the start page once first.");
        if (!content.TryGet<VbExperience>(Experience, out var experience))
        {
            experience = CreateExperience(services, content, start);
            done.Add($"created experience {experience.ContentLink}");
        }

        var blueprints = services.GetRequiredService<IBlueprintRepository>();
        if (!(await blueprints.ListAsync(AllBlueprints)).Any(b => b.DisplayName == BlueprintName))
        {
            var blueprint = await blueprints.CreateAsync(experience.ContentLink.ToReferenceWithoutVersion(), new CreateBlueprintOptions { Name = BlueprintName, RequiredAccess = AccessLevel.NoAccess });
            done.Add($"created blueprint {blueprint.ID} ({blueprint.Content?.ContentLink})");
        }

        var versions = services.GetRequiredService<IContentVersionRepository>();
        if (!versions.List(new VersionFilter { ContentLink = experience.ContentLink.ToReferenceWithoutVersion(), Variations = [VariationKey] }, 0, 10, out _).Any())
        {
            var variation = (VbExperience)experience.CreateWritableClone();
            variation.Variation = VariationKey;
            variation.Summary = "The variation's summary";
            var saved = content.Save(variation, SaveAction.Publish, AccessLevel.NoAccess);
            done.Add($"published variation '{VariationKey}' as {saved}");
        }
        return done;
    }

    private static void EnsureTemplates(IDisplayTemplateRepository templates, List<string> done)
    {
        if (templates.Load(SectionTemplate) is null)
        {
            templates.Save(new DisplayTemplate
            {
                Key = SectionTemplate,
                Name = "VB section look",
                NodeType = "section",
                Settings =
                [
                    new DisplaySetting
                    {
                        Key = "background", Name = "Background", Editor = DisplaySettingEditor.Select,
                        Choices = [new DisplaySettingChoice { Key = "light", Name = "Light" }, new DisplaySettingChoice { Key = "dark", Name = "Dark", SortOrder = 1 }],
                    },
                    new DisplaySetting { Key = "fullWidth", Name = "Full width", Editor = DisplaySettingEditor.Checkbox, SortOrder = 1 },
                ],
            });
            done.Add($"created display template {SectionTemplate}");
        }
        if (templates.Load(ElementTemplate) is null)
        {
            templates.Save(new DisplayTemplate
            {
                Key = ElementTemplate,
                Name = "VB element look",
                NodeType = "component",
                Settings =
                [
                    new DisplaySetting
                    {
                        Key = "color", Name = "Color", Editor = DisplaySettingEditor.Select,
                        Choices = [new DisplaySettingChoice { Key = "plain", Name = "Plain" }, new DisplaySettingChoice { Key = "accent", Name = "Accent", SortOrder = 1 }],
                    },
                ],
            });
            done.Add($"created display template {ElementTemplate}");
        }
    }

    private static VbExperience CreateExperience(IServiceProvider services, IContentRepository content, ContentReference start)
    {
        var blocks = services.GetRequiredService<IBlockPropertyFactory>();
        var mapper = services.GetRequiredService<ICompositionMapper>();

        // One shared element, a block of its own in the global block folder, used by reference.
        if (!content.TryGet<VbTextElement>(SharedElement, out var shared))
        {
            var block = content.GetDefault<VbTextElement>(ContentReference.GlobalBlockFolder);
            block.Heading = "Shared element";
            block.Body = new XhtmlString("<p>A shared element block, used by reference.</p>");
            ((IContent)block).Name = "VB shared element";
            ((IContent)block).ContentGuid = SharedElement;
            content.Save((IContent)block, SaveAction.Publish, AccessLevel.NoAccess);
            shared = content.Get<VbTextElement>(SharedElement);
        }

        var page = content.GetDefault<VbExperience>(start);
        page.Name = ExperienceName;
        page.ContentGuid = Experience;
        page.Summary = "An experience made by opticli's CMS 13 fixture";

        var composition = new Composition();
        var hero = composition.AddSection(blocks.Create<VbSection>(), "Hero")
            .WithStyles(SectionTemplate, new Dictionary<string, string> { ["background"] = "dark", ["fullWidth"] = "true" });
        var heroColumn = hero.AddRow("Hero row").AddColumn("Hero column");
        heroColumn.AddComponent(blocks.Create<VbTextElement>(e =>
        {
            e.Heading = "Welcome";
            e.Body = new XhtmlString("<p>Rich text in an <strong>inline</strong> element.</p>");
        }), "Intro").WithStyles(ElementTemplate, new Dictionary<string, string> { ["color"] = "accent" });

        var body = composition.AddSection(blocks.Create<VbSection>(), "Body");
        var row = body.AddRow("Two columns");
        row.AddColumn("Left").AddComponent(blocks.Create<VbLinkElement>(e =>
        {
            e.Heading = "Read more";
            e.Target = start;
            e.Link = new Url("https://example.com/");
        }), "Link");
        var right = row.AddColumn("Right");
        right.AddComponent(blocks.Create<VbTextElement>(e =>
        {
            e.Heading = "Second text";
            e.Body = new XhtmlString("<p>Another inline element.</p>");
        }), "Second text");
        right.AddComponent(shared, "Shared");

        mapper.Populate(page, composition);
        var link = content.Save(page, SaveAction.Publish, AccessLevel.NoAccess);
        return content.Get<VbExperience>(link.ToReferenceWithoutVersion());
    }

    /// <summary>What the CMS's API returns for the fixture content.</summary>
    public static async Task<object> ReportAsync(IServiceProvider services)
    {
        var loader = services.GetRequiredService<IContentLoader>();
        var types = services.GetRequiredService<IContentTypeRepository>();
        var mapper = services.GetRequiredService<ICompositionMapper>();
        if (!loader.TryGet<VbExperience>(Experience, out var experience))
        {
            return new { experience = (object)null };
        }
        var versions = services.GetRequiredService<IContentVersionRepository>()
            .List(new VersionFilter { ContentLink = experience.ContentLink.ToReferenceWithoutVersion() }, 0, 100, out _)
            .Select(v => new { link = v.ContentLink.ToString(), v.Status, v.Variation, language = v.LanguageBranch, v.IsMasterLanguageBranch });
        var variation = loader.Get<VbExperience>(experience.ContentLink.ToReferenceWithoutVersion(), new LoaderOptions { VariationLoaderOption.With(VariationKey) });
        var blueprints = (await services.GetRequiredService<IBlueprintRepository>().ListAsync(AllBlueprints))
            .Select(b => new { b.ID, b.DisplayName, content = b.Content?.ContentLink.ToString(), type = b.Content is null ? null : types.Load(b.Content.ContentTypeID)?.Name });
        var templates = services.GetRequiredService<IDisplayTemplateRepository>().List()
            .Select(t => new { t.ID, t.Key, t.Name, t.NodeType, t.BaseType, t.ContentTypeID, t.IsDefault, settings = t.Settings.Select(s => new { s.Key, s.Editor, choices = s.Choices.Select(c => c.Key) }) });
        var fixtureTypes = new[] { typeof(IVbHeading), typeof(VbExperience), typeof(VbSection), typeof(VbTextElement), typeof(VbLinkElement) }
            .Select(t => types.Load(t))
            .Select(t => new { t.ID, t.Name, t.GUID, t.Base, t.IsContract, compositionBehaviors = t.CompositionBehaviors.Select(b => b.ToString()), contracts = t.Contracts?.Select(c => new { c.GUID, c.Name }), properties = t.PropertyDefinitions.Select(p => new { p.Name, p.ID, type = p.Type.Name, dataType = p.Type.DataType.ToString(), typeName = p.Type.TypeName, kind = p.Kind.ToString(), itemType = p.ItemTypeReference is { } item ? new { item.GUID, item.Name } : null, p.IsSystemProperty }) });
        return new
        {
            url = services.GetRequiredService<IUrlResolver>().GetUrl(experience.ContentLink.ToReferenceWithoutVersion()),
            experience = Describe(experience, types),
            composition = Node(mapper.ToComposition(experience, false), types),
            variation = new { variation.ContentLink, variation.Variation, variation.Summary },
            versions,
            blueprints,
            templates,
            types = fixtureTypes,
        };
    }

    private static object Describe(IContentData data, IContentTypeRepository types) => new
    {
        link = (data as IContent)?.ContentLink.ToString(),
        type = types.Load(data.ContentTypeID)?.Name,
        properties = data.Property.Where(p => p.PropertyDefinitionID > 0).Select(p => new
        {
            p.Name,
            propertyType = p.GetType().FullName,
            valueType = p.Value?.GetType().FullName,
            value = p.Value switch
            {
                null => null,
                ContentArea area => (object)area.Items.Select(i => new
                {
                    contentLink = i.ContentLink?.ToString(),
                    inlineBlock = i.InlineBlock is null ? null : Describe(i.InlineBlock, types),
                    i.RenderSettings,
                }),
                BlockData block => Describe(block, types),
                var other => other.ToString(),
            },
        }),
    };

    private static object Node(CompositionNode node, IContentTypeRepository types) => new
    {
        node.NodeType,
        node.Key,
        node.Name,
        node.Type,
        node.LayoutType,
        node.DisplayTemplateKey,
        node.DisplaySettings,
        component = node is ComponentNode { Component: { } component } ? Describe(component, types) : null,
        nodes = node is StructureNode structure ? structure.Nodes.Select(n => Node(n, types)) : null,
    };

    /// <summary>POST makes the content, GET reports it; from the machine itself only.</summary>
    private sealed class Endpoint : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (!context.Request.Path.Equals(Path, StringComparison.OrdinalIgnoreCase))
                {
                    await nextMiddleware();
                    return;
                }
                if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                object result;
                try
                {
                    result = HttpMethods.IsPost(context.Request.Method)
                        ? new { ok = true, done = await EnsureAsync(context.RequestServices) }
                        : new { ok = true, report = await ReportAsync(context.RequestServices) };
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    result = new { ok = false, error = ex.ToString() };
                }
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            });
            next(app);
        };
    }
}
