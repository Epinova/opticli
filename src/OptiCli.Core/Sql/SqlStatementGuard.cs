using OptiCli.Core.Errors;

namespace OptiCli.Core.Sql;

/// <summary>
/// Belt and braces for <c>sql</c>, which also always runs inside a rolled-back transaction: accepts one
/// SELECT (or WITH ... SELECT) statement against this database only, and nothing that writes, executes
/// code, reaches another database or server, or reads personal-data tables.
/// </summary>
public static class SqlStatementGuard
{
    private const string Hint = "Only a single read-only SELECT (or WITH ... SELECT) against the site's own database is allowed.";

    /// <summary>Keywords that write, change state, run code, or reach outside the database.</summary>
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "ALTER", "CREATE", "INTO", "GRANT", "REVOKE", "DENY",
        "EXEC", "EXECUTE", "DECLARE", "SET", "USE", "GO", "BEGIN", "COMMIT", "ROLLBACK", "SAVE", "TRAN", "TRANSACTION",
        "BACKUP", "RESTORE", "DBCC", "KILL", "SHUTDOWN", "RECONFIGURE", "WAITFOR", "BULK", "CHECKPOINT",
        "OPENROWSET", "OPENQUERY", "OPENDATASOURCE", "OPENXML",
    };

    /// <summary>Schemas that may lead a three-part name (<c>dbo.Table.Column</c>); anything else there is a database.</summary>
    private static readonly HashSet<string> Schemas = new(StringComparer.OrdinalIgnoreCase) { "dbo", "sys", "INFORMATION_SCHEMA" };

    /// <summary>Server-wide catalog views that list or describe other databases and sessions.</summary>
    private static readonly HashSet<string> ServerCatalog = new(StringComparer.OrdinalIgnoreCase)
    {
        "databases", "sysdatabases", "servers", "sysservers", "master_files", "sysaltfiles", "syslogins", "sql_logins", "server_principals",
    };

    /// <exception cref="RefusedException">The statement is not allowed; the message says why.</exception>
    public static void Check(string sql, bool includePersonalData)
    {
        var tokens = SqlTokenizer.Tokenize(sql);
        if (tokens.Count == 0)
        {
            throw Refuse("the query is empty.");
        }

        var last = tokens.Count;
        while (last > 0 && tokens[last - 1] is { Kind: SqlTokenKind.Symbol, Text: ";" })
        {
            last--;
        }
        if (tokens.Take(last).Any(t => t is { Kind: SqlTokenKind.Symbol, Text: ";" }))
        {
            throw Refuse("it contains more than one statement.");
        }
        if (!tokens[0].Is("SELECT") && !tokens[0].Is("WITH"))
        {
            throw Refuse($"it starts with '{tokens[0].Text}', not SELECT or WITH.");
        }

        foreach (var token in tokens.Where(t => t.IsName))
        {
            if (token.Kind == SqlTokenKind.Word && Forbidden.Contains(token.Text))
            {
                throw Refuse($"it contains {token.Text.ToUpperInvariant()}.");
            }
            // [xp_cmdshell] is still a procedure name.
            if (token.Text.StartsWith("xp_", StringComparison.OrdinalIgnoreCase) || token.Text.StartsWith("sp_", StringComparison.OrdinalIgnoreCase))
            {
                throw Refuse($"it references the procedure '{token.Text}'.");
            }
        }

        // SQL Server ignores trailing spaces when it resolves a name, so [tblBigTable ] is tblBigTable.
        foreach (var name in Names(tokens).Select(n => n.Select(part => part.TrimEnd()).ToList()))
        {
            if (name.Count > 3 || (name.Count == 3 && !Schemas.Contains(name[0])) || name.Any(part => part.Length == 0))
            {
                throw Refuse($"'{string.Join(".", name)}' names another database or server; only the site's own database may be queried.");
            }
            if (name.Any(part => ServerCatalog.Contains(part) || part.StartsWith("dm_", StringComparison.OrdinalIgnoreCase)))
            {
                throw Refuse($"'{string.Join(".", name)}' is a server-wide catalog view.");
            }
            if (!includePersonalData && name.FirstOrDefault(PersonalDataTables.IsPersonal) is { } personal)
            {
                throw new RefusedException(
                    $"Refusing SQL: '{personal}' holds personal data (form submissions, users or profiles).",
                    "Pass --include-personal-data if you really need it. Personal-data tables: " + PersonalDataTables.Description + ".");
            }
        }
    }

    /// <summary>Dotted name chains (<c>a</c>, <c>a.b</c>, <c>a..c</c>); empty parts mark omitted schemas.</summary>
    internal static IEnumerable<IReadOnlyList<string>> Names(IReadOnlyList<SqlToken> tokens)
    {
        var i = 0;
        while (i < tokens.Count)
        {
            if (!tokens[i].IsName || tokens[i].Text.StartsWith('@'))
            {
                i++;
                continue;
            }
            var parts = new List<string> { tokens[i].Text };
            i++;
            while (i < tokens.Count && tokens[i] is { Kind: SqlTokenKind.Symbol, Text: "." })
            {
                i++;
                if (i < tokens.Count && tokens[i].IsName)
                {
                    parts.Add(tokens[i].Text);
                    i++;
                }
                else if (i < tokens.Count && tokens[i] is { Kind: SqlTokenKind.Symbol, Text: "." })
                {
                    parts.Add("");
                }
                else
                {
                    // "t.*" and similar: the chain ends at the wildcard.
                    break;
                }
            }
            yield return parts;
        }
    }

    private static RefusedException Refuse(string why) => new($"Refusing SQL: {why}", Hint);
}
