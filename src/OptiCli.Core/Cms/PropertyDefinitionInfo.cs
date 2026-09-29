namespace OptiCli.Core.Cms;

/// <summary>
/// One row of <c>tblPropertyDefinition</c> with its type resolved.
/// </summary>
/// <remarks>
/// In CMS 12 the code model is the source of truth for tab, order and required; the DB columns are
/// only filled when an admin overrides them in the UI, so null here usually means "see the C# attribute".
/// </remarks>
/// <param name="DataType">Name from <c>tblPropertyDefinitionType</c> (String, XhtmlString, ContentArea, ...).</param>
/// <param name="BlockType">For block-typed properties, the block's content type name.</param>
/// <param name="Tab">Admin-overridden tab (<c>tblPropertyDefinitionGroup.Name</c>), if any.</param>
/// <param name="FieldOrder">Admin-overridden sort order, if any.</param>
/// <param name="Required">Admin-overridden required flag, if any.</param>
public sealed record PropertyDefinitionInfo(
    int Id,
    string Name,
    string? DataType,
    string? BlockType,
    bool IsList,
    bool CultureSpecific,
    bool? Required,
    string? Tab,
    int? FieldOrder,
    string? EditCaption,
    bool ExistsOnModel);
