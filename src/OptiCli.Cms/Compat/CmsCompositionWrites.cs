using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Cms.Content;
using OptiCli.Cms.Operations;
using OptiCli.Protocol;
#if CMS13
using EPiServer.Data.Entity;
using EPiServer.Security;
using EPiServer.ServiceLocation;
using EPiServer.VisualBuilder;
using EPiServer.VisualBuilder.Compositions;
#endif

namespace OptiCli.Cms.Compat;

/// <summary>
/// CMS 13's Visual Builder for writes: reads an experience's (or a section's) composition with the CMS's own
/// <c>ICompositionMapper</c>, lets <see cref="CompositionEditor"/> change it, and has the mapper store it again
/// (<c>Populate</c>), which rebuilds both storage properties (<c>Layout</c>, <c>UnstructuredData</c>) with the keys kept.
/// New inline blocks come from the CMS's <c>IBlockPropertyFactory</c>, their values are set as a draft's are
/// (<see cref="PropertyWriter"/>). CMS 12 has no compositions: a request with one is refused there (the MCP module, CMS 12
/// only, never sends one).
/// </summary>
internal static class CmsCompositionWrites
{
    /// <summary>
    /// Applies <paramref name="composition"/> (the whole composition) and then <paramref name="operations"/> to
    /// <paramref name="writable"/>, an experience or a section; nothing when both are null.
    /// </summary>
    /// <exception cref="AgentException"><c>usage</c> for content without a composition, or on CMS 12.</exception>
    public static void Apply(WriteFlow flow, IContentData writable, CompositionNodeValue? composition, IReadOnlyList<CompositionOperation>? operations)
    {
        if (composition is null && operations is not { Count: > 0 })
        {
            return;
        }
#if CMS13
        var mapper = flow.Call.Service<ICompositionMapper>();
        var blocks = new Blocks(flow);
        var type = flow.Types.Load(writable.ContentTypeID);
        DraftNode root;
        switch (writable)
        {
            case ExperienceData experience:
                var read = mapper.ToComposition(experience, false);
                root = new DraftNode
                {
                    NodeType = DraftNode.Experience,
                    Key = read?.Key,
                    DisplayTemplate = Empty(read?.DisplayTemplateKey),
                    DisplaySettings = Settings(read?.DisplaySettings),
                };
                root.Children.AddRange((read?.Nodes ?? []).Select(n => Draft(n, blocks)).OfType<DraftNode>());
                break;
            case SectionData section:
                var own = mapper.ToSectionComposition(section, false);
                root = new DraftNode
                {
                    NodeType = DraftNode.Section,
                    Key = own?.Key,
                    DisplayTemplate = Empty(own?.DisplayTemplateKey),
                    DisplaySettings = Settings(own?.DisplaySettings),
                };
                root.Children.AddRange((own?.Nodes ?? []).Select(n => Draft(n, blocks)).OfType<DraftNode>());
                break;
            default:
                throw AgentException.Usage($"{type?.Name ?? writable.GetOriginalType().Name} has no Visual Builder composition; only experiences and sections have one.",
                    "`opticli types --kind experience` lists the experience types.");
        }
        blocks.RootType = type;
        var editor = new CompositionEditor(root, blocks);
        if (composition is not null)
        {
            editor.Replace(composition);
        }
        editor.Apply(operations);

        if (writable is ExperienceData target)
        {
            var result = new Composition
            {
                Key = root.Key ?? (target as IContent)?.ContentGuid.ToString(),
                Name = (target as IContent)?.Name,
                Type = type?.Name,
                NodeType = "experience",
                LayoutType = "outline",
                ExperienceData = target,
            };
            Fill(result, root, blocks);
            mapper.Populate(target, result);
        }
        else
        {
            var section = (SectionData)writable;
            var result = new SectionNode
            {
                Key = root.Key ?? (section as IContent)?.ContentGuid.ToString(),
                Name = (section as IContent)?.Name,
                Type = type?.Name,
                NodeType = "section",
                LayoutType = "grid",
                SectionData = section,
            };
            Fill(result, root, blocks);
            mapper.PopulateSection(section, result);
        }
#else
        _ = (flow, writable);
        throw AgentException.Usage("Visual Builder compositions are CMS 13's; this site runs CMS 12.");
#endif
    }

    /// <summary>
    /// CMS 13: the content of the Visual Builder blueprint <paramref name="blueprint"/> (its content GUID), in
    /// <paramref name="language"/> (else its master language). Refused on CMS 12.
    /// </summary>
    /// <exception cref="AgentException"><c>not_found</c> when there is no such blueprint.</exception>
    public static IContent Blueprint(WriteFlow flow, Guid blueprint, System.Globalization.CultureInfo? language)
    {
#if CMS13
        var found = flow.Call.Service<IBlueprintRepository>().GetAsync(blueprint, new GetBlueprintOptions { RequiredAccess = AccessLevel.NoAccess }).GetAwaiter().GetResult()
            ?? throw AgentException.NotFound($"No blueprint {blueprint}.", "`opticli find --type <type> --blueprints` lists the blueprints of a type.");
        var link = found.Content.ContentLink.ToReferenceWithoutVersion();
        return language is not null && flow.Repository.TryGet<IContent>(link, language, out var inLanguage)
            ? inLanguage
            : flow.Repository.Get<IContent>(link, new LoaderOptions { LanguageLoaderOption.FallbackWithMaster() });
#else
        _ = (flow, blueprint, language);
        throw AgentException.Usage("Visual Builder blueprints are CMS 13's; this site runs CMS 12.");
#endif
    }

    /// <summary>
    /// Gives new content <paramref name="target"/> what the blueprint <paramref name="source"/> has, as a copy of it would:
    /// every property's value (cloned), and its composition through the CMS's mapper, with the blueprint's keys (each piece of
    /// content has its own). Not its name, page settings or other language branches.
    /// </summary>
    public static void CopyBlueprint(WriteFlow flow, IContent source, IContent target)
    {
#if CMS13
        foreach (var property in source.Property.Where(p => !p.IsMetaData && !CmsCompositions.IsStorage(source, p) && !p.IsNull))
        {
            if (target.Property[property.Name] is { IsMetaData: false } copy)
            {
                copy.Value = property.Value is IReadOnly readOnly ? readOnly.CreateWritableClone() : property.Value;
            }
        }
        var mapper = flow.Call.Service<ICompositionMapper>();
        var blocks = new Blocks(flow);
        StructureNode? composition = source switch
        {
            ExperienceData experience => mapper.ToComposition(experience, false),
            SectionData section => mapper.ToSectionComposition(section, false),
            _ => null,
        };
        if (composition is null)
        {
            return;
        }
        var draft = new DraftNode { NodeType = target is ExperienceData ? DraftNode.Experience : DraftNode.Section };
        draft.DisplayTemplate = Empty(composition.DisplayTemplateKey);
        draft.DisplaySettings = Settings(composition.DisplaySettings);
        draft.Children.AddRange(composition.Nodes.Select(n => Draft(n, blocks)).OfType<DraftNode>());
        if (target is ExperienceData experienceTarget)
        {
            var result = new Composition { Key = target.ContentGuid.ToString(), Name = target.Name, Type = composition.Type, NodeType = "experience", LayoutType = "outline", ExperienceData = experienceTarget };
            Fill(result, draft, blocks);
            mapper.Populate(experienceTarget, result);
        }
        else if (target is SectionData sectionTarget)
        {
            var result = new SectionNode { Key = target.ContentGuid.ToString(), Name = target.Name, Type = composition.Type, NodeType = "section", LayoutType = "grid", SectionData = sectionTarget };
            Fill(result, draft, blocks);
            mapper.PopulateSection(sectionTarget, result);
        }
#else
        _ = (flow, source, target);
        throw AgentException.Usage("Visual Builder blueprints are CMS 13's; this site runs CMS 12.");
#endif
    }

    /// <summary>
    /// The composition of an experience or a section as a snapshot value (the shape <see cref="CompositionNodeValue"/> takes,
    /// with each inline block's values as <paramref name="properties"/> renders them), so a write's diff shows it and its
    /// <c>after</c> can be sent back as <see cref="DraftRequest.Composition"/>. Null for other content, and on CMS 12.
    /// </summary>
    public static CompositionNodeValue? Snapshot(IContentData content, Func<IContentData, IReadOnlyDictionary<string, JsonElement?>> properties)
    {
#if CMS13
        // An inline section (no content of its own) is part of its owner's composition, which shows its nodes.
        if (content is not (ExperienceData or SectionData) || content is not IContent)
        {
            return null;
        }
        var mapper = EPiServer.ServiceLocation.ServiceLocator.Current.GetInstance<ICompositionMapper>();
        StructureNode? root = content switch
        {
            ExperienceData experience => mapper.ToComposition(experience, false),
            SectionData section => mapper.ToSectionComposition(section, false),
            _ => null,
        };
        return root is null ? null : Value(root, properties, isRoot: true);
#else
        _ = (content, properties);
        return null;
#endif
    }

#if CMS13
    private static CompositionNodeValue Value(CompositionNode node, Func<IContentData, IReadOnlyDictionary<string, JsonElement?>> properties, bool isRoot = false)
    {
        var block = node switch
        {
            ComponentNode component => component.Component,
            SectionNode section when !isRoot => section.SectionData,
            _ => null,
        };
        var shared = block is IContent { ContentLink: var link } && !ContentReference.IsNullOrEmpty(link) ? link.ToReferenceWithoutVersion().ToString() : null;
        var values = block is not null && shared is null
            // An inline section's own composition is its nodes, below.
            ? properties(block).Where(p => p.Value is not null && p.Key != PropertyValues.CompositionKey).ToDictionary(p => p.Key, p => p.Value!.Value, StringComparer.OrdinalIgnoreCase)
            : null;
        return new CompositionNodeValue
        {
            NodeType = isRoot ? null : node.NodeType,
            Key = isRoot ? null : node.Key,
            Name = isRoot ? null : node.Name,
            Type = isRoot ? null : node.Type,
            Ref = shared,
            DisplayTemplate = Empty(node.DisplayTemplateKey),
            DisplaySettings = node.DisplaySettings is { Count: > 0 } settings ? settings.ToDictionary(s => s.Key, s => (string?)s.Value, StringComparer.OrdinalIgnoreCase) : null,
            Properties = values is { Count: > 0 } ? values : null,
            // A shared section's rows are its own content's.
            Nodes = node is StructureNode structure && shared is null && structure.Nodes.Count > 0 ? structure.Nodes.Select(n => Value(n, properties)).ToList() : null,
        };
    }

    /// <summary>The CMS's node as an editable one; null for a node of a kind the editor doesn't know (kept out of the composition).</summary>
    private static DraftNode? Draft(CompositionNode node, Blocks blocks)
    {
        DraftNode? draft = node switch
        {
            SectionNode section => new DraftNode { NodeType = DraftNode.Section, Block = blocks.Handle(section.SectionData) },
            ComponentNode component => new DraftNode { NodeType = DraftNode.Component, Block = blocks.Handle(component.Component) },
            StructureNode { NodeType: var type } when type is DraftNode.Row or DraftNode.Column => new DraftNode { NodeType = type },
            _ => null,
        };
        if (draft is null)
        {
            return null;
        }
        draft.Key = node.Key;
        draft.Name = node.Name;
        draft.DisplayTemplate = Empty(node.DisplayTemplateKey);
        draft.DisplaySettings = Settings(node.DisplaySettings);
        // A shared section's rows are its own content's; they stay with it.
        if (node is StructureNode structure && draft.Block is not { Shared: true })
        {
            draft.Children.AddRange(structure.Nodes.Select(n => Draft(n, blocks)).OfType<DraftNode>());
        }
        return draft;
    }

    /// <summary>Puts the editor's children of <paramref name="draft"/> on the CMS's <paramref name="node"/>.</summary>
    private static void Fill(StructureNode node, DraftNode draft, Blocks blocks)
    {
        node.DisplayTemplateKey = draft.DisplayTemplate;
        node.DisplaySettings = new Dictionary<string, string>(draft.DisplaySettings, StringComparer.OrdinalIgnoreCase);
        node.Nodes = draft.Children.Select(child => Cms(child, blocks)).ToList();
    }

    private static CompositionNode Cms(DraftNode draft, Blocks blocks)
    {
        switch (draft.NodeType)
        {
            case DraftNode.Section:
                var section = new SectionNode
                {
                    Key = draft.Key,
                    Name = draft.Name,
                    Type = draft.Block!.Type,
                    NodeType = "section",
                    LayoutType = "grid",
                    // The mapper sets an inline section's layout and items on it.
                    SectionData = (SectionData)blocks.Writable(draft.Block).Block,
                };
                Fill(section, draft, blocks);
                return section;
            case DraftNode.Component:
                return new ComponentNode
                {
                    Key = draft.Key,
                    Name = draft.Name,
                    Type = draft.Block!.Type,
                    NodeType = "component",
                    LayoutType = "section",
                    Component = (BlockData)blocks.Writable(draft.Block).Block,
                    DisplayTemplateKey = draft.DisplayTemplate,
                    DisplaySettings = new Dictionary<string, string>(draft.DisplaySettings, StringComparer.OrdinalIgnoreCase),
                };
            default:
                var structure = new StructureNode { Key = draft.Key, Name = draft.Name, NodeType = draft.NodeType };
                Fill(structure, draft, blocks);
                return structure;
        }
    }

    private static string? Empty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static Dictionary<string, string> Settings(IDictionary<string, string>? settings) =>
        settings is null ? new(StringComparer.OrdinalIgnoreCase) : new(settings, StringComparer.OrdinalIgnoreCase);

    /// <summary>The CMS side of <see cref="ICompositionBlocks"/>.</summary>
    private sealed class Blocks(WriteFlow flow) : ICompositionBlocks
    {
        private readonly IBlockPropertyFactory _factory = flow.Call.Service<IBlockPropertyFactory>();

        private readonly IDisplayTemplateRepository _templates = flow.Call.Service<IDisplayTemplateRepository>();

        /// <summary>The type of the content whose composition it is, for the root's display template.</summary>
        public ContentType? RootType { get; set; }

        public CompositionBlock Handle(BlockData block)
        {
            var type = flow.Types.Load(block.ContentTypeID);
            var shared = block is IContent { ContentLink: var link } && !ContentReference.IsNullOrEmpty(link) ? link.ToReferenceWithoutVersion().ID.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return new CompositionBlock(
                block,
                type?.Name ?? block.GetOriginalType().Name,
                shared is not null,
                shared,
                block is SectionData,
                type?.CompositionBehaviors.Contains(CompositionBehavior.SectionEnabled) == true,
                type?.CompositionBehaviors.Contains(CompositionBehavior.ElementEnabled) == true);
        }

        public CompositionBlock New(string type, string where)
        {
            var found = TypeOperation.Find(flow.Call, flow.Types, type);
            if (!CmsApi.IsBlockType(found))
            {
                throw AgentException.Usage($"{where}: {found.Name} isn't a block type, so it can't be a section or element.",
                    "`opticli types --kind section` and `--kind element` list the types a composition takes.");
            }
            return Handle(_factory.Create(found.ID));
        }

        public CompositionBlock Shared(string reference, string where)
        {
            var link = flow.Locator.ResolveContent(reference, "shared block");
            var content = flow.Locator.LoadAnyLanguage(link);
            return content is BlockData block
                ? Handle(block)
                : throw AgentException.Usage($"{where}: {reference} is {flow.Types.Load(content.ContentTypeID)?.Name ?? content.GetOriginalType().Name} content, not a block.",
                    "A composition places sections and elements, inline or shared blocks.");
        }

        public (CompositionBlock Block, IReadOnlyList<DraftNode> Rows, string? DisplayTemplate, IReadOnlyDictionary<string, string> DisplaySettings) Blueprint(Guid blueprint, string where)
        {
            var found = flow.Call.Service<IBlueprintRepository>().GetAsync(blueprint, new GetBlueprintOptions { RequiredAccess = AccessLevel.NoAccess }).GetAwaiter().GetResult()
                ?? throw AgentException.NotFound($"{where}: no blueprint {blueprint}.", "`opticli find --type <section type> --blueprints` lists blueprints.");
            if (found.Content is not SectionData source)
            {
                throw AgentException.Usage($"{where}: the blueprint '{found.DisplayName}' is {flow.Types.Load(found.Content.ContentTypeID)?.Name}, not a section.",
                    "A section blueprint makes a section; content is made from an experience blueprint with create --blueprint.");
            }
            // A new inline section with the blueprint's own values; its grid comes from the blueprint's composition.
            var copy = _factory.Create(source.ContentTypeID);
            foreach (var property in source.Property.Where(p => !p.IsMetaData && !CmsCompositions.IsStorage(source, p)))
            {
                if (copy.Property[property.Name] is { } target)
                {
                    target.Value = property.Value is IReadOnly readOnly ? readOnly.CreateWritableClone() : property.Value;
                }
            }
            var composition = flow.Call.Service<ICompositionMapper>().ToSectionComposition(source, false);
            var rows = (composition?.Nodes ?? []).Select(n => Draft(n, this)).OfType<DraftNode>().ToList();
            return (Handle(copy), rows, Empty(composition?.DisplayTemplateKey), Settings(composition?.DisplaySettings));
        }

        public CompositionBlock Writable(CompositionBlock block) =>
            block.Shared || block.Block is not IReadOnly { IsReadOnly: true } readOnly
                ? block
                : block with { Block = readOnly.CreateWritableClone() };

        public void SetProperties(CompositionBlock block, IReadOnlyDictionary<string, JsonElement> properties, string where)
        {
            try
            {
                flow.Writer.Apply((IContentData)block.Block, properties);
            }
            catch (AgentException ex)
            {
                throw new AgentException(ex.Code, $"{where}: {ex.Message}", ex.Hint) { Validation = ex.Validation };
            }
        }

        public string DefaultName(CompositionBlock block) =>
            flow.Types.Load(((BlockData)block.Block).ContentTypeID) is { } type && !string.IsNullOrWhiteSpace(type.DisplayName) ? type.DisplayName : block.Type;

        /// <summary>The CMS's own rules (its <c>LayoutDisplaySettingsValidator</c>, which only warns), as errors with what the site has.</summary>
        public void CheckStyle(DraftNode node, string where)
        {
            var nodeType = node.NodeType;
            var contentType = node.Block is { } block ? flow.Types.Load(((BlockData)block.Block).ContentTypeID) : RootType;
            var what = $"{where}: {node.Describe()}";
            if (node.DisplayTemplate is not { } key)
            {
                if (node.DisplaySettings.Count > 0)
                {
                    throw AgentException.Usage($"{what} has display settings but no display template.", "Give the template the settings belong to (displayTemplate).");
                }
                return;
            }
            var fitting = _templates.List().Where(t => Fits(t, nodeType, contentType)).Select(t => t.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var choices = fitting.Count == 0 ? $"The site has no display template for a {Shown(nodeType)}{(contentType is null ? "" : $" of type {contentType.Name}")}." : $"Templates for it: {string.Join(", ", fitting)}.";
            var template = _templates.Load(key) ?? throw AgentException.Usage($"{what}: the site has no display template '{key}'.", $"{choices} `opticli display-templates` lists them.");
            if (!Fits(template, nodeType, contentType))
            {
                throw AgentException.Usage($"{what}: the display template '{template.Key}' is for {For(template)}, not for this {Shown(nodeType)}{(contentType is null ? "" : $" ({contentType.Name})")}.", choices);
            }
            foreach (var (name, value) in node.DisplaySettings)
            {
                var setting = template.Settings.FirstOrDefault(s => s.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw AgentException.Usage($"{what}: the display template '{template.Key}' has no setting '{name}'.",
                        template.Settings.Count == 0 ? "It has no settings." : $"Settings: {string.Join(", ", template.Settings.Select(s => s.Key))}.");
                if ("checkbox".Equals(setting.Editor, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(value) && !bool.TryParse(value, out _))
                    {
                        throw AgentException.Usage($"{what}: '{setting.Key}' of '{template.Key}' is a checkbox, which takes true or false, not '{value}'.");
                    }
                }
                else if (!string.IsNullOrEmpty(value) && !setting.Choices.Any(c => c.Key.Equals(value, StringComparison.OrdinalIgnoreCase)))
                {
                    throw AgentException.Usage($"{what}: '{value}' isn't a choice of '{setting.Key}' in '{template.Key}'.",
                        $"Choices: {string.Join(", ", setting.Choices.OrderBy(c => c.SortOrder).Select(c => c.Key))}.");
                }
            }
        }

        /// <summary>Whether the template may style the node, as the CMS's validator decides: node type, content type and base, each when it names one.</summary>
        private static bool Fits(DisplayTemplate template, string nodeType, ContentType? contentType) =>
            (string.IsNullOrEmpty(template.NodeType) || template.NodeType.Equals(nodeType, StringComparison.OrdinalIgnoreCase))
            && (template.ContentTypeID is not { } typeId || typeId == contentType?.ID)
            && (template.BaseType is not { } baseType || contentType is null || contentType.Base == baseType);

        private string For(DisplayTemplate template)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(template.NodeType))
            {
                parts.Add($"{Shown(template.NodeType)}s");
            }
            if (template.ContentTypeID is { } id)
            {
                parts.Add($"content type {flow.Types.Load(id)?.Name ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (template.BaseType is { } baseType)
            {
                parts.Add($"base {baseType}");
            }
            return parts.Count == 0 ? "any node" : string.Join(", ", parts);
        }

        private static string Shown(string nodeType) => nodeType == DraftNode.Component ? "element" : nodeType;
    }
#endif
}
