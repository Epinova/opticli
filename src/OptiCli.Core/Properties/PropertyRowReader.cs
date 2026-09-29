using System.Globalization;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Data;

namespace OptiCli.Core.Properties;

public static class PropertyRowReader
{
    private const string Columns = """
        fkPropertyDefinitionID, ScopeName, BranchSpecificScope, Boolean, Number, FloatNumber, ContentType,
        ContentLink, Date, String, LongString, LinkGuid
        """;

    /// <summary>
    /// The primary (published, or latest if never published) values of several items in the given
    /// branches, from <c>tblContentProperty</c>.
    /// </summary>
    public static async Task<IReadOnlyList<PropertyRow>> PrimaryAsync(
        CmsDatabase db, IEnumerable<int> contentIds, IEnumerable<int> languageIds, CancellationToken cancellationToken)
    {
        var languages = string.Join(",", languageIds.Distinct().Select(l => l.ToString(CultureInfo.InvariantCulture)));
        var rows = new List<PropertyRow>();
        if (languages.Length == 0)
        {
            return rows;
        }
        foreach (var ids in SqlLists.Ints(contentIds))
        {
            var sql = $"""
                SELECT fkContentID, fkLanguageBranchID, {Columns}
                FROM tblContentProperty
                WHERE fkContentID IN ({ids}) AND fkLanguageBranchID IN ({languages})
                """;
            rows.AddRange(await db.QueryAsync(sql, r => Map(r, r.GetInt32("fkContentID"), r.GetInt32("fkLanguageBranchID")), cancellationToken));
        }
        return rows;
    }

    /// <summary>The values saved with one version (<c>tblWorkContentProperty</c>), attributed to its branch.</summary>
    public static Task<IReadOnlyList<PropertyRow>> VersionAsync(
        CmsDatabase db, int contentId, int versionId, int languageId, CancellationToken cancellationToken) =>
        db.QueryAsync($"SELECT {Columns} FROM tblWorkContentProperty WHERE fkWorkContentID = @version",
            r => Map(r, contentId, languageId), cancellationToken, new SqlParameter("@version", versionId));

    private static PropertyRow Map(SqlDataReader r, int contentId, int languageId) => new(
        contentId,
        r.GetInt32("fkPropertyDefinitionID"),
        languageId,
        r.GetStringOrNull("ScopeName"),
        r.GetBooleanOrNull("BranchSpecificScope"),
        r.GetBooleanOrNull("Boolean"),
        r.GetInt32OrNull("Number"),
        r.GetDoubleOrNull("FloatNumber"),
        r.GetInt32OrNull("ContentType"),
        r.GetInt32OrNull("ContentLink"),
        r.GetDateTimeOrNull("Date"),
        r.GetStringOrNull("String"),
        r.GetStringOrNull("LongString"),
        r.GetGuidOrNull("LinkGuid"));
}
