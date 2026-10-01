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
    /// <summary>The simple class name from <see cref="ModelType"/> (namespace, assembly and nesting stripped).</summary>
    public string? ClassName => ModelTypeNames.ClassName(ModelType);

    /// <summary>The namespace from <see cref="ModelType"/>, or null for global or missing types.</summary>
    public string? Namespace => ModelTypeNames.Namespace(ModelType);
}
