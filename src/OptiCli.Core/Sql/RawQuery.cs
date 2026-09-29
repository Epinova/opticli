using System.Globalization;
using OptiCli.Core.Data;
using OptiCli.Core.Properties;

namespace OptiCli.Core.Sql;

/// <summary><c>sql</c>: guard, then run in an always-rolled-back transaction.</summary>
public static class RawQuery
{
    /// <exception cref="Errors.RefusedException">The guard refused the statement.</exception>
    public static Task<SqlResult> RunAsync(CmsDatabase db, string sql, int maxRows, bool includePersonalData, bool full, CancellationToken cancellationToken)
    {
        SqlStatementGuard.Check(sql, includePersonalData);
        return db.QueryRolledBackAsync(sql, maxRows, value => Convert(value, full), cancellationToken);
    }

    private static object? Convert(object value, bool full) => value switch
    {
        string text when !full && text.Length > TextValues.MaxLength => text[..TextValues.MaxLength] + "…",
        byte[] bytes => $"0x{System.Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, full ? bytes.Length : 64)))}{(!full && bytes.Length > 64 ? "…" : "")}",
        DateTime date => date.ToString("yyyy-MM-ddTHH:mm:ss.FFF", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
        Guid or bool or string or int or long or short or byte or decimal or double or float => value,
        _ => System.Convert.ToString(value, CultureInfo.InvariantCulture),
    };
}
