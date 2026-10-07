namespace OptiCli.Core.Cms;

/// <param name="Base">Raw <c>tblContentType.Base</c> (Page, Block, Image, Setting, ...).</param>
/// <param name="ModelType">Assembly-qualified CLR type name, when the type is defined in code.</param>
/// <param name="Instances">Content items of this type that are not deleted; null unless they were counted.</param>
public sealed record ContentTypeInfo(
    int Id,
    Guid Guid,
    string Name,
    string? DisplayName,
    string? Description,
    ContentKind Kind,
    string? Base,
    string? ModelType,
    int? Instances)
{
    /// <summary>
    /// CMS 13: the version of the assembly the model sync made the type from (<c>tblContentType.Version</c>), the only sign
    /// of code CMS 13 records for a class with a GUID; null when unset, and not read on CMS 12.
    /// </summary>
    public string? SyncedVersion { get; init; }

    /// <summary>CMS 13: the external content source the type belongs to (<c>tblContentType.Source</c>); null for the site's own.</summary>
    public string? Source { get; init; }

    /// <summary>CMS 13: the Visual Builder composition behaviours (<c>SectionEnabled</c>, <c>ElementEnabled</c>); empty otherwise.</summary>
    public IReadOnlyList<string> CompositionBehaviors { get; init; } = [];

    /// <summary>CMS 13: the contracts (interfaces) the type implements, by name.</summary>
    public IReadOnlyList<string> Contracts { get; init; } = [];

    /// <summary>
    /// CMS 13, set by <c>types --orphaned</c>: no class and no model-sync version on record, and no class of the site has its
    /// GUID (made in admin mode, or a code type a content import overwrote).
    /// </summary>
    public bool? OriginUnknown { get; init; }

    /// <summary>CMS 13, when counted: the type's Visual Builder blueprints, which <see cref="Instances"/> leaves out; null for none.</summary>
    public int? Blueprints { get; init; }

    /// <summary>
    /// CMS 13, when counted: inline blocks of the type in each branch's primary version (Visual Builder sections and elements,
    /// mostly), which <see cref="Instances"/> leaves out as they aren't content of their own; null for none.
    /// </summary>
    public int? InlineUses { get; init; }

    /// <summary>The simple class name from <see cref="ModelType"/> (namespace, assembly and nesting stripped).</summary>
    public string? ClassName => ModelTypeNames.ClassName(ModelType);

    /// <summary>The namespace from <see cref="ModelType"/>, or null for global or missing types.</summary>
    public string? Namespace => ModelTypeNames.Namespace(ModelType);
}
