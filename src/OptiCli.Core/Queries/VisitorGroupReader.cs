using Microsoft.Data.SqlClient;
using OptiCli.Core.Data;

namespace OptiCli.Core.Queries;

/// <param name="Id">What ContentAreas and rich text store (<c>visitorGroups</c> in <c>get</c>), and what <c>set</c> takes.</param>
/// <param name="Match">How its criteria combine: <c>all</c>, <c>any</c>, or <c>points</c> (at least <see cref="PointsThreshold"/>).</param>
/// <param name="SecurityRole">Usable as a role in access rights (<c>access --grant</c>).</param>
/// <param name="Statistics">The CMS counts how often it matches.</param>
public sealed record VisitorGroupInfo(Guid Id, string Name, string? Match, int? PointsThreshold, bool SecurityRole, bool Statistics);

/// <summary>
/// <c>visitor-groups</c>: the site's visitor groups (personalization), from the Dynamic Data Store's <c>VisitorGroup</c>
/// store.
/// </summary>
/// <remarks>
/// The store lives in <c>tblSystemBigTable</c>, which <c>sql</c> treats as personal data (it holds user profiles and form
/// submissions too). This reads only that one store's rows, and of them only the group's id, name and settings: its notes
/// and criteria (an IP range, a user name, a profile value) can name people, so they stay unread.
/// </remarks>
public sealed class VisitorGroupReader(CmsDatabase db)
{
    public const string StoreName = "VisitorGroup";

    private static readonly string[] Properties = ["Name", "CriteriaOperator", "PointsThreshold", "IsSecurityRole", "EnableStatistics"];

    /// <summary>The CMS's <c>CriteriaOperator</c>, by value.</summary>
    private static readonly string[] Matches = ["all", "any", "points"];

    public async Task<IReadOnlyList<VisitorGroupInfo>> ListAsync(CancellationToken cancellationToken)
    {
        if (await DynamicDataStore.FindAsync(db, StoreName, Properties, cancellationToken) is not { } store || store.Column("Name") is not { } name)
        {
            return [];
        }
        string Or(string property, string fallback) => store.Column(property) is { } column ? $"b.{column}" : fallback;
        var sql = $"""
            SELECT i.Guid AS Id, b.{name} AS Name, {Or("CriteriaOperator", "NULL")} AS CriteriaOperator, {Or("PointsThreshold", "NULL")} AS PointsThreshold,
                   {Or("IsSecurityRole", "0")} AS IsSecurityRole, {Or("EnableStatistics", "0")} AS EnableStatistics
            FROM {store.Table} b
            JOIN tblBigTableIdentity i ON i.pkId = b.pkId
            WHERE b.StoreName = @store
            """;
        var groups = await db.QueryAsync(sql, r => (
                Id: r.GetGuid("Id"),
                Name: r.GetStringOrNull("Name") ?? "",
                Operator: r.GetInt32OrNull("CriteriaOperator"),
                Points: r.GetInt32OrNull("PointsThreshold"),
                Role: Flag(r, "IsSecurityRole"),
                Statistics: Flag(r, "EnableStatistics")),
            cancellationToken, new SqlParameter("@store", StoreName));
        return groups
            .Select(g => new VisitorGroupInfo(g.Id, g.Name, g.Operator is { } op && op >= 0 && op < Matches.Length ? Matches[op] : null,
                g.Operator == 2 ? g.Points : null, g.Role, g.Statistics))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool Flag(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return !reader.IsDBNull(ordinal) && Convert.ToBoolean(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    }
}
