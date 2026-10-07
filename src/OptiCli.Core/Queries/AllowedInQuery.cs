using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Properties;
using OptiCli.Core.SourceScan;

namespace OptiCli.Core.Queries;

/// <summary>One property that can hold the target type.</summary>
/// <param name="Allowed"><c>explicit</c>: its <c>[AllowedTypes]</c> names the type or one of its bases (<paramref name="MatchedBy"/>);
/// <c>any</c>: a ContentArea or reference list with no <c>[AllowedTypes]</c> (or only restrictions that don't name the type);
/// CMS 13 <c>composition</c>: a Visual Builder experience's outline (the type is <c>SectionEnabled</c>) or a section's columns
/// (<c>ElementEnabled</c>), the composition behaviour being <paramref name="MatchedBy"/>; <paramref name="Property"/> is then
/// <c>composition</c>.</param>
/// <param name="UiHint">A <c>[UIHint]</c> on the property: an editor descriptor for it may change the allowed types at runtime.</param>
/// <param name="Source">Where the property is declared (<c>path:line</c>, relative to the source root).</param>
public sealed record AllowedIn(
    string Type, ContentKind Kind, string Property, string PropertyType, string Allowed, string? MatchedBy,
    IReadOnlyList<string>? AllowedTypes, string? UiHint, string? Source);

/// <summary>
/// <c>allowed-in</c>: the ContentArea and reference properties whose <c>[AllowedTypes]</c> (read from the C#
/// sources) let them hold a given content type. The CMS keeps these rules in code only.
/// </summary>
public static class AllowedInQuery
{
    /// <summary>The CMS base classes every type of a kind derives from, as <c>[AllowedTypes]</c> may name them.</summary>
    private static readonly Dictionary<ContentKind, string[]> KindBases = new()
    {
        [ContentKind.Page] = ["PageData"],
        [ContentKind.Experience] = ["ExperienceData", "PageData"],
        [ContentKind.Block] = ["BlockData"],
        [ContentKind.Section] = ["SectionData", "BlockData"],
        [ContentKind.Element] = ["BlockData"],
        [ContentKind.Media] = ["MediaData", "IContentMedia"],
        [ContentKind.Folder] = ["ContentFolder"],
    };

    /// <summary>
    /// The CMS media classes below <c>MediaData</c>, by <c>tblContentType.Base</c>: the CMS records there whether a media
    /// type derives from <c>ImageData</c> or <c>VideoData</c>, so this holds for types from packages too.
    /// </summary>
    private static readonly Dictionary<string, string[]> MediaBases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Image"] = ["ImageData", "IContentImage"],
        ["Video"] = ["VideoData", "IContentVideo"],
    };

    private static readonly string[] ContentBases = ["IContent", "IContentData", "ContentData"];

    public static IReadOnlyList<AllowedIn> Find(CmsModel model, CSharpSourceIndex index, string sourceRoot, ContentTypeInfo target, bool explicitOnly)
    {
        var names = TargetNames(index, target);
        var result = new List<AllowedIn>();
        foreach (var owner in model.Types)
        {
            // CMS 13: a composition's items are placed by composition behaviour (below), not by the ContentArea they are stored in.
            var storage = Compositions.StorageProperties(model, owner.Id);
            var properties = model.PropertiesOf(owner.Id).Where(IsReference).Where(p => !storage.Contains(p.Name, StringComparer.Ordinal)).ToList();
            if (properties.Count == 0)
            {
                continue;
            }
            var classes = ContentTypeSources.FindClasses(index, owner.Guid, owner.ClassName ?? owner.Name, owner.Namespace);
            var code = classes.Count > 0 ? ContentTypeSources.FindProperties(index, classes[0].Class) : new Dictionary<string, PropertySource>();

            foreach (var property in properties)
            {
                var source = code.GetValueOrDefault(property.Name);
                var declaration = source?.AllowedTypes;
                if (declaration is not null && declaration.Restricted.Any(names.Contains))
                {
                    continue;
                }
                var match = declaration?.Allowed.FirstOrDefault(names.Contains);
                var allowed = match is not null ? "explicit" : declaration is null || declaration.Allowed.Count == 0 ? "any" : null;
                // A single reference without [AllowedTypes] takes any content (images, pages, ...): only list it when the
                // attribute names the type, or the rows drown in unrelated reference properties.
                if (allowed is null || (allowed == "any" && (explicitOnly || !IsList(property))))
                {
                    continue;
                }
                result.Add(new AllowedIn(
                    owner.Name, owner.Kind, property.Name, property.TypeName, allowed, match,
                    declaration is { Allowed.Count: > 0 } ? declaration.Allowed : null,
                    source?.UiHint,
                    source is null ? null : $"{Path.GetRelativePath(sourceRoot, source.File)}:{source.Line}"));
            }
        }
        result.AddRange(CompositionRows(model, target));
        return result
            .OrderBy(a => a.Allowed == "explicit" ? 0 : a.Allowed == "composition" ? 1 : 2)
            .ThenBy(a => a.Type, StringComparer.Ordinal)
            .ThenBy(a => a.Property, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Whether <paramref name="owner"/>'s reference property takes <paramref name="target"/>: false only when its
    /// <c>[AllowedTypes]</c> in the code leaves the type out (or restricts it); true without the attribute.
    /// </summary>
    /// <returns>Null when the property isn't a ContentArea or reference.</returns>
    public static bool? Allows(CmsModel model, CSharpSourceIndex index, ContentTypeInfo owner, string property, ContentTypeInfo target)
    {
        var definition = model.PropertiesOf(owner.Id).FirstOrDefault(p => p.Name.Equals(property, StringComparison.OrdinalIgnoreCase));
        if (definition is null || !IsReference(definition))
        {
            return null;
        }
        var classes = ContentTypeSources.FindClasses(index, owner.Guid, owner.ClassName ?? owner.Name, owner.Namespace);
        var declaration = classes.Count > 0 ? ContentTypeSources.FindProperties(index, classes[0].Class).GetValueOrDefault(definition.Name)?.AllowedTypes : null;
        if (declaration is null)
        {
            return true;
        }
        var names = TargetNames(index, target);
        return !declaration.Restricted.Any(names.Contains) && (declaration.Allowed.Count == 0 || declaration.Allowed.Any(names.Contains));
    }

    /// <summary>
    /// CMS 13: where a Visual Builder composition takes the type, as the CMS validates a composition: a <c>SectionEnabled</c>
    /// type in every experience's outline, an <c>ElementEnabled</c> one in every section's columns.
    /// </summary>
    private static IEnumerable<AllowedIn> CompositionRows(CmsModel model, ContentTypeInfo target)
    {
        foreach (var (behavior, ownerKind) in new[] { (ContentKinds.SectionEnabled, ContentKind.Experience), (ContentKinds.ElementEnabled, ContentKind.Section) })
        {
            if (!target.CompositionBehaviors.Contains(behavior, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (var owner in model.Types.Where(t => t.Kind == ownerKind && Compositions.IsLayouted(model, t.Id)).OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                yield return new AllowedIn(owner.Name, owner.Kind, Compositions.Field, "Composition", "composition", behavior, null, null, null);
            }
        }
    }

    private static bool IsList(PropertyDefinition property) => property.TypeName is "ContentArea" or "ContentReferenceList";

    public static bool IsReference(PropertyDefinition property) =>
        property.TypeName is "ContentArea" or "ContentReferenceList"
        || property.BaseType is PropertyBaseType.ContentReference or PropertyBaseType.PageReference;

    /// <summary>
    /// The target's type and class name, its base classes and interfaces found in the sources, its kind's CMS bases and,
    /// for media, the CMS media class its <c>Base</c> names.
    /// </summary>
    private static HashSet<string> TargetNames(CSharpSourceIndex index, ContentTypeInfo target)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { target.Name, target.ClassName ?? target.Name };
        names.UnionWith(KindBases.GetValueOrDefault(target.Kind, []));
        if (target.Kind == ContentKind.Media && target.Base is not null)
        {
            names.UnionWith(MediaBases.GetValueOrDefault(target.Base, []));
        }
        names.UnionWith(ContentBases);

        var pending = new Queue<ClassDeclaration>(ContentTypeSources.FindClasses(index, target.Guid, target.ClassName ?? target.Name, target.Namespace).Select(c => c.Class));
        while (pending.Count > 0)
        {
            foreach (var baseName in pending.Dequeue().BaseTypes)
            {
                if (names.Add(baseName))
                {
                    foreach (var declaration in index.Classes.Where(c => c.Name == baseName))
                    {
                        pending.Enqueue(declaration);
                    }
                }
            }
        }
        return names;
    }
}
