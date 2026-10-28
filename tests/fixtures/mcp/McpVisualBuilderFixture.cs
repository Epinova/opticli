// Copied beside McpFixture.cs by setup.sh into a CMS 13 MCP test site only, for the MCP module's end-to-end tests: Visual
// Builder content an editor reads through the module. Uses the content types of tests/fixtures/cms13's
// VisualBuilderFixture.cs, which the CMS 13 edge-case site already has. At startup, on this site's database only, it
// makes (once) an experience below the start page with inline sections, rows, columns and elements, and shared content
// by reference: the CMS 13 fixture's shared element, a section-enabled block (VbBanner) standing in the outline as a
// section, and a section of its own (a grid section as content: CMS 13.3's edit UI makes these only as blueprints, and
// its validation refuses them elsewhere, so it is saved without validation, as an API or an older version could have),
// and one of each only administrators can read (in a folder of their own): an editor sees that something is there,
// nothing more. A second experience holds an element type with properties the edit UI hides or locks (McpVbFieldsElement),
// inline and in a shared section, and has a draft (saved by nobody, as a job's) that changes its composition: adds the
// hidden shared content and changes hidden values, for what a write's diff shows an editor.
using System.ComponentModel.DataAnnotations;
using EPiServer.Applications;
using EPiServer.DataAccess;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.Security;
using EPiServer.VisualBuilder;
using EPiServer.VisualBuilder.Compositions;
using OptiCliCms13;

namespace OptiCliMcpFixture;

/// <summary>
/// An element with properties the edit UI hides or locks, as McpFieldsBlock has them: one it doesn't show, one it shows
/// locked, one an editor descriptor locks for all but WebAdmins, and one on the tab only administrators see. (CMS 13
/// allows no local block in an element.)
/// </summary>
[ContentType(GUID = "6d2e8b4c-1a3f-4c5d-9e7f-0a1b2c3d4e60", DisplayName = "MCP VB fields element", Description = "opticli MCP fixture: an element whose properties the edit UI hides or locks",
    CompositionBehaviors = [CompositionBehavior.ElementEnabledKey])]
public class McpVbFieldsElement : BlockData
{
    public virtual string Heading { get; set; }

    [ScaffoldColumn(false)]
    public virtual string Hidden { get; set; }

    [Editable(false)]
    public virtual string Locked { get; set; }

    [UIHint(McpAdminOnlyEditorDescriptor.Hint)]
    public virtual string AdminScripts { get; set; }

    [Display(GroupName = McpFixtureTabs.AdminOnly)]
    public virtual string AdminTab { get; set; }
}

[InitializableModule]
[ModuleDependency(typeof(McpFixture))]
public class McpVisualBuilderFixture : IInitializableModule
{
    /// <summary>The experience: inline sections and elements, and the four shared ones below.</summary>
    public static readonly Guid Experience = Guid.Parse("0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b301");

    public const string ExperienceName = "MCP Visual Builder fixture";

    /// <summary>A shared grid section editors can read, with a row, a column and an element of its own.</summary>
    public static readonly Guid SharedSection = Guid.Parse("0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b302");

    /// <summary>The folder in the global assets only administrators can read, with the two shared items below.</summary>
    public static readonly Guid HiddenFolder = Guid.Parse("0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b303");

    /// <summary>A shared element only administrators can read.</summary>
    public static readonly Guid HiddenElement = Guid.Parse("0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b304");

    /// <summary>A shared grid section only administrators can read.</summary>
    public static readonly Guid HiddenSection = Guid.Parse("0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b305");

    /// <summary>A shared section-enabled block (VbBanner) editors can read, in the outline as a section.</summary>
    public static readonly Guid SharedBanner = Guid.Parse("0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b306");

    /// <summary>A shared section-enabled block only administrators can read.</summary>
    public static readonly Guid HiddenBanner = Guid.Parse("0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b307");

    public const string HiddenElementName = "MCP VB hidden element";

    public const string HiddenSectionName = "MCP VB hidden section";

    public const string HiddenBannerName = "MCP VB hidden banner";

    /// <summary>The experience with McpVbFieldsElement, and a draft by nobody that changes its composition.</summary>
    public static readonly Guid FieldsExperience = Guid.Parse("0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b308");

    public const string FieldsExperienceName = "MCP Visual Builder fields fixture";

    /// <summary>A shared grid section editors can read, with a McpVbFieldsElement of its own.</summary>
    public static readonly Guid FieldsSection = Guid.Parse("0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2b309");

    /// <summary>The fields experience's node keys, the same in both its versions.</summary>
    public const string FieldsSectionKey = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2c001";
    public const string FieldsRowKey = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2c002";
    public const string FieldsColumnKey = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2c003";
    public const string FieldsElementKey = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2c004";
    public const string SharedFieldsKey = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2c005";
    public const string HiddenElementKey = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2c006";
    public const string HiddenBannerKey = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2c007";
    public const string HiddenSectionKey = "0b7e1c2d-3f4a-4b5c-8d6e-7f8091a2c008";

    public void Initialize(InitializationEngine context) =>
        context.InitComplete += (_, _) => SetupAtStart(context);

    public void Uninitialize(InitializationEngine context)
    {
    }

    private static void SetupAtStart(InitializationEngine context)
    {
        try
        {
            Setup(context.Locate.Advanced);
        }
        catch (Exception e)
        {
            // The site should still start; setup.sh looks for this line in the log.
            Console.Error.WriteLine($"[mcp-fixture] setup failed: {e}");
        }
    }

    private static void Setup(IServiceProvider services)
    {
        var repository = services.GetRequiredService<IContentRepository>();
        var security = services.GetRequiredService<IContentSecurityRepository>();
        var blocks = services.GetRequiredService<IBlockPropertyFactory>();
        var mapper = services.GetRequiredService<ICompositionMapper>();

        var hiddenFolder = McpFixture.Folder(repository, ContentReference.GlobalBlockFolder, "opticli MCP hidden VB", HiddenFolder);
        McpFixture.Ensure(security, hiddenFolder, inherit: false,
            ("Administrators", AccessLevel.FullAccess),
            ("WebAdmins", AccessLevel.FullAccess));

        var sharedSection = Section(repository, blocks, mapper, ContentReference.GlobalBlockFolder, SharedSection, "MCP VB shared section", "In a shared section");
        var hiddenSection = Section(repository, blocks, mapper, hiddenFolder, HiddenSection, HiddenSectionName, "Hidden section text");
        var sharedBanner = Banner(repository, ContentReference.GlobalBlockFolder, SharedBanner, "MCP VB shared banner", "A shared banner");
        var hiddenBanner = Banner(repository, hiddenFolder, HiddenBanner, HiddenBannerName, "Hidden banner title");
        if (!repository.TryGet<VbTextElement>(HiddenElement, out var hiddenElement))
        {
            var block = repository.GetDefault<VbTextElement>(hiddenFolder);
            block.Heading = "Hidden heading";
            block.Body = new XhtmlString("<p>Only administrators can read this element.</p>");
            ((IContent)block).Name = HiddenElementName;
            ((IContent)block).ContentGuid = HiddenElement;
            repository.Save((IContent)block, SaveAction.Publish, AccessLevel.NoAccess);
            hiddenElement = repository.Get<VbTextElement>(HiddenElement);
            Console.Error.WriteLine("[mcp-fixture] created the hidden shared element");
        }
        var start = services.GetRequiredService<IApplicationRepository>().GetDefault()?.EntryPoint
            ?? throw new InvalidOperationException("The site has no default application.");
        Fields(repository, blocks, mapper, start, hiddenElement, hiddenBanner, hiddenSection);
        if (repository.TryGet<IContent>(Experience, out _))
        {
            return;
        }
        // The CMS 13 fixture's shared element (VisualBuilderFixture.SharedElement), which every editor can read.
        var sharedElement = repository.Get<VbTextElement>(VisualBuilderFixture.SharedElement);

        var page = repository.GetDefault<VbExperience>(start);
        page.Name = ExperienceName;
        page.ContentGuid = Experience;
        page.Summary = "Visual Builder content for the MCP module's tests";

        var composition = new Composition();
        var hero = composition.AddSection(blocks.Create<VbSection>(), "Hero")
            .WithStyles(VisualBuilderFixture.SectionTemplate, new Dictionary<string, string> { ["background"] = "light" });
        hero.AddRow("Hero row").AddColumn("Hero column").AddComponent(blocks.Create<VbTextElement>(e =>
        {
            e.Heading = "MCP welcome";
            e.Body = new XhtmlString("<p>An inline element in an inline section.</p>");
        }), "Intro").WithStyles(VisualBuilderFixture.ElementTemplate, new Dictionary<string, string> { ["color"] = "accent" });

        var body = composition.AddSection(blocks.Create<VbSection>(), "Body");
        var row = body.AddRow("Shared elements");
        row.AddColumn("Readable").AddComponent(sharedElement, "Shared element");
        var hiddenColumn = row.AddColumn("Hidden");
        hiddenColumn.AddComponent(hiddenElement, "Hidden element");
        hiddenColumn.AddComponent(blocks.Create<VbLinkElement>(e =>
        {
            e.Heading = "Inline link";
            e.Target = start;
            e.Link = new Url("https://example.com/mcp");
        }), "Link");

        composition.Nodes.Add(new ComponentNode { Name = "Shared banner", NodeType = "component", LayoutType = "section", Key = Guid.NewGuid().ToString(), Component = sharedBanner });
        composition.Nodes.Add(new ComponentNode { Name = "Hidden banner", NodeType = "component", LayoutType = "section", Key = Guid.NewGuid().ToString(), Component = hiddenBanner });
        composition.AddSection(sharedSection, "Shared section");
        composition.AddSection(hiddenSection, "Hidden section");

        mapper.Populate(page, composition);
        var link = repository.Save(page, SaveAction.Publish, AccessLevel.NoAccess);
        Console.Error.WriteLine($"[mcp-fixture] created the Visual Builder experience {link}");

        // The shared nodes must be references, not copies: what the tests rely on.
        var saved = mapper.ToComposition(repository.Get<VbExperience>(link.ToReferenceWithoutVersion()), false);
        var shared = Shared(saved).ToList();
        foreach (var guid in new[] { VisualBuilderFixture.SharedElement, HiddenElement, SharedBanner, HiddenBanner, SharedSection, HiddenSection })
        {
            if (!shared.Contains(guid))
            {
                throw new InvalidOperationException($"The experience's composition doesn't refer to the shared content {guid}.");
            }
        }
    }

    /// <summary>
    /// The fields experience, made if it isn't there: published with an inline section holding an inline
    /// McpVbFieldsElement and the shared fields section; then a draft, saved by nobody, whose composition also has the
    /// hidden shared element (styled) in the same column and the hidden banner and section in the outline, and whose
    /// inline element has other values, hidden ones included.
    /// </summary>
    private static void Fields(IContentRepository repository, IBlockPropertyFactory blocks, ICompositionMapper mapper, ContentReference start,
        VbTextElement hiddenElement, VbBanner hiddenBanner, VbSection hiddenSection)
    {
        if (repository.TryGet<IContent>(FieldsExperience, out _))
        {
            return;
        }
        var sharedFields = repository.TryGet<VbSection>(FieldsSection, out var existing) ? existing : FieldsSectionContent(repository, blocks, mapper);

        var page = repository.GetDefault<VbExperience>(start);
        page.Name = FieldsExperienceName;
        page.ContentGuid = FieldsExperience;
        page.Summary = "Visual Builder elements whose properties the edit UI hides or locks";
        mapper.Populate(page, FieldsComposition(blocks, sharedFields, "Fields heading", "", null));
        var link = repository.Save(page, SaveAction.Publish, AccessLevel.NoAccess);

        var draft = (VbExperience)repository.Get<VbExperience>(link.ToReferenceWithoutVersion()).CreateWritableClone();
        mapper.Populate(draft, FieldsComposition(blocks, sharedFields, "Fields heading, draft", ", draft", (hiddenElement, hiddenBanner, hiddenSection)));
        var draftLink = repository.Save(draft, SaveAction.Save | SaveAction.ForceNewVersion, AccessLevel.NoAccess);
        Console.Error.WriteLine($"[mcp-fixture] created the Visual Builder fields experience {link} and its draft {draftLink}");
    }

    private static Composition FieldsComposition(IBlockPropertyFactory blocks, VbSection sharedFields, string heading, string suffix,
        (VbTextElement Element, VbBanner Banner, VbSection Section)? hidden)
    {
        var column = new StructureNode { Key = FieldsColumnKey, Name = "Fields column", NodeType = LayoutStructureType.Column };
        column.Nodes.Add(new ComponentNode { Key = FieldsElementKey, Name = "Fields", NodeType = "component", Component = FieldsElement(blocks, heading, suffix) });
        if (hidden is { } shown)
        {
            column.Nodes.Add(new ComponentNode { Key = HiddenElementKey, Name = "Hidden element", NodeType = "component", Component = shown.Element }
                .WithStyles(VisualBuilderFixture.ElementTemplate, new Dictionary<string, string> { ["color"] = "accent" }));
        }
        var row = new StructureNode { Key = FieldsRowKey, Name = "Fields row", NodeType = LayoutStructureType.Row };
        row.Nodes.Add(column);
        var section = new SectionNode { Key = FieldsSectionKey, Name = "Fields section", NodeType = "section", LayoutType = "grid", SectionData = blocks.Create<VbSection>() }
            .WithStyles(VisualBuilderFixture.SectionTemplate, new Dictionary<string, string> { ["background"] = "light" });
        section.Nodes.Add(row);
        var composition = new Composition();
        composition.Nodes.Add(section);
        composition.Nodes.Add(new SectionNode { Key = SharedFieldsKey, Name = "Shared fields section", NodeType = "section", LayoutType = "grid", SectionData = sharedFields });
        if (hidden is { } more)
        {
            composition.Nodes.Add(new ComponentNode { Key = HiddenBannerKey, Name = "Hidden banner", NodeType = "component", LayoutType = "section", Component = more.Banner });
            composition.Nodes.Add(new SectionNode { Key = HiddenSectionKey, Name = "Hidden section", NodeType = "section", LayoutType = "grid", SectionData = more.Section });
        }
        return composition;
    }

    /// <summary>An inline McpVbFieldsElement with a value in every property, the hidden and locked ones included.</summary>
    private static McpVbFieldsElement FieldsElement(IBlockPropertyFactory blocks, string heading, string suffix) => blocks.Create<McpVbFieldsElement>(e =>
    {
        e.Heading = heading;
        e.Hidden = "Hidden value" + suffix;
        e.Locked = "Locked value" + suffix;
        e.AdminScripts = "Admin scripts value" + suffix;
        e.AdminTab = "Admin tab value" + suffix;
    });

    /// <summary>The shared fields section: a row, a column and a McpVbFieldsElement of its own.</summary>
    private static VbSection FieldsSectionContent(IContentRepository repository, IBlockPropertyFactory blocks, ICompositionMapper mapper)
    {
        var section = repository.GetDefault<VbSection>(ContentReference.GlobalBlockFolder);
        ((IContent)section).Name = "MCP VB fields section";
        ((IContent)section).ContentGuid = FieldsSection;
        var node = new SectionNode { Name = "MCP VB fields section", NodeType = "section", LayoutType = "grid", SectionData = section, Key = FieldsSection.ToString() };
        node.AddRow("Row").AddColumn("Column").AddComponent(FieldsElement(blocks, "Shared fields heading", " in the shared section"), "Shared fields");
        mapper.PopulateSection(section, node);
        // SharedSectionValidator refuses a grid section outside the blueprints.
        repository.Save((IContent)section, SaveAction.Publish | SaveAction.SkipValidation, AccessLevel.NoAccess);
        Console.Error.WriteLine("[mcp-fixture] created the shared fields section");
        return repository.Get<VbSection>(FieldsSection);
    }

    /// <summary>A shared section with a row, a column and a text element of its own, made if it isn't there.</summary>
    private static VbSection Section(IContentRepository repository, IBlockPropertyFactory blocks, ICompositionMapper mapper, ContentReference folder, Guid guid, string name, string heading)
    {
        if (repository.TryGet<VbSection>(guid, out var existing))
        {
            return existing;
        }
        var section = repository.GetDefault<VbSection>(folder);
        ((IContent)section).Name = name;
        ((IContent)section).ContentGuid = guid;
        var node = new SectionNode { Name = name, NodeType = "section", LayoutType = "grid", SectionData = section, Key = guid.ToString() };
        node.AddRow("Row").AddColumn("Column").AddComponent(blocks.Create<VbTextElement>(e =>
        {
            e.Heading = heading;
            e.Body = new XhtmlString($"<p>{heading}.</p>");
        }), "Text");
        mapper.PopulateSection(section, node);
        // SharedSectionValidator refuses a grid section outside the blueprints.
        repository.Save((IContent)section, SaveAction.Publish | SaveAction.SkipValidation, AccessLevel.NoAccess);
        Console.Error.WriteLine($"[mcp-fixture] created the shared section {name}");
        return repository.Get<VbSection>(guid);
    }

    /// <summary>A shared section-enabled block, made if it isn't there.</summary>
    private static VbBanner Banner(IContentRepository repository, ContentReference folder, Guid guid, string name, string title)
    {
        if (repository.TryGet<VbBanner>(guid, out var existing))
        {
            return existing;
        }
        var banner = repository.GetDefault<VbBanner>(folder);
        banner.Title = title;
        ((IContent)banner).Name = name;
        ((IContent)banner).ContentGuid = guid;
        repository.Save((IContent)banner, SaveAction.Publish, AccessLevel.NoAccess);
        Console.Error.WriteLine($"[mcp-fixture] created the shared banner {name}");
        return repository.Get<VbBanner>(guid);
    }

    /// <summary>The content GUIDs of the shared blocks and sections the composition refers to.</summary>
    private static IEnumerable<Guid> Shared(CompositionNode node)
    {
        var block = node switch
        {
            ComponentNode component => component.Component,
            SectionNode section when node is not Composition => section.SectionData,
            _ => null,
        };
        if (block is IContent { ContentLink: var link } content && !ContentReference.IsNullOrEmpty(link))
        {
            yield return content.ContentGuid;
        }
        if (node is StructureNode structure)
        {
            foreach (var guid in structure.Nodes.SelectMany(Shared))
            {
                yield return guid;
            }
        }
    }
}
