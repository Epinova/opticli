namespace OptiCli.Core.Sql;

/// <param name="Rows">One object per row, keyed by column name (duplicates get a <c>_2</c> suffix).</param>
/// <param name="Truncated">True when the query returned more rows than the limit.</param>
public sealed record SqlResult(IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, object?>> Rows, int RowCount, bool? Truncated, bool RolledBack);
