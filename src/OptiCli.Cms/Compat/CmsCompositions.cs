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

    /// <summary>The composition the CMS reads for an experience or a section (shared blocks loaded whether published or not); null for other content.</summary>
    /// <param name="properties">Renders a block's properties.</param>
    public static ContentItemCompositionNode? Read(IServiceProvider services, IContent content, Func<IContentData, IReadOnlyDictionary<string, ContentItemProperty>> properties)
    {
#if CMS13
        var mapper = services.GetRequiredService<ICompositionMapper>();
        CompositionNode? root = content switch
        {
            ExperienceData experience => mapper.ToComposition(experience, false),
            SectionData section => mapper.ToSectionComposition(section, false),
            _ => null,
        };
        return root is null ? null : Node(root, properties);
#else
        _ = (services, content, properties);
        return null;
#endif
    }

#if CMS13
    private static ContentItemCompositionNode Node(CompositionNode node, Func<IContentData, IReadOnlyDictionary<string, ContentItemProperty>> properties)
    {
        var block = node switch
        {
            ComponentNode component => component.Component,
            SectionNode section when node is not Composition => section.SectionData,
            _ => null,
        };
        var shared = block is IContent { ContentLink: var link } && !ContentReference.IsNullOrEmpty(link) ? link.ToReferenceWithoutVersion().ToString() : null;
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
            Nodes = node is StructureNode structure ? structure.Nodes.Select(n => Node(n, properties)).ToList() : null,
        };
    }
#endif
}
