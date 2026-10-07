using OptiCli.Core.SourceScan;
using OptiCli.Protocol;

namespace OptiCli.Core.Cms;

/// <summary>
/// <c>types --orphaned</c>: content types the CMS keeps after their class was removed from code. The model sync deletes a
/// code-defined type whose class is gone only when nothing uses it, so what stays has content (or is a block type of a
/// property, or has a rename pending). A type is code-defined when <c>tblContentType.ModelType</c> names its class; types
/// made in admin mode have none and never count. CMS 13 records no class for a class with a GUID, only the version of its
/// assembly (<c>tblContentType.Version</c>), and a content import (every Alloy-template site's) leaves neither: such a type
/// whose GUID no class of the build has is listed too, as <see cref="ContentTypeInfo.OriginUnknown"/> when it has no version.
/// </summary>
public static class OrphanedTypes
{
    /// <summary>Through the running site: the types whose class it can't load (<see cref="AgentRoutes.TypesWithoutCode"/>).</summary>
    public static IReadOnlyList<ContentTypeInfo> FromSite(IReadOnlyList<ContentTypeInfo> types, TypesWithoutCodeResult site)
    {
        var missing = site.Types.ToDictionary(t => t.Guid);
        return types.Where(t => missing.ContainsKey(t.Guid))
            .Select(t => missing[t.Guid].OriginUnknown == true ? t with { OriginUnknown = true } : t)
            .ToList();
    }

    /// <summary>
    /// CMS 13, without the site: types with no class on record (<see cref="FromSource"/> can't place them) whose GUID no class
    /// in the site's build output has (<paramref name="buildGuids"/>, packages' classes included) and that the source scan
    /// doesn't find either (by GUID, then name). Those without a version are of unknown origin. The CMS's own types and an
    /// external content source's are left out.
    /// </summary>
    public static IReadOnlyList<ContentTypeInfo> WithoutClassOnRecord(IReadOnlyList<ContentTypeInfo> types, IReadOnlySet<Guid> buildGuids, CSharpSourceIndex index) =>
        types.Where(t => string.IsNullOrWhiteSpace(t.ModelType) && t.Source is null && !OrphanRemoval.SystemTypes.Contains(t.Name, StringComparer.Ordinal)
                && !buildGuids.Contains(t.Guid) && ContentTypeSources.FindClasses(index, t.Guid, t.Name, null).Count == 0)
            .Select(t => t.SyncedVersion is null ? t with { OriginUnknown = true } : t)
            .ToList();

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
