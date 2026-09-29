using OptiCli.Core.Errors;

namespace OptiCli.Core.Sql;

public enum SqlTokenKind
{
    /// <summary>Unquoted keyword or identifier (including <c>@variables</c> and <c>#temp</c> names).</summary>
    Word,

    /// <summary><c>[name]</c> or <c>"name"</c>, unescaped.</summary>
    QuotedName,

    String,
    Number,
    Symbol,
}

public sealed record SqlToken(SqlTokenKind Kind, string Text)
{
    public bool IsName => Kind is SqlTokenKind.Word or SqlTokenKind.QuotedName;

    public bool Is(string word) => Kind == SqlTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Splits T-SQL into tokens so the guard can reason about keywords and names without being fooled by
/// comments, string literals or quoted identifiers.
/// </summary>
public static class SqlTokenizer
{
    /// <exception cref="RefusedException">An unterminated string, quoted name or comment.</exception>
    public static IReadOnlyList<SqlToken> Tokenize(string sql)
    {
        var tokens = new List<SqlToken>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && Peek(sql, i + 1) == '-')
            {
                var end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end + 1;
            }
            else if (c == '/' && Peek(sql, i + 1) == '*')
            {
                i = SkipBlockComment(sql, i);
            }
            else if (c == '\'' || ((c == 'N' || c == 'n') && Peek(sql, i + 1) == '\''))
            {
                var start = c == '\'' ? i : i + 1;
                var (text, next) = Quoted(sql, start, '\'', "string literal");
                tokens.Add(new SqlToken(SqlTokenKind.String, text));
                i = next;
            }
            else if (c == '[')
            {
                var (text, next) = Quoted(sql, i, ']', "[name]");
                tokens.Add(new SqlToken(SqlTokenKind.QuotedName, text));
                i = next;
            }
            else if (c == '"')
            {
                var (text, next) = Quoted(sql, i, '"', "\"name\"");
                tokens.Add(new SqlToken(SqlTokenKind.QuotedName, text));
                i = next;
            }
            else if (char.IsLetter(c) || c is '_' or '@' or '#')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '@' or '#' or '$'))
                {
                    i++;
                }
                tokens.Add(new SqlToken(SqlTokenKind.Word, sql[start..i]));
            }
            else if (char.IsAsciiDigit(c))
            {
                var start = i;
                i = SkipNumber(sql, i);
                // SQL Server ends a number at the first letter, so "1FROM" is "1 FROM": refuse rather than guess
                // where its lexer splits (a keyword glued to a number would otherwise go unseen).
                if (i < sql.Length && (char.IsLetter(sql[i]) || sql[i] is '_' or '@' or '#'))
                {
                    throw new RefusedException($"Refusing SQL: the number '{sql[start..i]}' runs straight into '{sql[i]}'.", "Put a space between a number and the word after it.");
                }
                tokens.Add(new SqlToken(SqlTokenKind.Number, sql[start..i]));
            }
            else
            {
                tokens.Add(new SqlToken(SqlTokenKind.Symbol, c.ToString()));
                i++;
            }
        }
        return tokens;
    }

    private static char Peek(string sql, int index) => index < sql.Length ? sql[index] : '\0';

    /// <summary>A T-SQL numeric literal: <c>0x</c> + hex digits, or digits, optional <c>.digits</c>, optional <c>e[+-]digits</c>.</summary>
    private static int SkipNumber(string sql, int i)
    {
        if (sql[i] == '0' && Peek(sql, i + 1) is 'x' or 'X')
        {
            i += 2;
            while (i < sql.Length && char.IsAsciiHexDigit(sql[i]))
            {
                i++;
            }
            return i;
        }
        while (i < sql.Length && char.IsAsciiDigit(sql[i]))
        {
            i++;
        }
        if (Peek(sql, i) == '.')
        {
            i++;
            while (i < sql.Length && char.IsAsciiDigit(sql[i]))
            {
                i++;
            }
        }
        if (Peek(sql, i) is 'e' or 'E')
        {
            var exponent = Peek(sql, i + 1) is '+' or '-' ? i + 2 : i + 1;
            if (char.IsAsciiDigit(Peek(sql, exponent)))
            {
                i = exponent;
                while (i < sql.Length && char.IsAsciiDigit(sql[i]))
                {
                    i++;
                }
            }
        }
        return i;
    }

    /// <summary>T-SQL block comments nest.</summary>
    private static int SkipBlockComment(string sql, int i)
    {
        var depth = 0;
        while (i < sql.Length)
        {
            if (sql[i] == '/' && Peek(sql, i + 1) == '*')
            {
                depth++;
                i += 2;
            }
            else if (sql[i] == '*' && Peek(sql, i + 1) == '/')
            {
                depth--;
                i += 2;
                if (depth == 0)
                {
                    return i;
                }
            }
            else
            {
                i++;
            }
        }
        throw new RefusedException("Refusing SQL with an unterminated /* comment.");
    }

    /// <summary>Reads from the opening quote at <paramref name="start"/>; a doubled closing quote is an escaped one.</summary>
    private static (string Text, int Next) Quoted(string sql, int start, char close, string what)
    {
        var text = new System.Text.StringBuilder();
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (Peek(sql, i + 1) == close)
                {
                    text.Append(close);
                    i += 2;
                    continue;
                }
                return (text.ToString(), i + 1);
            }
            text.Append(sql[i]);
            i++;
        }
        throw new RefusedException($"Refusing SQL with an unterminated {what}.");
    }
}
