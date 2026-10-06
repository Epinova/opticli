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
    /// so output says so (<c>Z</c>) instead of passing for local time. CMS 13's <c>datetime2</c> columns are rounded to the
    /// millisecond, the most CMS 12's <c>datetime</c> ever gave, so times print the same on both.
    /// </summary>
    public static DateTime? GetDateTimeOrNull(this SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : DateTime.SpecifyKind(ToMilliseconds(reader.GetDateTime(ordinal)), DateTimeKind.Utc);
    }

    /// <summary>
    /// Rounded to the nearest millisecond; a CMS 12 <c>datetime</c> value already is. The last half millisecond of
    /// 9999-12-31 (where <see cref="DateTime.MaxValue"/> is stored in a <c>datetime2</c>) stays in it rather than rounding
    /// past the largest date there is.
    /// </summary>
    public static DateTime ToMilliseconds(DateTime time) =>
        new(Math.Min((time.Ticks + TimeSpan.TicksPerMillisecond / 2) / TimeSpan.TicksPerMillisecond, LastMillisecond) * TimeSpan.TicksPerMillisecond, time.Kind);

    /// <summary><see cref="DateTime.MaxValue"/> in whole milliseconds: 9999-12-31 23:59:59.999.</summary>
    private static readonly long LastMillisecond = DateTime.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;

    public static double? GetDoubleOrNull(this SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : Convert.ToDouble(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static Guid GetGuid(this SqlDataReader reader, string column) => reader.GetGuid(reader.GetOrdinal(column));

    public static int GetInt32(this SqlDataReader reader, string column) => reader.GetInt32(reader.GetOrdinal(column));

    public static string GetString(this SqlDataReader reader, string column) => reader.GetString(reader.GetOrdinal(column));
}
