using System.Data;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Errors;
using OptiCli.Core.Safety;

namespace OptiCli.Core.Data;

/// <summary>
/// The only place opticli opens a SQL connection. It accepts nothing but a
/// <see cref="VerifiedConnectionString"/>, re-checks that SqlClient connects to the server that was checked, and
/// runs fixed, parameterised queries owned by the readers in this assembly.
/// </summary>
public sealed class CmsDatabase : IAsyncDisposable
{
    private readonly SqlConnection _connection;

    private CmsDatabase(SqlConnection connection, VerifiedConnectionString connectionString)
    {
        _connection = connection;
        ConnectionString = connectionString;
    }

    public VerifiedConnectionString ConnectionString { get; }

    public string Server => ConnectionString.Server;

    public string? Database => ConnectionString.Database;

    public static async Task<CmsDatabase> OpenAsync(VerifiedConnectionString connectionString, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString.Value) { ApplicationName = "opticli" };
        if (connectionString.IsLocal)
        {
            // A hint only (it matters for availability-group listeners); read-only is enforced by opticli running
            // fixed SELECTs, not by the server. Left off for remote servers: on Azure SQL it routes to a read
            // replica, which can lag behind the writes the site just made.
            builder.ApplicationIntent = ApplicationIntent.ReadOnly;
        }

        var connection = new SqlConnection(builder.ConnectionString);
        if (connectionString.IsLocal
            ? !ConnectionSafety.IsLocalDataSource(connection.DataSource)
            : !string.Equals(connection.DataSource, connectionString.Server, StringComparison.OrdinalIgnoreCase))
        {
            await connection.DisposeAsync();
            throw new RefusedException($"Refusing to connect: server '{connection.DataSource}' is not the one that was checked ('{connectionString.Server}').");
        }

        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (SqlException ex)
        {
            await connection.DisposeAsync();
            throw new UnreachableException(
                $"Could not connect to database '{connectionString.Database}' on '{connectionString.Server}': {ex.Message}",
                connectionString.IsLocal
                    ? "Check that the local SQL Server is running and the database exists; `opticli doctor` shows which connection string was used and where it came from."
                    : "Check the network, the server's firewall (Azure SQL allows only listed client IPs) and the login; for Microsoft Entra authentication, sign in with `az login` first. `opticli doctor` shows which connection string was used.",
                ex);
        }

        return new CmsDatabase(connection, connectionString);
    }

    internal async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        Func<SqlDataReader, T> map,
        CancellationToken cancellationToken,
        params SqlParameter[] parameters)
    {
        await using var command = new SqlCommand(sql, _connection) { CommandType = CommandType.Text };
        command.Parameters.AddRange(parameters);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<T>();
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(map(reader));
            }
            return rows;
        }
        catch (SqlException ex) when (ex.Number is 208 or 2812)
        {
            // 208 = invalid object name, 2812 = unknown stored procedure.
            throw new NotFoundException(
                $"Database '{Database}' does not look like an Optimizely CMS database: {ex.Message}",
                "Check that the connection string points at the site's CMS database (`opticli doctor`).");
        }
        catch (SqlException ex)
        {
            throw new UnreachableException(
                $"The query on database '{Database}' failed: {ex.Message}",
                "A timeout or dropped connection usually passes on a retry; a permission error means the login can't read the CMS tables (`opticli doctor`).",
                ex);
        }
    }

    /// <summary>
    /// Runs caller-supplied SQL (already checked by <see cref="Sql.SqlStatementGuard"/>) inside a
    /// transaction that is always rolled back, so even a statement the guard missed changes nothing.
    /// </summary>
    /// <param name="maxRows">Rows returned; one more is read to tell whether the result was cut.</param>
    /// <param name="convert">Turns each non-null value into its output form.</param>
    /// <exception cref="UsageException">SQL Server rejected the query.</exception>
    internal async Task<Sql.SqlResult> QueryRolledBackAsync(string sql, int maxRows, Func<object, object?> convert, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await _connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await using var command = new SqlCommand(sql, _connection, transaction) { CommandType = CommandType.Text, CommandTimeout = 60 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var columns = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = string.IsNullOrEmpty(reader.GetName(i)) ? $"column{i + 1}" : reader.GetName(i);
                var unique = name;
                for (var n = 2; columns.Contains(unique, StringComparer.OrdinalIgnoreCase); n++)
                {
                    unique = $"{name}_{n}";
                }
                columns.Add(unique);
            }

            var rows = new List<Dictionary<string, object?>>();
            var truncated = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count == maxRows)
                {
                    truncated = true;
                    break;
                }
                var row = new Dictionary<string, object?>(columns.Count);
                for (var i = 0; i < columns.Count; i++)
                {
                    row[columns[i]] = reader.IsDBNull(i) ? null : convert(reader.GetValue(i));
                }
                rows.Add(row);
            }
            return new Sql.SqlResult(columns, rows, rows.Count, truncated ? true : null, RolledBack: true);
        }
        catch (SqlException ex)
        {
            throw new UsageException($"SQL Server rejected the query: {ex.Message}", "Check table and column names with `opticli sql \"SELECT name FROM sys.tables\"`.");
        }
        finally
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or SqlException)
            {
                // The server already ended the transaction (deadlock victim, dropped connection): nothing is left to
                // roll back, and the original error is the one worth reporting.
            }
        }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
