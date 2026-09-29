using OptiCli.Core.Errors;

namespace OptiCli.Core.Queries;

public enum WhereOperator
{
    /// <summary><c>=</c>: exact (case-insensitive for text, as the database collation compares).</summary>
    Equals,

    /// <summary><c>~</c>: text contains.</summary>
    Contains,
}

/// <summary>One <c>--where Prop=value</c> or <c>--where Prop~value</c>; <c>Block.Prop</c> reaches into a local block.</summary>
public sealed record WhereClause(string Property, WhereOperator Operator, string Value)
{
    /// <exception cref="UsageException">No operator, or no property name.</exception>
    public static WhereClause Parse(string text)
    {
        var at = text.IndexOfAny(['=', '~']);
        if (at <= 0)
        {
            throw new UsageException($"Invalid --where '{text}'.", "Use Prop=value (exact) or Prop~value (contains), e.g. --where Heading~news.");
        }
        return new WhereClause(text[..at].Trim(), text[at] == '=' ? WhereOperator.Equals : WhereOperator.Contains, text[(at + 1)..]);
    }

    /// <summary>A LIKE pattern matching <paramref name="text"/> anywhere, with LIKE wildcards escaped (use <c>ESCAPE '\'</c>).</summary>
    public static string ContainsPattern(string text) =>
        "%" + text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[") + "%";
}
