using OptiCli.Core.Errors;
using OptiCli.Core.Sql;

namespace OptiCli.Integration;

/// <summary><c>sql</c> against a real database: recursive CTEs, also when <c>--limit</c> cuts their rows.</summary>
public sealed class SqlTests
{
    private const string Numbers = "WITH n AS (SELECT 1 AS i UNION ALL SELECT i + 1 FROM n WHERE i < 200) SELECT i FROM n";

    private const string Tree = """
        WITH t AS (
            SELECT pkID, fkParentID, 0 AS depth FROM tblContent WHERE fkParentID IS NULL
            UNION ALL
            SELECT c.pkID, c.fkParentID, t.depth + 1 FROM tblContent c JOIN t ON c.fkParentID = t.pkID
        )
        SELECT pkID, fkParentID, depth FROM t
        """;

    [SiteFact]
    public async Task A_recursive_cte_over_the_content_tree_returns_its_rows_also_when_cut()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);

        var cut = await RawQuery.RunAsync(site.Session.Db, Tree, 3, false, false, cancellationToken);

        Assert.Equal(3, cut.RowCount);
        Assert.True(cut.Truncated);
        Assert.True(cut.RolledBack);
    }

    [SiteFact]
    public async Task A_cte_past_maxrecursion_is_sql_servers_error_with_a_hint_also_when_cut()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);

        // With 5 rows the cut comes before the error: it used to be swallowed with the rest of the rows, and the
        // ended transaction was reported as an opticli bug.
        foreach (var limit in new[] { 5, 500 })
        {
            var error = await Assert.ThrowsAsync<UsageException>(() => RawQuery.RunAsync(site.Session.Db, Numbers, limit, false, false, cancellationToken));
            Assert.Contains("maximum recursion 100", error.Message);
            Assert.Contains("MAXRECURSION", error.Hint);
        }
    }

    [SiteFact]
    public async Task Maxrecursion_in_an_option_clause_lifts_the_limit()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);

        var result = await RawQuery.RunAsync(site.Session.Db, $"{Numbers} OPTION (MAXRECURSION 1000)", 500, false, false, cancellationToken);

        Assert.Equal(200, result.RowCount);
        Assert.Null(result.Truncated);
    }
}
