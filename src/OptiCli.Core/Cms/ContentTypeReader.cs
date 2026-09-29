using Microsoft.Data.SqlClient;
using OptiCli.Core.Data;

namespace OptiCli.Core.Cms;

public static class ContentTypeReader
{
    private const string TypesSql = """
        SELECT ct.pkID, ct.ContentTypeGUID, ct.Name, ct.DisplayName, ct.Description, ct.ContentType, ct.Base,
               ct.ModelType, ISNULL(n.Instances, 0) AS Instances
        FROM tblContentType ct
        LEFT JOIN (
            SELECT fkContentTypeID, COUNT(*) AS Instances
            FROM tblContent
            WHERE Deleted = 0
            GROUP BY fkContentTypeID
        ) n ON n.fkContentTypeID = ct.pkID
        ORDER BY ct.Name
        """;

    private const string PropertiesSql = """
        SELECT pd.pkID, pd.Name, pdt.Name AS DataType, bt.Name AS BlockType, pd.IsList, pd.LanguageSpecific,
               pd.Required, g.Name AS Tab, pd.FieldOrder, pd.EditCaption, pd.ExistsOnModel
        FROM tblPropertyDefinition pd
        LEFT JOIN tblPropertyDefinitionType pdt ON pdt.pkID = pd.fkPropertyDefinitionTypeID
        LEFT JOIN tblContentType bt ON bt.ContentTypeGUID = pdt.fkContentTypeGUID
        LEFT JOIN tblPropertyDefinitionGroup g ON g.pkID = pd.Advanced
        WHERE pd.fkContentTypeID = @typeId
        ORDER BY pd.Name
        """;

    /// <summary><c>tblPropertyDefinition.LanguageSpecific</c> value for culture-specific properties.</summary>
    private const int CultureSpecificFlag = 4;

    public static Task<IReadOnlyList<ContentTypeInfo>> ListAsync(CmsDatabase db, CancellationToken cancellationToken) =>
        db.QueryAsync(TypesSql, r => new ContentTypeInfo(
            r.GetInt32("pkID"),
            r.GetGuid("ContentTypeGUID"),
            r.GetString("Name"),
            r.GetStringOrNull("DisplayName"),
            r.GetStringOrNull("Description"),
            ContentKinds.From(r.GetInt32("ContentType"), r.GetStringOrNull("Base")),
            r.GetStringOrNull("Base"),
            r.GetStringOrNull("ModelType"),
            r.GetInt32("Instances")), cancellationToken);

    public static Task<IReadOnlyList<PropertyDefinitionInfo>> ListPropertiesAsync(CmsDatabase db, int contentTypeId, CancellationToken cancellationToken) =>
        db.QueryAsync(PropertiesSql, r => new PropertyDefinitionInfo(
            r.GetInt32("pkID"),
            r.GetString("Name"),
            r.GetStringOrNull("DataType"),
            r.GetStringOrNull("BlockType"),
            r.GetBooleanOrNull("IsList") ?? false,
            r.GetInt32("LanguageSpecific") == CultureSpecificFlag,
            r.GetBooleanOrNull("Required"),
            r.GetStringOrNull("Tab"),
            r.GetInt32OrNull("FieldOrder"),
            r.GetStringOrNull("EditCaption"),
            r.GetBooleanOrNull("ExistsOnModel") ?? false), cancellationToken,
            new SqlParameter("@typeId", contentTypeId));
}
