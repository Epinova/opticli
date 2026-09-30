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
        return WithCategories(rows, await PrimaryCategoriesAsync(db, contentIds, languages, cancellationToken));
    }

    /// <summary>The values saved with one version (<c>tblWorkContentProperty</c>), attributed to its branch.</summary>
    public static async Task<IReadOnlyList<PropertyRow>> VersionAsync(
        CmsDatabase db, int contentId, int versionId, int languageId, CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync($"SELECT {Columns} FROM tblWorkContentProperty WHERE fkWorkContentID = @version",
            r => Map(r, contentId, languageId), cancellationToken, new SqlParameter("@version", versionId));
        return WithCategories(rows, await VersionCategoriesAsync(db, contentId, versionId, languageId, cancellationToken));
    }

    /// <summary>The built-in category of pages and media (<c>CategoryType</c> 0) of one branch, or of one version.</summary>
    public static async Task<IReadOnlyList<int>> BuiltInCategoriesAsync(
        CmsDatabase db, int contentId, int languageId, int? versionId, CancellationToken cancellationToken)
    {
        var rows = versionId is { } version
            ? await VersionCategoriesAsync(db, contentId, version, languageId, cancellationToken)
            : await PrimaryCategoriesAsync(db, [contentId], languageId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        return rows.Where(r => r.CategoryType == BuiltInCategoryType).Select(r => r.CategoryId).ToList();
    }

    /// <summary><c>CategoryType</c> of the built-in category; a Category property's rows use its own number.</summary>
    private const int BuiltInCategoryType = 0;

    private sealed record CategoryRow(int ContentId, int LanguageId, int CategoryType, string? ScopeName, int CategoryId);

    private static async Task<IReadOnlyList<CategoryRow>> PrimaryCategoriesAsync(
        CmsDatabase db, IEnumerable<int> contentIds, string languages, CancellationToken cancellationToken)
    {
        var rows = new List<CategoryRow>();
        foreach (var ids in SqlLists.Ints(contentIds))
        {
            var sql = $"""
                SELECT fkContentID, fkLanguageBranchID, CategoryType, ScopeName, fkCategoryID
                FROM tblContentCategory
                WHERE fkContentID IN ({ids}) AND fkLanguageBranchID IN ({languages})
                ORDER BY pkID
                """;
            rows.AddRange(await db.QueryAsync(sql, r => new CategoryRow(
                r.GetInt32("fkContentID"), r.GetInt32("fkLanguageBranchID"), r.GetInt32("CategoryType"), r.GetStringOrNull("ScopeName"), r.GetInt32("fkCategoryID")),
                cancellationToken));
        }
        return rows;
    }

    private static Task<IReadOnlyList<CategoryRow>> VersionCategoriesAsync(
        CmsDatabase db, int contentId, int versionId, int languageId, CancellationToken cancellationToken) =>
        db.QueryAsync("SELECT CategoryType, ScopeName, fkCategoryID FROM tblWorkContentCategory WHERE fkWorkContentID = @version ORDER BY pkID",
            r => new CategoryRow(contentId, languageId, r.GetInt32("CategoryType"), r.GetStringOrNull("ScopeName"), r.GetInt32("fkCategoryID")),
            cancellationToken, new SqlParameter("@version", versionId));

    /// <summary>Attaches each Category property row's categories: a Category row's number is its <c>CategoryType</c>.</summary>
    private static IReadOnlyList<PropertyRow> WithCategories(IReadOnlyList<PropertyRow> rows, IReadOnlyList<CategoryRow> categories)
    {
        if (categories.Count == 0)
        {
            return rows;
        }
        var byKey = categories.Where(c => c.CategoryType != BuiltInCategoryType)
            .ToLookup(c => (c.ContentId, c.LanguageId, c.CategoryType, c.ScopeName ?? ""), c => c.CategoryId);
        return rows.Select(row => row.Number is { } type && byKey[(row.ContentId, row.LanguageId, type, row.ScopeName ?? "")].ToList() is { Count: > 0 } ids
            ? row with { Categories = ids }
            : row).ToList();
    }

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
