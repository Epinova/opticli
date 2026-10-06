using OptiCli.Core.SourceScan;
using OptiCli.Protocol;

namespace OptiCli.Core.Cms;

/// <summary>
/// <c>types --orphaned</c>: content types the CMS keeps after their class was removed from code. The model sync deletes a
/// code-defined type whose class is gone only when nothing uses it, so what stays has content (or is a block type of a
/// property, or has a rename pending). A type is code-defined when <c>tblContentType.ModelType</c> names its class; types
/// made in admin mode have none and never count.
/// </summary>
public static class OrphanedTypes
{
    /// <summary>Through the running site: the types whose class it can't load (<see cref="AgentRoutes.TypesWithoutCode"/>).</summary>
    public static IReadOnlyList<ContentTypeInfo> FromSite(IReadOnlyList<ContentTypeInfo> types, TypesWithoutCodeResult site)
    {
        var missing = site.Types.Select(t => t.Guid).ToHashSet();
        return types.Where(t => missing.Contains(t.Guid)).ToList();
    }

    /// <summary>
    /// Without the site: code-defined types of the solution's own assemblies (<paramref name="ownAssemblies"/>) whose
    /// class the source scan doesn't find, by the attribute's GUID and then by name, as <c>type</c> finds them. Types of
    /// other assemblies (the CMS's, add-ons') are left out: their classes aren't in the source.
    /// </summary>
    public static IReadOnlyList<ContentTypeInfo> FromSource(IReadOnlyList<ContentTypeInfo> types, CSharpSourceIndex index, IReadOnlySet<string> ownAssemblies) =>
        types.Where(t => Assembly(t.ModelType) is { } assembly && ownAssemblies.Contains(assembly)
                && ContentTypeSources.FindClasses(index, t.Guid, t.ClassName ?? t.Name, t.Namespace).Count == 0)
            .ToList();

    /// <summary>The assembly name in an assembly-qualified type name (<c>Site.Models.NewsPage, Site, Version=...</c>).</summary>
    public static string? Assembly(string? modelType)
    {
        if (string.IsNullOrWhiteSpace(modelType))
        {
            return null;
        }
        // Generic arguments are in brackets; the assembly is the part after the first comma outside them.
        var depth = 0;
        for (var i = 0; i < modelType.Length; i++)
        {
            switch (modelType[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    var rest = modelType[(i + 1)..];
                    var end = rest.IndexOf(',');
                    var name = (end >= 0 ? rest[..end] : rest).Trim();
                    return name.Length > 0 ? name : null;
            }
        }
        return null;
    }
}
