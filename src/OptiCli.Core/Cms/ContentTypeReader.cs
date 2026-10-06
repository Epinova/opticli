using Microsoft.Data.SqlClient;
using OptiCli.Core.Data;

namespace OptiCli.Core.Cms;

public static class ContentTypeReader
{
    private const string TypesSql = """
        SELECT ct.pkID, ct.ContentTypeGUID, ct.Name, ct.DisplayName, ct.Description, ct.ContentType, ct.Base,
               ct.ModelType, NULL AS Instances
        FROM tblContentType ct
        ORDER BY ct.Name
        """;

    // A scan of tblContent: only for the commands that show the counts, not for every model load.
    private const string TypesWithInstancesSql = """
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

    // Only for the properties the model sync left in the database: counting every property's values would scan the
    // property tables for each one.
    private const string OrphanValuesSql = """
        SELECT pd.pkID,
               (SELECT COUNT(*) FROM tblContentProperty cp WHERE cp.fkPropertyDefinitionID = pd.pkID) AS ContentValues,
               (SELECT COUNT(*) FROM tblWorkContentProperty wp WHERE wp.fkPropertyDefinitionID = pd.pkID) AS VersionValues
        FROM tblPropertyDefinition pd
        WHERE pd.fkContentTypeID = @typeId AND pd.ExistsOnModel = 0
        """;

    /// <summary><c>tblPropertyDefinition.LanguageSpecific</c> value for culture-specific properties.</summary>
    private const int CultureSpecificFlag = 4;

    /// <param name="countInstances">Also count each type's content (<see cref="ContentTypeInfo.Instances"/>).</param>
    public static Task<IReadOnlyList<ContentTypeInfo>> ListAsync(CmsDatabase db, CancellationToken cancellationToken, bool countInstances = false) =>
        db.QueryAsync(countInstances ? TypesWithInstancesSql : TypesSql, r => new ContentTypeInfo(
            r.GetInt32("pkID"),
            r.GetGuid("ContentTypeGUID"),
            r.GetString("Name"),
            r.GetStringOrNull("DisplayName"),
            r.GetStringOrNull("Description"),
            ContentKinds.From(r.GetInt32("ContentType"), r.GetStringOrNull("Base")),
            r.GetStringOrNull("Base"),
            r.GetStringOrNull("ModelType"),
            r.GetInt32OrNull("Instances")), cancellationToken);

    /// <summary>
    /// For each property of the type that isn't in its code (<c>ExistsOnModel = 0</c>): how many values are stored, on
    /// content (<c>tblContentProperty</c>: what is published, or the draft where nothing is) and in versions
    /// (<c>tblWorkContentProperty</c>). The model sync deletes a property removed from code only when it has none.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, (int Content, int Versions)>> OrphanValuesAsync(CmsDatabase db, int contentTypeId, CancellationToken cancellationToken) =>
        (await db.QueryAsync(OrphanValuesSql, r => (Id: r.GetInt32("pkID"), Content: r.GetInt32("ContentValues"), Versions: r.GetInt32("VersionValues")), cancellationToken,
            new SqlParameter("@typeId", contentTypeId)))
        .ToDictionary(r => r.Id, r => (r.Content, r.Versions));

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
