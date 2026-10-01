namespace OptiCli.Core.Sql;

/// <summary>
/// Which table names and aliases (<c>FROM</c>, <c>JOIN</c> and <c>APPLY</c> sources) each token of a query can see. A
/// query's scope runs from its <c>SELECT</c> to the next <c>SELECT</c> at the same parenthesis level (a <c>UNION</c>
/// branch) or the closing parenthesis; it also sees the scopes it is nested in.
/// </summary>
/// <remarks>
/// Used only to allow more, so it errs towards seeing fewer names: an alias it misses only makes the guard refuse a
/// query it could have run. Names compare exactly (ordinal), so a case-sensitive collation can't tell them apart.
/// </remarks>
internal sealed class SqlTableScopes
{
    /// <summary>Words that can follow a table source but don't name it.</summary>
    private static readonly HashSet<string> NotAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "ON", "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "APPLY", "GROUP", "ORDER", "HAVING",
        "UNION", "EXCEPT", "INTERSECT", "WITH", "FOR", "OPTION", "PIVOT", "UNPIVOT", "TABLESAMPLE", "SELECT", "FROM", "WINDOW",
    };

    private readonly int[] _scopeOf;
    private readonly List<int> _parent = [];
    private readonly HashSet<(int Scope, string Name)> _tables = [];

    public SqlTableScopes(IReadOnlyList<SqlToken> tokens)
    {
        _scopeOf = new int[tokens.Count];
        var open = new Stack<int>();
        open.Push(NewScope(-1));
        // Where a source ends, a comma starts the next one (FROM a x, b y).
        var sourceEnds = new HashSet<int>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token is { Kind: SqlTokenKind.Symbol, Text: "(" })
            {
                open.Push(NewScope(open.Peek()));
            }
            else if (token is { Kind: SqlTokenKind.Symbol, Text: ")" } && open.Count > 1)
            {
                open.Pop();
            }
            else if (token.Is("SELECT"))
            {
                open.Push(NewScope(_parent[open.Pop()]));
            }
            _scopeOf[i] = open.Peek();

            if (token.Is("FROM") || token.Is("JOIN") || token.Is("APPLY") || (sourceEnds.Contains(i) && token is { Kind: SqlTokenKind.Symbol, Text: "," }))
            {
                sourceEnds.Add(Source(tokens, i + 1, open.Peek()));
            }
        }
    }

    /// <summary>Whether <paramref name="name"/> is a table or alias the token at <paramref name="index"/> can see.</summary>
    public bool IsTable(int index, string name)
    {
        for (var scope = _scopeOf[index]; scope >= 0; scope = _parent[scope])
        {
            if (_tables.Contains((scope, name)))
            {
                return true;
            }
        }
        return false;
    }

    private int NewScope(int parent)
    {
        _parent.Add(parent);
        return _parent.Count - 1;
    }

    /// <summary>
    /// Reads the table source at <paramref name="i"/> (<c>[schema.]table</c>, <c>function(...)</c> or <c>(subquery)</c>,
    /// then an optional <c>[AS] alias[(columns)]</c>) and returns the index after it.
    /// </summary>
    private int Source(IReadOnlyList<SqlToken> tokens, int i, int scope)
    {
        string? exposed = null;
        if (i < tokens.Count && tokens[i] is { Kind: SqlTokenKind.Symbol, Text: "(" })
        {
            // A derived table is named only by its alias.
            i = Close(tokens, i) + 1;
        }
        else if (i < tokens.Count && tokens[i].IsName)
        {
            exposed = tokens[i].Text;
            i++;
            while (i + 1 < tokens.Count && tokens[i] is { Kind: SqlTokenKind.Symbol, Text: "." } && tokens[i + 1].IsName)
            {
                exposed = tokens[i + 1].Text;
                i += 2;
            }
            if (i < tokens.Count && tokens[i] is { Kind: SqlTokenKind.Symbol, Text: "(" })
            {
                i = Close(tokens, i) + 1;
            }
        }
        else
        {
            return i;
        }

        if (i < tokens.Count && tokens[i].Is("AS"))
        {
            i++;
        }
        if (i < tokens.Count && tokens[i].IsName && !(tokens[i].Kind == SqlTokenKind.Word && NotAliases.Contains(tokens[i].Text)))
        {
            exposed = tokens[i].Text;
            i++;
            if (i < tokens.Count && tokens[i] is { Kind: SqlTokenKind.Symbol, Text: "(" })
            {
                i = Close(tokens, i) + 1;
            }
        }
        if (exposed is not null)
        {
            _tables.Add((scope, exposed.TrimEnd()));
        }
        return i;
    }

    /// <summary>The index of the parenthesis that closes the one at <paramref name="i"/>, or the last token.</summary>
    private static int Close(IReadOnlyList<SqlToken> tokens, int i)
    {
        var depth = 0;
        for (; i < tokens.Count; i++)
        {
            if (tokens[i] is { Kind: SqlTokenKind.Symbol, Text: "(" })
            {
                depth++;
            }
            else if (tokens[i] is { Kind: SqlTokenKind.Symbol, Text: ")" } && --depth == 0)
            {
                return i;
            }
        }
        return tokens.Count - 1;
    }
}
