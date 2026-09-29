namespace OptiCli.Core.Content;

/// <summary>
/// A <c>tblPropertyDefinition</c> row joined with its <c>tblPropertyDefinitionType</c>, which is all the
/// decoder needs to turn a stored value into a typed one.
/// </summary>
/// <param name="TypeName">Type name as the CMS knows it (String, XhtmlString, ContentArea, a block type name, ...).</param>
/// <param name="BaseType">
/// <c>tblPropertyDefinitionType.Property</c>: which storage column and base class the value uses
/// (see <see cref="PropertyBaseType"/>).
/// </param>
/// <param name="BlockType">For block-typed properties, the block's content type id.</param>
/// <param name="CultureSpecific">Stored per language branch rather than once on the master branch.</param>
public sealed record PropertyDefinition(
    int Id,
    int ContentTypeId,
    string Name,
    string TypeName,
    PropertyBaseType BaseType,
    int? BlockType,
    bool CultureSpecific,
    bool IsList);

/// <summary>Values of <c>tblPropertyDefinitionType.Property</c> (EPiServer.Core.PropertyDataType).</summary>
public enum PropertyBaseType
{
    Boolean = 0,
    Number = 1,
    FloatNumber = 2,
    PageType = 3,
    PageReference = 4,
    Date = 5,
    String = 6,
    LongString = 7,
    Category = 8,
    LinkCollection = 10,
    ContentReference = 11,
    Block = 12,

    /// <summary>Property lists and other values serialised as JSON.</summary>
    Json = 13,
}
