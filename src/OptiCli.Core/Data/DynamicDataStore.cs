using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace OptiCli.Core.Data;

/// <summary>
/// Where one Dynamic Data Store keeps its properties: the big table (<c>tblSystemBigTable</c> or <c>tblBigTable</c>) and
/// the column of each property, so a fixed query can read one store's rows and only the columns it needs.
/// </summary>
/// <remarks>
/// <para>Those tables are personal data as far as <c>sql</c> is concerned: they hold user profiles, form submissions and
/// site secrets beside the stores opticli reads. Callers read only their store's rows (<c>StoreName = @store</c>) and only
/// the properties they name, and say in a comment why those are fine to read. Nothing here changes <c>sql</c>'s guard.</para>
/// <para>The CMS regenerates a store's view (<c>VW_&lt;store&gt;</c>) whenever it maps the store anew, so the view's
/// definition says what the running CMS uses; the mapping in <c>tblBigTableStoreInfo</c> can lag behind it (seen on a
/// restored database: rows written by an older CMS in <c>Indexed_String01</c>, newer ones, which the CMS reads, in
/// <c>String01</c>, and the mapping still naming the first). So the view comes first, that table only without it.</para>
/// </remarks>
public sealed partial record DynamicDataStore(string Name, string Table, IReadOnlyDictionary<string, string> Columns)
{
    private const string ConfigSql = """
        SELECT TableName FROM tblBigTableStoreConfig WHERE StoreName = @store
        """;

    private const string ViewSql = "SELECT OBJECT_DEFINITION(OBJECT_ID(@view)) AS Definition";

    private const string MappingSql = """
        SELECT i.PropertyName, i.ColumnName
        FROM tblBigTableStoreInfo i
        JOIN tblBigTableStoreConfig s ON s.pkId = i.fkStoreId
        WHERE s.StoreName = @store AND i.Active = 1 AND i.ColumnName IS NOT NULL
        """;

    /// <summary>The column of <paramref name="property"/>; null when the store doesn't map it.</summary>
    public string? Column(string property) => Columns.GetValueOrDefault(property);

    /// <summary>The store with the columns of <paramref name="properties"/> it maps; null when the database has no such store.</summary>
    public static async Task<DynamicDataStore?> FindAsync(CmsDatabase db, string storeName, IReadOnlyCollection<string> properties, CancellationToken cancellationToken)
    {
        var table = (await db.QueryAsync(ConfigSql, r => r.GetStringOrNull("TableName"), cancellationToken, new SqlParameter("@store", storeName))).FirstOrDefault();
        // Only the two big tables: the name goes into the SQL.
        if (table is not ("tblSystemBigTable" or "tblBigTable"))
        {
            return null;
        }
        var view = (await db.QueryAsync(ViewSql, r => r.GetStringOrNull("Definition"), cancellationToken, new SqlParameter("@view", $"dbo.VW_{storeName}"))).FirstOrDefault();
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (ViewColumn(view, property) is { } column)
            {
                columns[property] = column;
            }
        }
        if (columns.Count < properties.Count)
        {
            foreach (var (property, column) in await db.QueryAsync(MappingSql, r => (r.GetString("PropertyName"), r.GetString("ColumnName")), cancellationToken, new SqlParameter("@store", storeName)))
            {
                if (properties.Contains(property))
                {
                    columns.TryAdd(property, column);
                }
            }
        }
        // Only the big table's value columns, so nothing else from the database reaches the SQL.
        return new DynamicDataStore(storeName, table, columns.Where(c => ValueColumn().IsMatch(c.Value)).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal));
    }

    /// <summary>The column a store's view maps <paramref name="property"/> to (<c>R01.String01 as "Name"</c>); null when it doesn't say.</summary>
    public static string? ViewColumn(string? viewDefinition, string property)
    {
        if (viewDefinition is null)
        {
            return null;
        }
        var match = Regex.Match(viewDefinition, $@"\bR01\.\[?(?<column>[A-Za-z_0-9]+)\]?\s+as\s+[""\[]{Regex.Escape(property)}[""\]]", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["column"].Value : null;
    }

    [GeneratedRegex("^(Indexed_)?(String|Integer|Long|Boolean|Float|Decimal|Guid|DateTime|Binary)[0-9]{2}$")]
    private static partial Regex ValueColumn();
}
