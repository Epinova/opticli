using Microsoft.Data.SqlClient;
using OptiCli.Core.Data;
using OptiCli.Protocol;

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
    /// For each property that isn't in its type's code (<c>ExistsOnModel = 0</c>): how many content items and versions hold
    /// a value of it, counted as the site agent counts before <c>types remove-property</c>
    /// (<see cref="OrphanRemoval.PropertyValuesSql"/>): what the CMS deletes with the property, values inside a block
    /// property and category selections included. Only for those properties: counting every property's values would scan
    /// the property tables for each one. The model sync deletes a property removed from code only when it has none.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, (int Content, int Versions)>> OrphanValuesAsync(CmsDatabase db, IEnumerable<PropertyDefinitionInfo> properties, CancellationToken cancellationToken)
    {
        var values = new Dictionary<int, (int, int)>();
        foreach (var property in properties.Where(p => !p.ExistsOnModel))
        {
            var row = await db.QueryAsync(OrphanRemoval.PropertyValuesSql(property.BlockType is not null), r => (r.GetInt32("Content"), r.GetInt32("Versions")), cancellationToken,
                new SqlParameter("@id", property.Id));
            values[property.Id] = row.Single();
        }
        return values;
    }

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
