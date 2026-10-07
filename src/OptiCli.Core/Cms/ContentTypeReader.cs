using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Protocol;

namespace OptiCli.Core.Cms;

public static class ContentTypeReader
{
    /// <summary>
    /// Every content type; with <paramref name="countInstances"/> also how many content items use each (a scan of
    /// <c>tblContent</c>: only for the commands that show the counts, not for every model load). CMS 13 also gives the
    /// Visual Builder facts (<see cref="CmsSchema.Compositions"/>); a blueprint (<c>tblContent.Blueprint</c>) isn't counted as
    /// an instance there.
    /// </summary>
    private static string TypesSql(CmsSchema schema, bool countInstances)
    {
        var composition = schema.Compositions ? ", ct.Version AS SyncedVersion, ct.Source, CONVERT(bit, ISNULL(ct.IsContract, 0)) AS IsContract, ct.CompositionBehavior" : "";
        if (!countInstances)
        {
            return $"""
                SELECT ct.pkID, ct.ContentTypeGUID, ct.Name, ct.DisplayName, ct.Description, ct.ContentType, ct.Base,
                       ct.ModelType, NULL AS Instances{composition}
                FROM tblContentType ct
                ORDER BY ct.Name
                """;
        }
        var blueprints = schema.Blueprints;
        // CMS 13: inline blocks of the type (Visual Builder sections and elements, mostly) in each branch's primary version,
        // from the CMS's index of them, as `where-used --type` counts them. Not on CMS 12, whose output stays as it was.
        var inline = schema.Compositions ? $"""

            LEFT JOIN (
                SELECT u.fkContentTypeID, COUNT(*) AS InlineUses
                FROM tblInlineBlockUsage u
                JOIN tblWorkContent w ON w.pkID = u.fkWorkContentID
                JOIN tblContent c ON c.pkID = u.fkContentID AND c.Deleted = 0
                JOIN tblContentLanguage cl ON cl.fkContentID = w.fkContentID AND cl.fkLanguageBranchID = w.fkLanguageBranchID
                {ContentHeaderReader.CommonDraftApply(schema)}
                WHERE w.pkID = CASE WHEN cl.Status = {(int)VersionStatus.Published} THEN cl.Version ELSE cd.CommonDraftId END
                GROUP BY u.fkContentTypeID
            ) iu ON iu.fkContentTypeID = ct.pkID
            """ : "";
        return $"""
            SELECT ct.pkID, ct.ContentTypeGUID, ct.Name, ct.DisplayName, ct.Description, ct.ContentType, ct.Base,
                   ct.ModelType, ISNULL(n.Instances, 0) AS Instances{composition}{(blueprints ? ", ISNULL(n.Blueprints, 0) AS Blueprints" : "")}{(schema.Compositions ? ", ISNULL(iu.InlineUses, 0) AS InlineUses" : "")}
            FROM tblContentType ct
            LEFT JOIN (
                SELECT fkContentTypeID, {(blueprints ? "SUM(CASE WHEN ISNULL(Blueprint, 0) = 0 THEN 1 ELSE 0 END) AS Instances, SUM(CASE WHEN Blueprint = 1 THEN 1 ELSE 0 END) AS Blueprints" : "COUNT(*) AS Instances")}
                FROM tblContent
                WHERE Deleted = 0
                GROUP BY fkContentTypeID
            ) n ON n.fkContentTypeID = ct.pkID{inline}
            ORDER BY ct.Name
            """;
    }

    /// <summary>CMS 13: which contract (interface) each content type implements, by the types' ids.</summary>
    private const string ContractsSql = """
        SELECT t.pkID AS TypeId, c.Name AS Contract
        FROM tblContentTypeContract tc
        JOIN tblContentType t ON t.ContentTypeGUID = tc.ContentTypeID
        JOIN tblContentType c ON c.ContentTypeGUID = tc.ContractID
        ORDER BY c.Name
        """;

    internal static string PropertiesSql(CmsSchema schema) => $"""
        SELECT pd.pkID, pd.Name, {CmsModel.PropertyTypeName(schema)} AS DataType, bt.Name AS BlockType, pd.IsList, pd.LanguageSpecific,
               pd.Required, g.Name AS Tab, pd.FieldOrder, pd.EditCaption, pd.ExistsOnModel
        FROM tblPropertyDefinition pd
        LEFT JOIN tblPropertyDefinitionType pdt ON pdt.pkID = pd.fkPropertyDefinitionTypeID
        {CmsModel.BlockTypeJoin(schema)}
        LEFT JOIN tblPropertyDefinitionGroup g ON g.pkID = pd.Advanced
        WHERE pd.fkContentTypeID = @typeId
        ORDER BY pd.Name
        """;

    /// <summary><c>tblPropertyDefinition.LanguageSpecific</c> value for culture-specific properties.</summary>
    private const int CultureSpecificFlag = 4;

    /// <param name="countInstances">Also count each type's content (<see cref="ContentTypeInfo.Instances"/>).</param>
    public static async Task<IReadOnlyList<ContentTypeInfo>> ListAsync(CmsDatabase db, CancellationToken cancellationToken, bool countInstances = false)
    {
        var schema = await db.SchemaAsync(cancellationToken);
        var types = await db.QueryAsync(TypesSql(schema, countInstances), r =>
        {
            var behaviors = schema.Compositions ? ContentKinds.Behaviors(r.GetStringOrNull("CompositionBehavior")) : [];
            var contract = schema.Compositions && r.GetBooleanOrNull("IsContract") == true;
            return new ContentTypeInfo(
                r.GetInt32("pkID"),
                r.GetGuid("ContentTypeGUID"),
                r.GetString("Name"),
                r.GetStringOrNull("DisplayName"),
                r.GetStringOrNull("Description"),
                ContentKinds.From(r.GetInt32("ContentType"), r.GetStringOrNull("Base"), behaviors, contract),
                r.GetStringOrNull("Base"),
                r.GetStringOrNull("ModelType"),
                r.GetInt32OrNull("Instances"))
            {
                SyncedVersion = schema.Compositions ? r.GetStringOrNull("SyncedVersion") : null,
                Source = schema.Compositions && r.GetStringOrNull("Source") is { Length: > 0 } source ? source : null,
                CompositionBehaviors = behaviors,
                Blueprints = countInstances && schema.Blueprints && r.GetInt32OrNull("Blueprints") is > 0 and var blueprints ? blueprints : null,
                InlineUses = countInstances && schema.Compositions && r.GetInt32OrNull("InlineUses") is > 0 and var uses ? uses : null,
            };
        }, cancellationToken);
        if (!schema.Compositions)
        {
            return types;
        }
        var contracts = (await db.QueryAsync(ContractsSql, r => (TypeId: r.GetInt32("TypeId"), Contract: r.GetString("Contract")), cancellationToken))
            .ToLookup(c => c.TypeId, c => c.Contract);
        return types.Select(t => contracts[t.Id].Any() ? t with { Contracts = contracts[t.Id].ToList() } : t).ToList();
    }

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

    public static async Task<IReadOnlyList<PropertyDefinitionInfo>> ListPropertiesAsync(CmsDatabase db, int contentTypeId, CancellationToken cancellationToken) =>
        await db.QueryAsync(PropertiesSql(await db.SchemaAsync(cancellationToken)), r => new PropertyDefinitionInfo(
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
