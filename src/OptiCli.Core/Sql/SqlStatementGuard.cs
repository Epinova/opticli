using System.Globalization;
using System.Text;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Sql;

/// <summary>
/// Belt and braces for <c>sql</c>, which also always runs inside a rolled-back transaction: accepts one
/// SELECT (or WITH ... SELECT) statement against this database only, and nothing that writes, executes
/// code, reaches another database or server, or reads personal-data tables. In the <c>sys</c> schema only the
/// views that describe this database's own schema (<see cref="SysCatalog"/>) may be read.
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

    /// <summary>
    /// The only <c>sys</c> views a query may read: this database's own schema. Everything else in <c>sys</c> is refused,
    /// since much of it is server-wide (sessions, logins, configuration, other databases' files) or reads logs and traces.
    /// </summary>
    private static readonly HashSet<string> SysCatalog = new(StringComparer.OrdinalIgnoreCase)
    {
        "objects", "all_objects", "system_objects", "tables", "views", "columns", "all_columns", "system_columns",
        "computed_columns", "identity_columns", "types", "table_types", "schemas", "indexes", "index_columns", "xml_indexes",
        "fulltext_indexes", "fulltext_index_columns", "stats", "stats_columns", "foreign_keys", "foreign_key_columns",
        "key_constraints", "default_constraints", "check_constraints", "procedures", "parameters", "all_parameters",
        "sql_modules", "all_sql_modules", "triggers", "sequences", "synonyms", "sql_expression_dependencies",
        "partitions", "allocation_units", "extended_properties",
    };

    /// <summary>Server-wide catalog views that list or describe other databases, logins, sessions and settings.</summary>
    private static readonly HashSet<string> ServerCatalog = new(StringComparer.OrdinalIgnoreCase)
    {
        "databases", "servers", "master_files", "sql_logins", "server_principals", "server_permissions", "server_role_members",
        "configurations", "credentials", "endpoints", "linked_logins", "remote_logins", "traces", "server_audits",
        "server_file_audits", "server_triggers",
    };

    /// <summary>
    /// The SQL Server 2000 compatibility views (<c>sys.sys*</c>). SQL Server finds them without the <c>sys</c> schema
    /// too (<c>sysprocesses</c>, <c>dbo.sysobjects</c>), so they are refused wherever they appear.
    /// </summary>
    private static readonly HashSet<string> CompatibilityViews = new(StringComparer.OrdinalIgnoreCase)
    {
        "sysaltfiles", "syscacheobjects", "syscharsets", "syscolumns", "syscomments", "sysconfigures", "sysconstraints",
        "syscurconfigs", "sysdatabases", "sysdepends", "sysdevices", "sysfilegroups", "sysfiles", "sysforeignkeys",
        "sysfulltextcatalogs", "sysindexes", "sysindexkeys", "syslanguages", "syslockinfo", "syslogins", "sysmembers",
        "sysmessages", "sysobjects", "sysoledbusers", "sysopentapes", "sysperfinfo", "syspermissions", "sysprocesses",
        "sysprotects", "sysreferences", "sysremotelogins", "sysservers", "systypes", "sysusers",
    };

    /// <summary>The <c>xml</c> type's methods (case-sensitive, like SQL Server): <c>t.Data.value(...)</c> is a column, not a database.</summary>
    private static readonly HashSet<string> XmlMethods = new(StringComparer.Ordinal) { "value", "nodes", "query", "exist" };

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
            var folded = Fold(token.Text);
            if (folded.StartsWith("XP_", StringComparison.Ordinal) || folded.StartsWith("SP_", StringComparison.Ordinal))
            {
                throw Refuse($"it references the procedure '{token.Text}'.");
            }
        }

        // SQL Server ignores trailing spaces when it resolves a name, so [tblBigTable ] is tblBigTable.
        foreach (var name in Names(tokens).Select(n => n.Select(part => part.TrimEnd()).ToList()))
        {
            var dotted = string.Join(".", name);
            if (name.Count > 3 || (name.Count == 3 && !Schemas.Contains(name[0])) || name.Any(part => part.Length == 0))
            {
                throw Refuse($"'{dotted}' names another database or server; only the site's own database may be queried.");
            }

            var folded = name.Select(Fold).ToList();
            if (folded.Any(CompatibilityViews.Contains))
            {
                throw Refuse($"'{dotted}' is a compatibility view; query sys.objects, sys.columns and the like instead.");
            }
            // fn_dblog(...) finds sys.fn_dblog without the schema; dbo.fn_x is the site's own function.
            if (name.Count == 1 && IsSystemFunction(folded[0]))
            {
                throw Refuse($"'{dotted}' is a system function; they can read the transaction log, traces and other databases.");
            }
            for (var k = 1; k < name.Count; k++)
            {
                if (folded[k - 1] == "SYS")
                {
                    CheckSysObject(dotted, name[k], folded[k]);
                }
            }
            if (folded.Any(part => part.StartsWith("DM_", StringComparison.Ordinal)))
            {
                throw Refuse($"'{dotted}' is a server-wide catalog view.");
            }
            if (!includePersonalData && name.Where((_, k) => PersonalDataTables.IsPersonal(folded[k])).FirstOrDefault() is { } personal)
            {
                throw new RefusedException(
                    $"Refusing SQL: '{personal}' holds personal data (form submissions, users or profiles).",
                    "Pass --include-personal-data if you really need it. Personal-data tables: " + PersonalDataTables.Description + ".");
            }
        }
    }

    /// <summary>
    /// Dotted name chains (<c>a</c>, <c>a.b</c>, <c>a..c</c>); empty parts mark omitted schemas. An <c>xml</c> method
    /// call is left off the end (<c>t.Data.value(...)</c> is <c>t.Data</c>), but in three parts only where <c>t</c> is a
    /// table or alias in scope: otherwise SQL Server reads <c>t.Data.value(...)</c> as function <c>value</c> in database <c>t</c>.
    /// </summary>
    internal static IEnumerable<IReadOnlyList<string>> Names(IReadOnlyList<SqlToken> tokens)
    {
        var scopes = new SqlTableScopes(tokens);
        var i = 0;
        while (i < tokens.Count)
        {
            if (!tokens[i].IsName || tokens[i].Text.StartsWith('@'))
            {
                i++;
                continue;
            }
            var start = i;
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
            if (parts.Count > 1 && tokens[i - 1] is { Kind: SqlTokenKind.Word } method && XmlMethods.Contains(method.Text)
                && i < tokens.Count && tokens[i] is { Kind: SqlTokenKind.Symbol, Text: "(" }
                && (parts.Count != 3 || scopes.IsTable(start, parts[0].TrimEnd())))
            {
                parts.RemoveAt(parts.Count - 1);
            }
            yield return parts;
        }
    }

    /// <summary><c>sys.&lt;name&gt;</c>: allowed only for <see cref="SysCatalog"/>.</summary>
    private static void CheckSysObject(string dotted, string name, string folded)
    {
        if (IsSystemFunction(folded))
        {
            throw Refuse($"'{dotted}' is a system function; they can read the transaction log, traces and other databases.");
        }
        if (ServerCatalog.Contains(folded) || folded.StartsWith("DM_", StringComparison.Ordinal))
        {
            throw Refuse($"'{dotted}' is a server-wide catalog view.");
        }
        if (!SysCatalog.Contains(folded))
        {
            throw new RefusedException(
                $"Refusing SQL: 'sys.{name}' is not one of the catalog views sql may read.",
                $"In sys, only these describe the site's own database: {string.Join(", ", SysCatalog.Order())}. INFORMATION_SCHEMA views are allowed too.");
        }
    }

    private static bool IsSystemFunction(string folded) => folded.StartsWith("FN_", StringComparison.Ordinal);

    /// <summary>
    /// A name the way SQL Server may match it, for the checks that refuse: system objects resolve ignoring width as well
    /// as case (<c>sys.ｄｍ_exec_sessions</c> is <c>sys.dm_exec_sessions</c>), and an accent-insensitive collation also
    /// ignores accents. Upper case, compatibility-decomposed, without combining marks.
    /// </summary>
    private static string Fold(string name)
    {
        string decomposed;
        try
        {
            decomposed = name.Normalize(NormalizationForm.FormKD);
        }
        catch (ArgumentException)
        {
            throw Refuse($"the name '{name}' is not valid Unicode.");
        }
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(char.ToUpperInvariant(c));
            }
        }
        return folded.ToString();
    }

    private static RefusedException Refuse(string why) => new($"Refusing SQL: {why}", Hint);
}
