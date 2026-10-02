using System.Globalization;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Protocol;

namespace OptiCli.Core.Drift;

/// <summary>
/// What <c>serve</c> compares before it starts a site against a shared database: the build's EF Core migrations against
/// <c>__EFMigrationsHistory</c>, and the CMS schema version the build's packages need against the database's. Both are
/// read without running the site.
/// </summary>
/// <param name="Drift">The differences, handed to the site agent for its report.</param>
/// <param name="PendingMigrations">Migrations in the build that the database lacks: a site that migrates at startup would apply them.</param>
/// <param name="SchemaRefusal">Why the CMS won't start against this database at all (its schema is older, or newer than it accepts); null when it will.</param>
public sealed record StartupCheck(StartupDrift Drift, IReadOnlyList<DriftItem> PendingMigrations, string? SchemaRefusal, string? SchemaHint)
{
    public const string PendingMigrationsReason = "pendingMigrations";

    public const string SchemaReason = "schemaVersion";

    /// <summary>Notes worth a warning on <c>serve</c>'s output.</summary>
    public IReadOnlyList<string> Warnings => Drift.Notes;

    /// <exception cref="RefusedException">The CMS wouldn't start, or the site could apply migrations to the shared database.</exception>
    public void ThrowIfBlocked(bool allowPendingMigrations)
    {
        if (SchemaRefusal is { } refusal)
        {
            throw new RefusedException(refusal, SchemaHint) { Details = new { reason = SchemaReason, schema = Drift.Schema } };
        }
        if (PendingMigrations.Count > 0 && !allowPendingMigrations)
        {
            var shown = string.Join(", ", PendingMigrations.Take(5).Select(m => m.Name));
            var more = PendingMigrations.Count > 5 ? $" and {PendingMigrations.Count - 5} more" : "";
            throw new RefusedException(
                $"This build has {PendingMigrations.Count} EF Core migration(s) the shared database doesn't ({shown}{more}). If the site migrates its database when it starts (Database.Migrate()), starting it would apply them for everyone, and opticli can't turn that off.",
                "Check out and build what is deployed there, or let the normal deployment apply the migrations first. If the site doesn't migrate at startup, or that context uses another database, start it with --allow-pending-migrations: writes then still stop until the user confirms the drift.")
            {
                Details = new { reason = PendingMigrationsReason, migrations = PendingMigrations },
            };
        }
    }
}

public static class StartupDriftCheck
{
    public const string HistoryTable = "__EFMigrationsHistory";

    /// <param name="outputDirectory">The folder of the site's build output, whose assemblies are scanned.</param>
    public static async Task<StartupCheck> RunAsync(CmsDatabase db, string outputDirectory, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var cmsData = Path.Combine(outputDirectory, BuildScanner.CmsDataAssembly);
        var (schema, refusal, hint) = Schema(await SchemaVersionAsync(db, notes, cancellationToken),
            BuildScanner.RequiredSchemaVersion(cmsData), BuildScanner.AssemblyVersion(cmsData), notes);
        var migrations = Migrations(BuildScanner.Migrations(outputDirectory), await AppliedMigrationsAsync(db, cancellationToken), notes);
        var drift = new StartupDrift { Migrations = migrations, Schema = schema, Notes = notes };
        return new StartupCheck(drift, migrations.Where(m => m.Ahead == DriftAhead.Local).ToList(), refusal, hint);
    }

    /// <summary>
    /// EPiServer.Framework from this version on starts against a schema one version newer than its packages need (for a
    /// rolling deployment). 12.15 and earlier don't; 12.16 wasn't checked, so it counts as not.
    /// </summary>
    public static readonly Version AcceptsOneNewerFrom = new(12, 17);

    /// <summary>
    /// The CMS starts against a schema of exactly the version its packages need, and from <see cref="AcceptsOneNewerFrom"/>
    /// one newer. Older doesn't start in shared mode, where schema updates are off; newer than that never does.
    /// </summary>
    /// <param name="database"><c>sp_DatabaseVersion</c>; null when it couldn't be read.</param>
    /// <param name="required">What the build's <c>EPiServer.Data</c> needs; null when it couldn't be read.</param>
    /// <param name="framework">The build's EPiServer.Data (EPiServer.Framework) version; null when unknown, which counts as not accepting newer.</param>
    public static (IReadOnlyList<DriftItem> Items, string? Refusal, string? Hint) Schema(int? database, int? required, Version? framework, List<string> notes)
    {
        if (database is not { } stored || required is not { } needed)
        {
            notes.Add(database is null
                ? "The CMS schema version wasn't compared: the database's couldn't be read."
                : $"The CMS schema version wasn't compared: the build output has no readable {BuildScanner.CmsDataAssembly}.");
            return ([], null, null);
        }
        if (stored < needed)
        {
            return ([new DriftItem("CMS", DriftAhead.Local, Versions(stored, needed))],
                string.Create(CultureInfo.InvariantCulture, $"The shared database's CMS schema is version {stored}, and this build's EPiServer packages need {needed}. Against a shared database the CMS doesn't update the schema, so the site won't start."),
                "Check out and build what is deployed there, or update that environment first; opticli never updates a shared database's schema.");
        }
        if (stored == needed)
        {
            return ([], null, null);
        }
        if (stored == needed + 1 && framework is { } version && version >= AcceptsOneNewerFrom)
        {
            return ([new DriftItem("CMS", DriftAhead.Database, $"{Versions(stored, needed)}; the CMS accepts one version newer, but the environment runs newer CMS packages")], null, null);
        }
        var packages = framework is null ? "" : string.Create(CultureInfo.InvariantCulture, $" (EPiServer.Framework {framework.ToString(framework.Build >= 0 ? 3 : 2)})");
        return ([new DriftItem("CMS", DriftAhead.Database, Versions(stored, needed))],
            string.Create(CultureInfo.InvariantCulture, $"The shared database's CMS schema is version {stored}, newer than this build's EPiServer packages{packages} support ({needed}), so the site won't start."),
            "Update the EPiServer packages to what is deployed there (pull), and build.");
    }

    /// <summary>Migrations in the build the history lacks are ahead locally; ones only the history has, in the database.</summary>
    /// <param name="applied">The ids in <c>__EFMigrationsHistory</c>; null when the database has no such table.</param>
    public static IReadOnlyList<DriftItem> Migrations(IReadOnlyList<EfMigration> local, IReadOnlyCollection<string>? applied, List<string> notes)
    {
        if (applied is null)
        {
            if (local.Count > 0)
            {
                var contexts = string.Join(", ", local.Select(m => m.Context ?? "?").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
                notes.Add($"The database has no {HistoryTable} table, so this build's {local.Count} EF Core migration(s) ({contexts}) weren't compared. Their context probably uses another database; if it uses this one and the site migrates at startup, starting it creates tables here.");
            }
            return [];
        }
        var appliedIds = applied.ToHashSet(StringComparer.Ordinal);
        var localIds = local.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return local
            .Where(m => !appliedIds.Contains(m.Id))
            .DistinctBy(m => m.Id)
            .Select(m => new DriftItem(m.Id, DriftAhead.Local, $"in this build ({m.Context ?? m.Assembly}), not applied to the database"))
            .Concat(applied.Where(id => !localIds.Contains(id)).Distinct(StringComparer.Ordinal)
                .Select(id => new DriftItem(id, DriftAhead.Database, "applied to the database, not in this build")))
            .ToList();
    }

    private static string Versions(int database, int required) =>
        string.Create(CultureInfo.InvariantCulture, $"schema version {database} in the database, {required} needed by this build's packages");

    private static async Task<int?> SchemaVersionAsync(CmsDatabase db, List<string> notes, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await db.QueryAsync("DECLARE @v int; EXEC @v = dbo.sp_DatabaseVersion; SELECT @v AS SchemaVersion;",
                r => r.IsDBNull(0) ? (int?)null : r.GetInt32(0), cancellationToken);
            return rows.Count > 0 ? rows[0] : null;
        }
        catch (OptiCliException ex) when (ex.Code is ErrorCode.NotFound or ErrorCode.Unreachable)
        {
            notes.Add($"The database's CMS schema version couldn't be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Every <c>MigrationId</c> in a <c>__EFMigrationsHistory</c> table, in any schema; null when there is none.</summary>
    private static async Task<IReadOnlyCollection<string>?> AppliedMigrationsAsync(CmsDatabase db, CancellationToken cancellationToken)
    {
        var schemas = await db.QueryAsync($"SELECT s.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.name = '{HistoryTable}'",
            r => r.GetString(0), cancellationToken);
        if (schemas.Count == 0)
        {
            return null;
        }
        var ids = new List<string>();
        foreach (var schema in schemas)
        {
            ids.AddRange(await db.QueryAsync($"SELECT MigrationId FROM [{schema.Replace("]", "]]", StringComparison.Ordinal)}].[{HistoryTable}]",
                r => r.GetString(0), cancellationToken));
        }
        return ids;
    }
}
