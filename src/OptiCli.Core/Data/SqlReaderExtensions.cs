using Microsoft.Data.SqlClient;

namespace OptiCli.Core.Data;

/// <summary>Null-aware column access by name, so readers stay one line per column.</summary>
internal static class SqlReaderExtensions
{
    public static string? GetStringOrNull(this SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static int? GetInt32OrNull(this SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    public static bool? GetBooleanOrNull(this SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
    }

    public static Guid? GetGuidOrNull(this SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    /// <summary>
    /// CMS 12 stores every date (publish dates, saved, Date properties) in UTC; the value is marked as such,
    /// so output says so (<c>Z</c>) instead of passing for local time.
    /// </summary>
    public static DateTime? GetDateTimeOrNull(this SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);
    }

    public static double? GetDoubleOrNull(this SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : Convert.ToDouble(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static Guid GetGuid(this SqlDataReader reader, string column) => reader.GetGuid(reader.GetOrdinal(column));

    public static int GetInt32(this SqlDataReader reader, string column) => reader.GetInt32(reader.GetOrdinal(column));

    public static string GetString(this SqlDataReader reader, string column) => reader.GetString(reader.GetOrdinal(column));
}
