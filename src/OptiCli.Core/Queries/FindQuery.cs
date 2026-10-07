using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Core.Properties;
using OptiCli.Core.Refs;
using OptiCli.Core.Text;

namespace OptiCli.Core.Queries;

public enum FindStatus
{
    Any,

    /// <summary>The branch is published.</summary>
    Published,

    /// <summary>The branch was never published, or has a version newer than the published one.</summary>
    Draft,

    /// <summary>The branch has a version waiting to be published at a set time (<c>delayedPublish</c>).</summary>
    Scheduled,

    /// <summary>The branch is published, and its stop-publish date has passed: visitors don't see it.</summary>
    Expired,
}

/// <param name="PublishAt">For <see cref="FindStatus.Scheduled"/>: when the first scheduled version is published, UTC.</param>
/// <param name="ExpiredAt">For <see cref="FindStatus.Expired"/>: when the published version stopped publishing, UTC.</param>
/// <param name="VariationDrafts">
/// For <see cref="FindStatus.Draft"/> on CMS 13: the content variations (keys) with an unpublished version newer than their
/// own published one, which make the item match too; null when none has.
/// </param>
public sealed record FoundItem(int Id, DateTime? PublishAt = null, DateTime? ExpiredAt = null, IReadOnlyList<string>? VariationDrafts = null);

/// <summary>
/// <c>find</c>: content of one type, filtered in SQL. <c>--where</c> compares the primary (published, or
/// never-published latest) values in <c>tblContentProperty</c>, the ones the site shows.
/// </summary>
/// <param name="includeBlueprints">CMS 13: Visual Builder blueprints too; left out by default.</param>
public sealed class FindQuery(ContentSession session, bool includeBlueprints = false)
{
    private const string NamePseudoProperty = "Name";

    /// <returns>Up to <paramref name="limit"/> + 1 matching items (the extra one signals more), ordered by id.</returns>
    public async Task<IReadOnlyList<FoundItem>> RunAsync(
        ContentTypeInfo type,
        IReadOnlyList<WhereClause> where,
        int? underId,
        FindStatus status,
        LanguageBranch? language,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var parameters = new List<SqlParameter>
        {
            new("@type", type.Id),
            new("@offset", offset),
            new("@take", limit + 1),
        };
        // A content variation's versions (CMS 13) count for draft and scheduled too: a variation's unpublished version
        // newer than its own published one, or one scheduled for publishing.
        var schema = session.Model.Schema;
        var variationDraftRows = $"""
            FROM tblWorkContent wc
                      WHERE wc.fkContentID = c.pkID AND wc.fkLanguageBranchID = cl.fkLanguageBranchID AND wc.fkVariationID IS NOT NULL
                        AND wc.Status IN ({VersionStatuses.UnpublishedSql})
                        AND wc.pkID > ISNULL((SELECT MAX(p.pkID) FROM tblWorkContent p
                                              WHERE p.fkContentID = wc.fkContentID AND p.fkLanguageBranchID = wc.fkLanguageBranchID
                                                AND p.fkVariationID = wc.fkVariationID AND p.Status = {(int)VersionStatus.Published}), 0)
            """;
        var variationDraft = schema.Variations ? $"""
             OR EXISTS (
                      SELECT 1 {variationDraftRows})
            """ : "";
        // Which variations those are, so a row can say why it matched.
        var variationKeys = schema.Variations && status == FindStatus.Draft ? $"""
            (SELECT STRING_AGG(v.[Key], NCHAR(31)) WITHIN GROUP (ORDER BY v.[Key]) FROM tblContentVariation v
             WHERE v.pkID IN (SELECT wc.fkVariationID {variationDraftRows}))
            """ : "NULL";
        var defaultOnly = schema.DefaultVariationOnly("wc");
        // A branch's first scheduled version: when the CMS's job publishes it.
        var scheduled = $"""
            (SELECT MIN(wc.DelayPublishUntil) FROM tblWorkContent wc
             WHERE wc.fkContentID = c.pkID AND wc.fkLanguageBranchID = cl.fkLanguageBranchID AND wc.Status = {(int)VersionStatus.DelayedPublish})
            """;
        var sql = new StringBuilder($"""
            SELECT c.pkID, {(status == FindStatus.Scheduled ? scheduled : "NULL")} AS PublishAt, {(status == FindStatus.Expired ? "cl.StopPublish" : "NULL")} AS ExpiredAt,
                   {variationKeys} AS VariationDrafts
            FROM tblContent c
            JOIN tblContentLanguage cl ON cl.fkContentID = c.pkID AND cl.fkLanguageBranchID = {(language is null ? "c.fkMasterLanguageBranchID" : "@lang")}
            WHERE c.fkContentTypeID = @type AND c.Deleted = 0{(schema.Blueprints && !includeBlueprints ? " AND ISNULL(c.Blueprint, 0) = 0" : "")}
            """);
        if (language is not null)
        {
            parameters.Add(new SqlParameter("@lang", language.Id));
        }
        if (underId is { } under)
        {
            sql.Append("\n  AND c.ContentPath LIKE @under");
            parameters.Add(new SqlParameter("@under", $"%.{under.ToString(CultureInfo.InvariantCulture)}.%"));
        }
        sql.Append(status switch
        {
            FindStatus.Published => $"\n  AND cl.Status = {(int)VersionStatus.Published}",
            FindStatus.Draft => $"""

                  AND (cl.Status <> {(int)VersionStatus.Published} OR EXISTS (
                      SELECT 1 FROM tblWorkContent wc
                      WHERE wc.fkContentID = c.pkID AND wc.fkLanguageBranchID = cl.fkLanguageBranchID
                        AND wc.Status IN ({VersionStatuses.UnpublishedSql}) AND wc.pkID > ISNULL(cl.Version, 0){defaultOnly}){variationDraft})
                """,
            FindStatus.Scheduled => $"""

                  AND EXISTS (SELECT 1 FROM tblWorkContent wc
                      WHERE wc.fkContentID = c.pkID AND wc.fkLanguageBranchID = cl.fkLanguageBranchID AND wc.Status = {(int)VersionStatus.DelayedPublish})
                """,
            // Dates are UTC in the database; the CLI's clock decides what has passed, as for every other UTC time it prints.
            FindStatus.Expired => $"\n  AND cl.Status = {(int)VersionStatus.Published} AND cl.StopPublish IS NOT NULL AND cl.StopPublish <= @now",
            _ => "",
        });
        if (status == FindStatus.Expired)
        {
            parameters.Add(new SqlParameter("@now", System.Data.SqlDbType.DateTime) { Value = DateTime.UtcNow });
        }

        for (var i = 0; i < where.Count; i++)
        {
            sql.Append("\n  AND ").Append(await ConditionAsync(type, where[i], i, parameters, cancellationToken));
        }
        sql.Append("\nORDER BY c.pkID\nOFFSET @offset ROWS FETCH NEXT @take ROWS ONLY");

        return await session.Db.QueryAsync(sql.ToString(), r => new FoundItem(
                r.GetInt32("pkID"), r.GetDateTimeOrNull("PublishAt"), r.GetDateTimeOrNull("ExpiredAt"),
                r.GetStringOrNull("VariationDrafts") is { Length: > 0 } keys ? keys.Split('\u001f') : null),
            cancellationToken, parameters.ToArray());
    }

    private async Task<string> ConditionAsync(ContentTypeInfo type, WhereClause clause, int index, List<SqlParameter> parameters, CancellationToken cancellationToken)
    {
        var value = $"@v{index}";
        var path = clause.Property.Split('.', StringSplitOptions.TrimEntries);
        var properties = session.Model.PropertiesOf(type.Id).ToList();
        var outer = properties.FirstOrDefault(p => string.Equals(p.Name, path[0], StringComparison.OrdinalIgnoreCase));

        if (outer is null && path.Length == 1 && string.Equals(path[0], NamePseudoProperty, StringComparison.OrdinalIgnoreCase))
        {
            parameters.Add(new SqlParameter(value, clause.Operator == WhereOperator.Contains ? WhereClause.ContainsPattern(clause.Value) : clause.Value));
            return clause.Operator == WhereOperator.Contains ? $"cl.Name LIKE {value} ESCAPE '\\'" : $"cl.Name = {value}";
        }

        var definition = outer ?? throw UnknownProperty(type, path[0], properties.Select(p => p.Name).Append(NamePseudoProperty));
        var scope = "p.ScopeName IS NULL";
        var inBlock = false;
        if (path.Length > 1)
        {
            if (definition.BaseType != PropertyBaseType.Block || definition.IsList || definition.BlockType is not { } blockType)
            {
                throw new UsageException($"'{definition.Name}' is not a local block, so '{clause.Property}' cannot be looked up.", "Use Block.Property only for single block-typed properties.");
            }
            var innerProperties = session.Model.PropertiesOf(blockType).ToList();
            var inner = innerProperties.FirstOrDefault(p => string.Equals(p.Name, path[1], StringComparison.OrdinalIgnoreCase))
                ?? throw UnknownProperty(session.Model.Type(blockType)!, path[1], innerProperties.Select(p => p.Name));
            scope = $"p.ScopeName = @s{index}";
            parameters.Add(new SqlParameter($"@s{index}", $".{definition.Id.ToString(CultureInfo.InvariantCulture)}.{inner.Id.ToString(CultureInfo.InvariantCulture)}."));
            inBlock = true;
            definition = inner;
        }
        else if (definition.BaseType == PropertyBaseType.Block)
        {
            throw new UsageException($"'{definition.Name}' is a block; filter on one of its properties, e.g. --where {definition.Name}.<Property>=value.");
        }

        var (condition, negate) = await ValueConditionAsync(definition, clause, value, parameters, cancellationToken);
        var exists = $"""
            EXISTS (SELECT 1 FROM tblContentProperty p
                    WHERE p.fkContentID = c.pkID AND p.fkPropertyDefinitionID = {definition.Id.ToString(CultureInfo.InvariantCulture)}
                      AND {LanguageCondition(definition, inBlock)}
                      AND {scope} AND {condition})
            """;
        return negate ? "NOT " + exists : exists;
    }

    /// <summary>
    /// The branch <c>p</c> must be stored on to be the value <c>get</c> shows in <c>cl</c>'s language: culture-specific
    /// values on that branch, shared ones on the master branch. Inside a local block the row's <c>BranchSpecificScope</c>
    /// decides (a shared block keeps even its culture-specific values on the master branch), as in <see cref="PropertyRows.Effective"/>.
    /// </summary>
    internal static string LanguageCondition(PropertyDefinition definition, bool inBlock) => inBlock
        ? PropertyRows.EffectiveSql("p", definition.CultureSpecific ? "1" : "0", "cl.fkLanguageBranchID", "c.fkMasterLanguageBranchID")
        : $"p.fkLanguageBranchID = {(definition.CultureSpecific ? "cl.fkLanguageBranchID" : "c.fkMasterLanguageBranchID")}";

    /// <returns>The SQL condition on <c>p</c>, and whether the EXISTS must be negated (false booleans are often not stored).</returns>
    private async Task<(string Condition, bool Negate)> ValueConditionAsync(
        PropertyDefinition definition, WhereClause clause, string value, List<SqlParameter> parameters, CancellationToken cancellationToken)
    {
        var text = clause.Value.Trim();
        var contains = clause.Operator == WhereOperator.Contains;

        UsageException Invalid(string expected) => new(
            $"'{clause.Value}' is not {expected}, which {definition.Name} ({definition.TypeName}) stores.",
            contains ? "~ (contains) only works on text properties; use =." : null);

        switch (definition.BaseType)
        {
            case PropertyBaseType.Boolean:
                var flag = text.ToLowerInvariant() switch
                {
                    _ when contains => throw Invalid("true or false"),
                    "true" or "1" or "yes" => true,
                    "false" or "0" or "no" => false,
                    _ => throw Invalid("true or false"),
                };
                return ("p.Boolean = 1", !flag);

            case PropertyBaseType.Number:
            case PropertyBaseType.PageType:
                if (contains)
                {
                    throw Invalid("a whole number");
                }
                var number = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed
                    : definition.BaseType == PropertyBaseType.PageType ? session.Model.RequireType(text).Id
                    : throw Invalid("a whole number");
                parameters.Add(new SqlParameter(value, number));
                return ($"p.Number = {value}", false);

            case PropertyBaseType.FloatNumber:
                if (contains || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
                {
                    throw Invalid("a number");
                }
                parameters.Add(new SqlParameter(value, real));
                return ($"p.FloatNumber = {value}", false);

            case PropertyBaseType.Date:
                if (contains || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                {
                    throw Invalid("a date (yyyy-MM-dd or yyyy-MM-ddTHH:mm:ss)");
                }
                parameters.Add(new SqlParameter(value, date));
                return (text.Length <= 10 ? $"CAST(p.Date AS date) = CAST({value} AS date)" : $"p.Date = {value}", false);

            case PropertyBaseType.PageReference:
            case PropertyBaseType.ContentReference:
                var target = await session.LocateAsync(text, null, cancellationToken);
                parameters.Add(new SqlParameter(value, target.Id));
                return ($"p.ContentLink = {value}", false);
        }

        if (definition.TypeName is "ContentArea" or "ContentReferenceList" && ContentRefParser.TryParse(text, out _, out _))
        {
            // Items are stored by GUID: "contains this item" is the useful question for both operators.
            var item = await session.LocateAsync(text, null, cancellationToken);
            var header = await session.HeaderAsync(item.Id, cancellationToken);
            parameters.Add(new SqlParameter(value, WhereClause.ContainsPattern(header.Guid.ToString("D"))));
            return ($"p.LongString LIKE {value} ESCAPE '\\'", false);
        }

        var column = definition.BaseType == PropertyBaseType.String ? "p.String" : "p.LongString";
        parameters.Add(new SqlParameter(value, contains ? WhereClause.ContainsPattern(clause.Value) : clause.Value));
        return (contains ? $"{column} LIKE {value} ESCAPE '\\'" : $"{column} = {value}", false);
    }

    private static UsageException UnknownProperty(ContentTypeInfo type, string name, IEnumerable<string> names) => new(
        $"{type.Name} has no property '{name}'.",
        Suggestions.DidYouMean(name, names) ?? $"Run `opticli type {type.Name}` to list its properties.");
}
