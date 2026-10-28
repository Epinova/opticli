using System.ComponentModel.DataAnnotations;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Security;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Tests.Content;
using OptiCli.Cms;
using OptiCli.Cms.Compat;
using OptiCli.Cms.Content;
using OptiCli.Protocol;
#if CMS13
using EPiServer.VisualBuilder;
using EPiServer.VisualBuilder.Compositions;
#endif

namespace OptiCli.Agent.Tests.Cms;

/// <summary>
/// A Visual Builder composition as each caller reads it, and a change to one as a write shows it: the developer sees
/// everything; an editor sees what every other read shows them. Shared content they can't read is only its place in the
/// composition, and an element's properties are those the edit UI shows them, by the content the element is in.
/// </summary>
public class CmsCompositionReadTests
{
    private const int AdminTab = 42;

    [Fact]
    public void Content_without_a_composition_has_none_for_either_caller()
    {
        var page = new CmsCallTests.Secured(new ContentReference(5), AccessLevel.Read);

        Assert.Null(CmsCompositions.Read(Call(CmsCaller.Developer), page, _ => new Dictionary<string, ContentItemProperty>()));
        Assert.Null(CmsCompositions.Read(Call(CmsCaller.Editor), page, _ => new Dictionary<string, ContentItemProperty>()));
        Assert.Empty(CmsCompositions.InlineBlocks(Call(CmsCaller.Editor), page));
    }

    [Fact]
    public void An_unreadable_shared_node_takes_the_kind_of_its_place_and_keeps_its_styles_only_in_a_column()
    {
        Assert.Equal("section", CmsCompositions.UnreadablePlace("experience"));
        Assert.Equal("component", CmsCompositions.UnreadablePlace("column"));
        Assert.True(CmsCompositions.UnreadableKeepsStyles("component", "column"));
        Assert.False(CmsCompositions.UnreadableKeepsStyles("component", "experience"));
        Assert.False(CmsCompositions.UnreadableKeepsStyles("section", "experience"));
        Assert.False(CmsCompositions.UnreadableKeepsStyles("section", "column"));
    }

#if CMS13
    [Fact]
    public void An_editor_sees_shared_content_they_cannot_read_only_as_its_place_and_an_elements_properties_as_the_edit_ui_shows_them()
    {
        var fixture = new Fixture();
        var editor = Call(CmsCaller.Editor, fixture);

        var root = Read(editor, fixture);

        Assert.Equal(("experience", "Experience", "Exp"), (root.NodeType, root.Name, root.Type));
        var (inline, hiddenElement, readableElement) = Column(root);
        // The inline element in the experience: what the edit UI hides is left out; a tab needing Administer, which the
        // editor lacks on the experience, is shown, as the CMS 13 edit UI shows an inline element's tabs whatever their access.
        Assert.Equal(["AdminTabbed", "Heading"], inline.Properties!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new ContentItemCompositionNode("component") { Key = "k-hidden-element", Ref = "50", DisplayTemplate = "vbElement" },
            hiddenElement with { DisplaySettings = null }, new NodeComparer());
        Assert.Equal("accent", hiddenElement.DisplaySettings!["color"]);
        Assert.Equal(("Readable element", "SharedElement", "51"), (readableElement.Name, readableElement.Type, readableElement.Ref));

        var outline = root.Nodes!;
        // In the outline: a section-enabled block and a section alike, without styles, a name, a type or nodes.
        Assert.Equal(new ContentItemCompositionNode("section") { Key = "k-hidden-banner", Ref = "52" }, outline[1], new NodeComparer());
        Assert.Equal(new ContentItemCompositionNode("section") { Key = "k-hidden-section", Ref = "53" }, outline[2], new NodeComparer());
        // A shared section the editor reads: its own nodes, and its element's properties as the edit UI shows them.
        var shared = outline[3];
        Assert.Equal(("Shared section", "54"), (shared.Name, shared.Ref));
        var inShared = shared.Nodes!.Single().Nodes!.Single().Nodes!.Single();
        Assert.Equal(["AdminTabbed", "Heading"], inShared.Properties!.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("Hidden", JsonSerializer.Serialize(root, AgentJson.Options));
    }

    [Fact]
    public void The_developer_reads_every_node_with_everything_in_it()
    {
        var fixture = new Fixture();
        var root = Read(Call(CmsCaller.Developer, fixture), fixture);

        var (inline, hiddenElement, _) = Column(root);
        Assert.Equal(["AdminTabbed", "Heading", "Scaffolded"], inline.Properties!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("component", "Hidden element", "SharedElement", "section", "50"), (hiddenElement.NodeType, hiddenElement.Name, hiddenElement.Type, hiddenElement.LayoutType, hiddenElement.Ref));
        var outline = root.Nodes!;
        Assert.Equal(("component", "Hidden banner", "Banner", "52"), (outline[1].NodeType, outline[1].Name, outline[1].Type, outline[1].Ref));
        Assert.Equal(("section", "Hidden section", "SharedSection", "grid", "53"), (outline[2].NodeType, outline[2].Name, outline[2].Type, outline[2].LayoutType, outline[2].Ref));
        Assert.Equal(("dark", "Hidden text"), (outline[2].DisplaySettings!["background"], outline[2].Nodes!.Single().Nodes!.Single().Nodes!.Single().Properties!["Heading"].Value!.Value.GetString()));
    }

    [Fact]
    public void A_change_to_a_composition_shows_an_editor_what_a_read_shows_them()
    {
        var fixture = new Fixture();
        var before = Snapshot(heading: "Old", scaffolded: "s1", tabbed: "t1", hidden: false);
        var after = Snapshot(heading: "New", scaffolded: "s2", tabbed: "t2", hidden: true);
        var changes = new List<PropertyChange> { new("composition", before, after) };

        var developer = Call(CmsCaller.Developer, fixture).Properties.Shown(fixture.Experience, changes);
        Assert.Same(changes, developer);

        var shown = Assert.Single(Call(CmsCaller.Editor, fixture).Properties.Shown(fixture.Experience, changes));
        var column = (JsonObject)JsonNode.Parse(shown.After!.Value.GetRawText())!["nodes"]![0]!["nodes"]![0]!["nodes"]![0]!;
        var nodes = column["nodes"]!.AsArray();
        Assert.Equal("""{"Heading":"New","AdminTabbed":"t2"}""", nodes[0]!["properties"]!.ToJsonString());
        Assert.Equal("""{"nodeType":"component","key":"k-hidden-element","ref":"50","displayTemplate":"vbElement","displaySettings":{"color":"accent"}}""", nodes[1]!.ToJsonString());
        var outline = JsonNode.Parse(shown.After!.Value.GetRawText())!["nodes"]!.AsArray();
        Assert.Equal("""{"nodeType":"section","key":"k-hidden-section","ref":"53"}""", outline[1]!.ToJsonString());
        Assert.Equal("""{"Heading":"Old","AdminTabbed":"t1"}""", JsonNode.Parse(shown.Before!.Value.GetRawText())!["nodes"]![0]!["nodes"]![0]!["nodes"]![0]!["nodes"]![0]!["properties"]!.ToJsonString());

        // A change to nothing but what the editor doesn't see isn't shown at all.
        var hiddenOnly = new List<PropertyChange> { new("composition", before, Snapshot(heading: "Old", scaffolded: "s2", tabbed: "t1", hidden: false)) };
        Assert.Empty(Call(CmsCaller.Editor, fixture).Properties.Shown(fixture.Experience, hiddenOnly));
    }

    private static ContentItemCompositionNode Read(CmsCall call, Fixture fixture) =>
        CmsCompositions.Read(call, fixture.Experience, new ReadValues(Types(), call.Properties.Shown).Properties)!;

    private static (ContentItemCompositionNode Inline, ContentItemCompositionNode Hidden, ContentItemCompositionNode Readable) Column(ContentItemCompositionNode root)
    {
        var column = root.Nodes![0].Nodes!.Single().Nodes!.Single().Nodes!;
        return (column[0], column[1], column[2]);
    }

    /// <summary>A composition's snapshot as a write's diff has it (CmsCompositionWrites.Snapshot): the inline section and, with <paramref name="hidden"/>, the hidden shared content.</summary>
    private static JsonElement Snapshot(string heading, string scaffolded, string tabbed, bool hidden)
    {
        var column = new JsonArray
        {
            new JsonObject { ["nodeType"] = "component", ["key"] = "k-fields", ["name"] = "Fields", ["type"] = "FieldsElement",
                ["properties"] = new JsonObject { ["Heading"] = heading, ["Scaffolded"] = scaffolded, ["AdminTabbed"] = tabbed } },
        };
        var outline = new JsonArray
        {
            new JsonObject { ["nodeType"] = "section", ["key"] = "k-section", ["name"] = "Inline", ["type"] = "Section", ["nodes"] = new JsonArray
            {
                new JsonObject { ["nodeType"] = "row", ["key"] = "k-row", ["nodes"] = new JsonArray
                {
                    new JsonObject { ["nodeType"] = "column", ["key"] = "k-column", ["nodes"] = column },
                } },
            } },
        };
        if (hidden)
        {
            column.Add(new JsonObject { ["nodeType"] = "component", ["key"] = "k-hidden-element", ["name"] = "Hidden element", ["type"] = "SharedElement", ["ref"] = "50",
                ["displayTemplate"] = "vbElement", ["displaySettings"] = new JsonObject { ["color"] = "accent" } });
            outline.Add(new JsonObject { ["nodeType"] = "section", ["key"] = "k-hidden-section", ["name"] = "Hidden section", ["type"] = "SharedSection", ["ref"] = "53",
                ["displayTemplate"] = "vbSection", ["displaySettings"] = new JsonObject { ["background"] = "dark" } });
        }
        return JsonSerializer.SerializeToElement(new JsonObject { ["nodes"] = outline });
    }

    /// <summary>
    /// An experience (the editor has Read and Edit) whose outline holds an inline section with an inline element, a
    /// shared element the editor can't read (styled) and one they can, in one column; a shared section-enabled block and a
    /// shared section they can't read; and a shared section they may administer, with an element of its own.
    /// </summary>
    private sealed class Fixture : ICompositionMapper
    {
        public SecuredExperience Experience { get; } = new(AccessLevel.Read | AccessLevel.Edit);

        public FieldsElement Inline { get; } = FieldsElement.Create("Inline heading");

        public Composition ToComposition(ExperienceData page, bool filterForPublished)
        {
            var composition = new Composition { Key = "root", Name = "Experience", Type = "Exp", ExperienceData = Experience };
            var section = new SectionNode { Key = "k-section", Name = "Inline", Type = "Section", SectionData = new Section() }
                .WithStyles("vbSection", new Dictionary<string, string> { ["background"] = "light" });
            var row = new StructureNode { Key = "k-row", NodeType = "row" };
            var column = new StructureNode { Key = "k-column", NodeType = "column" };
            column.Nodes.Add(new ComponentNode { Key = "k-fields", Name = "Fields", Type = "FieldsElement", Component = Inline });
            column.Nodes.Add(new ComponentNode { Key = "k-hidden-element", Name = "Hidden element", Type = "SharedElement", LayoutType = "section", Component = new SharedElement(50, AccessLevel.NoAccess) }
                .WithStyles("vbElement", new Dictionary<string, string> { ["color"] = "accent" }));
            column.Nodes.Add(new ComponentNode { Key = "k-readable", Name = "Readable element", Type = "SharedElement", Component = new SharedElement(51, AccessLevel.Read) });
            row.Nodes.Add(column);
            section.Nodes.Add(row);
            composition.Nodes.Add(section);
            composition.Nodes.Add(new ComponentNode { Key = "k-hidden-banner", Name = "Hidden banner", Type = "Banner", LayoutType = "section", Component = new SharedElement(52, AccessLevel.NoAccess) }
                .WithStyles("vbBanner", new Dictionary<string, string> { ["tone"] = "loud" }));
            composition.Nodes.Add(SharedSectionNode("k-hidden-section", "Hidden section", new SharedSection(53, AccessLevel.NoAccess), "Hidden text")
                .WithStyles("vbSection", new Dictionary<string, string> { ["background"] = "dark" }));
            composition.Nodes.Add(SharedSectionNode("k-shared-section", "Shared section", new SharedSection(54, AccessLevel.FullAccess), "Shared text"));
            return composition;
        }

        private static SectionNode SharedSectionNode(string key, string name, SharedSection content, string heading)
        {
            var node = new SectionNode { Key = key, Name = name, Type = "SharedSection", SectionData = content };
            var row = new StructureNode { Key = key + "-row", NodeType = "row" };
            var column = new StructureNode { Key = key + "-column", NodeType = "column" };
            column.Nodes.Add(new ComponentNode { Key = key + "-element", Name = "Text", Type = "FieldsElement", Component = FieldsElement.Create(heading) });
            row.Nodes.Add(column);
            node.Nodes.Add(row);
            return node;
        }

        public SectionNode ToSectionComposition(SectionData section, bool filterForPublished) => throw new NotSupportedException();

        public void PopulateSection(SectionData section, SectionNode composition) => throw new NotSupportedException();

        public void Populate(ExperienceData page, Composition composition) => throw new NotSupportedException();

        /// <summary>The shared content by id, as the CMS's loader finds it.</summary>
        public IContent? Load(ContentReference link) => ToComposition(Experience, false).Nodes.SelectMany(Flatten)
            .Select(n => n switch { ComponentNode c => c.Component, SectionNode s => s.SectionData, _ => null })
            .OfType<IContent>().FirstOrDefault(c => c.ContentLink.ID == link.ID);

        private static IEnumerable<CompositionNode> Flatten(CompositionNode node) =>
            node is StructureNode structure ? [node, .. structure.Nodes.SelectMany(Flatten)] : [node];
    }

    public class SecuredExperience(AccessLevel granted) : ExperienceData, ISecurityDescriptor
    {
        public override ContentReference ContentLink { get; set; } = new(40);

        public override ISecurityDescriptor GetSecurityDescriptor() => this;

        public bool HasAccess(IPrincipal principal, AccessLevel access) => (granted & access) == access;

        public AccessLevel GetAccessLevel(IPrincipal principal) => granted;
    }

    public class Section : SectionData;

    /// <summary>An element with a property the edit UI doesn't show and one on a tab that needs Administer.</summary>
    public class FieldsElement : BlockData
    {
        public virtual string? Heading { get; set; }

        [ScaffoldColumn(false)]
        public virtual string? Scaffolded { get; set; }

        public virtual string? AdminTabbed { get; set; }

        public static FieldsElement Create(string heading)
        {
            var block = new FieldsElement();
            block.Property.Add(nameof(Heading), new PropertyString(heading) { PropertyDefinitionID = 1 });
            block.Property.Add(nameof(Scaffolded), new PropertyString("scaffolded") { PropertyDefinitionID = 2 });
            block.Property.Add(nameof(AdminTabbed), new PropertyString("tabbed") { PropertyDefinitionID = 3, OwnerTab = AdminTab });
            return block;
        }
    }

    /// <summary>A shared block: content of its own, whose access rights give everyone exactly <paramref name="granted"/>.</summary>
    public class SharedElement(int id, AccessLevel granted) : BlockData, IContent, ISecurable, ISecurityDescriptor
    {
        public string Name { get; set; } = "Hidden shared";

        public ContentReference ContentLink { get; set; } = new(id);

        public ContentReference ParentLink { get; set; } = ContentReference.EmptyReference;

        public Guid ContentGuid { get; set; } = Guid.NewGuid();

        public bool IsDeleted { get; set; }

        public ISecurityDescriptor GetSecurityDescriptor() => this;

        public bool HasAccess(IPrincipal principal, AccessLevel access) => (granted & access) == access;

        public AccessLevel GetAccessLevel(IPrincipal principal) => granted;
    }

    public class SharedSection(int id, AccessLevel granted) : SectionData, IContent, ISecurable, ISecurityDescriptor
    {
        public string Name { get; set; } = "Hidden shared section";

        public ContentReference ContentLink { get; set; } = new(id);

        public ContentReference ParentLink { get; set; } = ContentReference.EmptyReference;

        public Guid ContentGuid { get; set; } = Guid.NewGuid();

        public bool IsDeleted { get; set; }

        public ISecurityDescriptor GetSecurityDescriptor() => this;

        public bool HasAccess(IPrincipal principal, AccessLevel access) => (granted & access) == access;

        public AccessLevel GetAccessLevel(IPrincipal principal) => granted;
    }

    /// <summary>Nodes compared by their shape: every field but the nested ones, which a test checks itself.</summary>
    private sealed class NodeComparer : IEqualityComparer<ContentItemCompositionNode>
    {
        public bool Equals(ContentItemCompositionNode? x, ContentItemCompositionNode? y) =>
            JsonSerializer.Serialize(x, AgentJson.Options) == JsonSerializer.Serialize(y, AgentJson.Options);

        public int GetHashCode(ContentItemCompositionNode obj) => 0;
    }
#endif

    /// <summary>A content type for each model, named as its class.</summary>
    private static IContentTypeRepository Types()
    {
        var types = Recorder<IContentTypeRepository>.Create();
        types.Recorder.Answer = (method, args) => method.Name == nameof(IContentTypeRepository.Load) && args is [Type model] ? new ContentType { Name = model.Name } : null;
        return types.Proxy;
    }

    private static CmsCall Call(CmsCaller caller, object? fixture = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(Types())
            .AddSingleton(EditUiPropertiesTests.Tabs(new TabDefinition { ID = AdminTab, Name = "Admin", RequiredAccess = AccessLevel.Administer }))
            .AddSingleton<IPrincipalAccessor>(new CmsCallTests.PrincipalAccessor(new GenericPrincipal(new GenericIdentity("editor@example.com"), [])));
#if CMS13
        if (fixture is Fixture compositions)
        {
            services.AddSingleton<ICompositionMapper>(compositions);
            var loader = Recorder<IContentLoader>.Create();
            loader.Recorder.Answer = (method, args) =>
            {
                if (method.Name == nameof(IContentLoader.TryGet) && args is [ContentReference link, _] && compositions.Load(link) is { } content)
                {
                    args[1] = content;
                    return true;
                }
                return null;
            };
            services.AddSingleton(loader.Proxy);
        }
#endif
        return new CmsCall(services.BuildServiceProvider(), CancellationToken.None, caller);
    }
}
