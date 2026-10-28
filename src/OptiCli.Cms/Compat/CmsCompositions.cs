using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Protocol;
#if CMS13
using EPiServer.VisualBuilder;
using EPiServer.VisualBuilder.Compositions;
using Microsoft.Extensions.DependencyInjection;
#endif

namespace OptiCli.Cms.Compat;

/// <summary>
/// CMS 13's Visual Builder for reads: an experience's or section's composition through the CMS's own
/// <c>ICompositionMapper</c>, the kinds of its content, and which properties hold a composition. CMS 12 has none of it,
/// so there everything is null or false (the MCP module compiles this branch).
/// </summary>
internal static class CmsCompositions
{
    /// <summary>The CMS 13 kind of content that is part of Visual Builder (<c>experience</c>, <c>section</c>, <c>element</c>); null otherwise.</summary>
    public static string? Kind(IContentData content, ContentType? type)
    {
#if CMS13
        return content switch
        {
            ExperienceData => "experience",
            SectionData => "section",
            BlockData when type?.CompositionBehaviors.Contains(CompositionBehavior.ElementEnabled) == true => "element",
            _ => null,
        };
#else
        _ = (content, type);
        return null;
#endif
    }

    /// <summary>Whether the property is where the content keeps its composition (<c>Layout</c>, <c>UnstructuredData</c>), shown as the composition instead.</summary>
    public static bool IsStorage(IContentData data, PropertyData property)
    {
#if CMS13
        // ILayoutedData is internal: experiences and sections are the classes that have it.
        return data is ExperienceData or SectionData && property.Name is nameof(ExperienceData.Layout) or nameof(ExperienceData.UnstructuredData);
#else
        _ = (data, property);
        return false;
#endif
    }

    /// <summary>
    /// The composition the CMS reads for an experience or a section (shared blocks loaded whether published or not); null
    /// for other content. For an editor it follows the rules of every other read: an inline block's properties are those
    /// the edit UI shows them (<paramref name="properties"/>, by the content the block is in), and shared content they
    /// can't read is only a place in the composition (<see cref="UnreadablePlace"/>), as a ContentArea shows such an item.
    /// </summary>
    /// <param name="properties">Renders a block's properties.</param>
    public static ContentItemCompositionNode? Read(CmsCall call, IContent content, Func<IContentData, IReadOnlyDictionary<string, ContentItemProperty>> properties)
    {
#if CMS13
        // The mapper loads shared blocks with IContentLoader, which checks no access rights: Node checks them.
        return Mapped(call, content) is { } root ? Node(root, call, content, null, properties) : null;
#else
        _ = (call, content, properties);
        return null;
#endif
    }

    /// <summary>
    /// The node type a composition shows for shared content the caller can't read, from its place alone: <c>section</c> in
    /// an experience's outline, <c>component</c> anywhere else. The CMS's own node type says whether the content is a
    /// section, which is the content's.
    /// </summary>
    /// <param name="parentNodeType">The node type of the node it is in.</param>
    public static string UnreadablePlace(string? parentNodeType) => parentNodeType == "experience" ? "section" : "component";

    /// <summary>
    /// Whether the display template and settings of a node for shared content the caller can't read are shown: only for a
    /// component in a column, where they are the composition's own. A shared section's are its own content's, and in an
    /// outline a section and a section-enabled block (a component) can't be told apart by the caller.
    /// </summary>
    public static bool UnreadableKeepsStyles(string? nodeType, string? parentNodeType) => nodeType == "component" && parentNodeType == "column";

    /// <summary>
    /// The inline blocks of the composition of <paramref name="content"/> (an experience or a section), by node key, as the
    /// CMS reads them now; not those inside shared sections, which are the shared content's own. Empty for other content
    /// and on CMS 12.
    /// </summary>
    public static IReadOnlyList<(string Key, BlockData Block)> InlineBlocks(CmsCall call, IContentData content)
    {
#if CMS13
        var root = content is IContent ? Mapped(call, content) : null;
        var blocks = new List<(string Key, BlockData Block)>();
        void Walk(CompositionNode node)
        {
            var block = node switch
            {
                ComponentNode component => component.Component,
                SectionNode section => section.SectionData,
                _ => null,
            };
            if (block is IContent { ContentLink: var link } && !ContentReference.IsNullOrEmpty(link))
            {
                return;
            }
            if (block is not null && node.Key is { } key)
            {
                blocks.Add((key, block));
            }
            if (node is StructureNode structure)
            {
                foreach (var child in structure.Nodes)
                {
                    Walk(child);
                }
            }
        }
        foreach (var node in root?.Nodes ?? [])
        {
            Walk(node);
        }
        return blocks;
#else
        _ = (call, content);
        return [];
#endif
    }

#if CMS13
    /// <summary>The CMS's composition of an experience or a section (shared blocks loaded whether published or not); null for other content.</summary>
    private static StructureNode? Mapped(CmsCall call, IContentData content) => content switch
    {
        ExperienceData experience => call.Service<ICompositionMapper>().ToComposition(experience, false),
        SectionData section => call.Service<ICompositionMapper>().ToSectionComposition(section, false),
        _ => null,
    };

    /// <param name="within">The content the node's inline blocks are in: the composition's own, or a shared section.</param>
    /// <param name="parentNodeType">The node type of the node this one is in; null at the root.</param>
    private static ContentItemCompositionNode Node(CompositionNode node, CmsCall call, IContent within, string? parentNodeType, Func<IContentData, IReadOnlyDictionary<string, ContentItemProperty>> properties)
    {
        var block = node switch
        {
            ComponentNode component => component.Component,
            SectionNode section when node is not Composition => section.SectionData,
            _ => null,
        };
        var sharedContent = block is IContent { ContentLink: var link } content && !ContentReference.IsNullOrEmpty(link) ? content : null;
        var shared = sharedContent?.ContentLink.ToReferenceWithoutVersion().ToString();
        if (sharedContent is not null && !call.CanRead(sharedContent))
        {
            return Unreadable(node, shared!, parentNodeType);
        }
        if (block is not null && sharedContent is null)
        {
            // An inline block is edited as part of the content it is in, which decides what of it the editor sees.
            call.Properties.Within(block, within);
        }
        return new ContentItemCompositionNode(node.NodeType)
        {
            Key = node.Key,
            Name = node.Name,
            Type = node.Type,
            LayoutType = node.LayoutType,
            DisplayTemplate = string.IsNullOrEmpty(node.DisplayTemplateKey) ? null : node.DisplayTemplateKey,
            DisplaySettings = node.DisplaySettings is { Count: > 0 } settings ? settings.ToDictionary(StringComparer.Ordinal) : null,
            Ref = shared,
            Properties = block is not null && shared is null ? properties(block) : null,
            Nodes = node is StructureNode structure ? structure.Nodes.Select(n => Node(n, call, sharedContent ?? within, node.NodeType, properties)).ToList() : null,
        };
    }

    /// <summary>
    /// Shared content the caller can't read, as a ContentArea shows such an item: that something is there, by its ref,
    /// and nothing of the content. Not its name in the composition (which can be the content's own), its type, its
    /// layout or the nodes of a shared section, nor the CMS's node type (<see cref="UnreadablePlace"/>); its display
    /// template and settings only where they are the composition's own (<see cref="UnreadableKeepsStyles"/>).
    /// </summary>
    private static ContentItemCompositionNode Unreadable(CompositionNode node, string shared, string? parentNodeType)
    {
        var styles = UnreadableKeepsStyles(node.NodeType, parentNodeType);
        return new ContentItemCompositionNode(UnreadablePlace(parentNodeType))
        {
            Key = node.Key,
            Ref = shared,
            DisplayTemplate = styles && !string.IsNullOrEmpty(node.DisplayTemplateKey) ? node.DisplayTemplateKey : null,
            DisplaySettings = styles && node.DisplaySettings is { Count: > 0 } settings ? settings.ToDictionary(StringComparer.Ordinal) : null,
        };
    }
#endif
}
