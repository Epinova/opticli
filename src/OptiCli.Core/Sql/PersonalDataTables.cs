namespace OptiCli.Core.Sql;

/// <summary>
/// Tables and views that hold personal data rather than content: form submissions, user and membership
/// stores, per-user notifications and profiles. <c>sql</c> refuses to read them unless
/// <c>--include-personal-data</c> is given; no other command reads them at all.
/// </summary>
public static class PersonalDataTables
{
    /// <summary>
    /// Exact names. Forms submissions live in the Dynamic Data Store (<c>tblBigTable</c> and its
    /// <c>VW_FormData_*</c> views); <c>tblSystemBigTable</c> also holds user profiles and site secrets.
    /// </summary>
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "tblXFormData",
        "tblBigTable",
        "tblBigTableReference",
        "tblSystemBigTable",
        "tblSynchedUser",
        "tblSynchedUserRelations",
        "tblSynchedUserRole",
        "tblNotificationMessage",
        "tblNotificationSubscription",
    };

    /// <summary>Name prefixes: Dynamic Data Store views, ASP.NET Identity and legacy membership tables.</summary>
    private static readonly string[] Prefixes = ["VW_", "AspNet", "aspnet_"];

    public static bool IsPersonal(string name) =>
        Names.Contains(name) || Prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static string Description => $"{string.Join(", ", Names.Order())}, and names starting with {string.Join(", ", Prefixes)}";
}
