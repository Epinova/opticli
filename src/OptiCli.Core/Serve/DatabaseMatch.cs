using OptiCli.Core.Safety;
using OptiCli.Protocol;

namespace OptiCli.Core.Serve;

/// <summary>
/// Compares the database a running site reports (ping) with the one opticli reads. They must be the
/// same: reads go straight to SQL and writes through the site, so a mismatch would write one database
/// while showing another.
/// </summary>
public static class DatabaseMatch
{
    /// <param name="requirePinned">True when opticli started the site with a pin; false for <c>env</c>, where the site's own configuration may supply it.</param>
    /// <returns>Why the site must not be used, or null when it matches.</returns>
    public static string? Problem(DatabaseTarget reported, VerifiedConnectionString expected, bool requirePinned) =>
        Problem(reported, expected.Server, expected.Database, expected.IsLocal, requirePinned);

    /// <param name="expectedLocal">opticli reads a local database, so the site must use a local one too.</param>
    public static string? Problem(DatabaseTarget reported, string expectedServer, string? expectedDatabase, bool expectedLocal, bool requirePinned)
    {
        if (expectedLocal && !reported.Local)
        {
            return $"the site's CMS uses non-local server '{reported.Server ?? "<none>"}'";
        }
        if (requirePinned && !reported.Pinned)
        {
            return "something in the site replaced the connection string opticli pinned";
        }
        if (!Same(reported.Server, expectedServer) || !Same(reported.Name, expectedDatabase))
        {
            return $"the site uses database '{reported.Name ?? "<none>"}' on '{reported.Server ?? "<none>"}', but opticli reads '{expectedDatabase ?? "<none>"}' on '{expectedServer}'";
        }
        return null;
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
