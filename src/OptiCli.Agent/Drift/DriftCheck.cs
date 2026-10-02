using System.Text.Json;
using OptiCli.Agent.Hosting;
using OptiCli.Protocol;

namespace OptiCli.Agent.Drift;

/// <summary>
/// The site's <see cref="DriftReport"/>: what differs between its code and the shared database it runs against. Worked
/// out once per run, the first time it is asked for (by then the CMS has started), and kept: the local view of the
/// database's content types is cached anyway, so a restart of <c>serve</c> is what checks again.
/// </summary>
/// <remarks>
/// It fails closed: a part that can't be compared becomes a difference of its own, so writes still stop and the user
/// decides.
/// </remarks>
internal sealed class DriftCheck
{
    public const string LocalNote = "The database is local: the content type sync commits the local models to it, so there is nothing to compare.";

    public const string CodeOnlyNote = "[AllowedTypes], validation attributes and other rules that live only in code aren't stored in the database, so they aren't compared.";

    public const string NoStartupNote = "EF Core migrations and the CMS schema version weren't compared: `opticli serve` does that before it starts the site, and this site wasn't started by it.";

    private readonly Lazy<DriftReport> _report;

    public DriftCheck(IServiceProvider services, AgentSettings settings)
        : this(() => Compute(services, settings))
    {
    }

    /// <param name="compute">Runs once, the first time <see cref="Report"/> is read.</param>
    internal DriftCheck(Func<DriftReport> compute) => _report = new Lazy<DriftReport>(compute);

    public DriftReport Report => _report.Value;

    internal static DriftReport Compute(IServiceProvider services, AgentSettings settings)
    {
        if (!settings.SharedDatabase)
        {
            return new DriftReport { Checked = false, Notes = [LocalNote] };
        }
        var notes = new List<string>();
        var startup = ReadStartup(settings.DriftFile, notes);
        var (types, properties) = Guarded("content types and properties", () => ContentModelScan.Compare(services), ex => ([Failed("content model", ex)], []));
        var stores = Guarded("Dynamic Data Store types", () => StoreDrift.Compare(services), ex => [Failed("Dynamic Data Store", ex)]);
        notes.Add(CodeOnlyNote);
        var report = DriftReport.Create(types, properties, startup.Migrations, stores, startup.Schema, [.. startup.Notes, .. notes]);
        Console.Error.WriteLine($"[opticli] drift: {report.Describe()}");
        return report;
    }

    /// <summary>What <c>serve</c> compared before the start; nothing (with a note) when the site wasn't started by it.</summary>
    internal static StartupDrift ReadStartup(string? path, List<string> notes)
    {
        if (path is null)
        {
            notes.Add(NoStartupNote);
            return new StartupDrift();
        }
        try
        {
            return JsonSerializer.Deserialize<StartupDrift>(File.ReadAllText(path), AgentJson.Options) ?? new StartupDrift();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            notes.Add($"What `opticli serve` compared before the start (EF Core migrations, the CMS schema version) couldn't be read from {path}: {ex.Message}");
            return new StartupDrift();
        }
    }

    private static T Guarded<T>(string what, Func<T> compare, Func<Exception, T> failed)
    {
        try
        {
            return compare();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[opticli] drift: comparing {what} failed: {ex}");
            return failed(ex);
        }
    }

    private static DriftItem Failed(string what, Exception ex) =>
        new($"({what})", DriftAhead.Unknown, $"couldn't be compared ({ex.GetType().Name}: {ex.Message}); the site's log has the details");
}
