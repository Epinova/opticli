using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Protocol;

namespace OptiCli.Core.Seals;

/// <summary>
/// Content types and properties only production's database has: classes or properties removed from code that the CMS kept
/// for their content (or newer code in production). A sealed plan never writes them, so a seal accepts them by name: it
/// records exactly which, the user sees them in the summary, and the run's site may report those and nothing else. Every
/// other difference between the build and production (what only the build has, a changed property type, stores,
/// migrations, the schema) is still refused.
/// </summary>
public static class SealedLeftovers
{
    /// <summary><c>details.reason</c> when the run's site finds a difference the seal didn't accept.</summary>
    public const string Reason = "driftNotSealed";

    /// <summary>A drift report's leftovers (types and properties only in the database), by name, and every other difference.</summary>
    public static (IReadOnlyList<string> Leftovers, IReadOnlyList<(string Section, DriftItem Item)> Other) Split(DriftReport report)
    {
        var blocking = report.Sections().Where(s => !s.Item.Informational).ToList();
        bool IsLeftover((string Section, DriftItem Item) s) =>
            s.Section is "contentTypes" or "properties" && s.Item.Ahead == DriftAhead.Database
            && string.Equals(s.Item.Difference, DriftItem.OnlyInDatabase, StringComparison.Ordinal);
        return (Sorted(blocking.Where(IsLeftover).Select(s => s.Item.Name)), blocking.Where(s => !IsLeftover(s)).ToList());
    }

    /// <summary>
    /// What the run's site will find only in production's database: the local leftovers production has too (the local
    /// database is a copy of production's model, so it has them for the same reason), and what only production has.
    /// </summary>
    /// <param name="local">The local site's leftovers (<c>Type</c>, <c>Type.Property</c>).</param>
    /// <param name="onlyInProduction">Types and properties production has and the local build doesn't.</param>
    public static IReadOnlyList<string> Expected(IEnumerable<string> local, IEnumerable<string> onlyInProduction, CmsModel production)
    {
        bool InProduction(string name)
        {
            var dot = name.IndexOf('.', StringComparison.Ordinal);
            var type = production.Types.FirstOrDefault(t => string.Equals(t.Name, dot < 0 ? name : name[..dot], StringComparison.OrdinalIgnoreCase));
            return type is not null && (dot < 0 || production.PropertiesOf(type.Id).Any(p => string.Equals(p.Name, name[(dot + 1)..], StringComparison.OrdinalIgnoreCase)));
        }
        return Sorted(local.Where(InProduction).Concat(onlyInProduction));
    }

    public static IReadOnlyList<string> Sorted(IEnumerable<string> names) =>
        names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The run's site's drift against what the seal accepted: the fingerprint to confirm the writes with (null when nothing
    /// differs).
    /// </summary>
    /// <exception cref="ConflictException">A difference that isn't one of <paramref name="sealedLeftovers"/>.</exception>
    public static string? Accept(DriftReport report, IReadOnlyList<string>? sealedLeftovers)
    {
        if (report.Fingerprint is null)
        {
            return null;
        }
        var (leftovers, other) = Split(report);
        var accepted = new HashSet<string>(sealedLeftovers ?? [], StringComparer.OrdinalIgnoreCase);
        var unsealed = other.Select(o => $"{o.Item.Name} ({o.Item.Difference})")
            .Concat(leftovers.Where(l => !accepted.Contains(l)).Select(l => $"{l} ({DriftItem.OnlyInDatabase}, not in the seal)"))
            .ToList();
        if (unsealed.Count > 0)
        {
            throw new ConflictException(
                $"Stopped before writing: the build and production differ in what the seal didn't accept: {string.Join("; ", unsealed.Take(8))}{(unsealed.Count > 8 ? "; …" : "")}.",
                "Production changed since the seal (a deploy, or admin mode), or the build did. Nothing was written; the seal is used: seal again.")
            {
                Details = new { reason = Reason, unsealed, drift = report },
            };
        }
        return report.Fingerprint;
    }
}
