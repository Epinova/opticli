using System.Globalization;
using System.Text;

namespace OptiCli.Integration.Comparison;

/// <summary>The run's summary: one row per mismatch category, allowed ones marked with the allow-list entry.</summary>
internal static class MismatchReport
{
    private const int ExampleLength = 160;

    public static string Render(IReadOnlyList<ItemResult> results, TimeSpan elapsed)
    {
        var mismatches = results.SelectMany(r => r.Mismatches).ToList();
        var text = new StringBuilder();
        var dbTimes = results.Select(r => r.DbTime.TotalMilliseconds).Order().ToList();
        text.AppendLine(CultureInfo.InvariantCulture, $"Compared {results.Count} items ({results.Count(r => r.Item.VersionId is not null)} draft versions) in {elapsed.TotalSeconds:0.0} s.");
        text.AppendLine(CultureInfo.InvariantCulture, $"Kinds: {string.Join(", ", results.GroupBy(r => r.Item.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key.ToString().ToLowerInvariant()} {g.Count()}"))}; " +
            $"{results.Select(r => r.Item.TypeId).Distinct().Count()} content types, {results.Select(r => r.Item.LanguageId).Distinct().Count()} language branches.");
        if (dbTimes.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"DB path per item: median {dbTimes[dbTimes.Count / 2]:0} ms, p95 {dbTimes[(int)(dbTimes.Count * 0.95)]:0} ms, max {dbTimes[^1]:0} ms.");
        }
        var failing = mismatches.Where(m => KnownDifferences.Find(m) is null).ToList();
        text.AppendLine(CultureInfo.InvariantCulture, $"Mismatches: {mismatches.Count} ({failing.Count} not allowed) on {results.Count(r => r.Mismatches.Count > 0)} items.");
        text.AppendLine();

        if (mismatches.Count == 0)
        {
            return text.ToString();
        }

        text.AppendLine("| category | count | items | allowed | example |");
        text.AppendLine("|---|---:|---:|---|---|");
        foreach (var group in mismatches.GroupBy(m => m.Category).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var example = group.First();
            var allowed = KnownDifferences.Find(example)?.Name ?? "";
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {group.Key} | {group.Count()} | {group.Select(m => m.Item).Distinct().Count()} | {allowed} | {Describe(example)} |");
        }

        text.AppendLine();
        text.AppendLine("Allowed, by allow-list entry:");
        foreach (var group in mismatches.Select(m => (Mismatch: m, Known: KnownDifferences.Find(m))).Where(p => p.Known is not null)
            .GroupBy(p => p.Known!.Name).OrderByDescending(g => g.Count()))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {group.Key}: {group.Count()} on {group.Select(p => p.Mismatch.Item).Distinct().Count()} items");
        }

        text.AppendLine();
        text.AppendLine("Not allowed, first 3 per category:");
        foreach (var group in failing.GroupBy(m => m.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {group.Key}");
            foreach (var mismatch in group.Take(3))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"    {mismatch.Item} {mismatch.ContentType}.{mismatch.Field}{mismatch.Path}");
                text.AppendLine(CultureInfo.InvariantCulture, $"      db:  {Cut(mismatch.Db, 600)}");
                text.AppendLine(CultureInfo.InvariantCulture, $"      cms: {Cut(mismatch.Agent, 600)}");
            }
        }
        return text.ToString();
    }

    private static string Describe(Mismatch m) =>
        $"{m.Item} {m.ContentType}.{m.Field}{m.Path}: db {Cut(m.Db, ExampleLength)} / cms {Cut(m.Agent, ExampleLength)}".Replace("|", "\\|", StringComparison.Ordinal);

    private static string Cut(string value, int length) => value.Length <= length ? value : value[..length] + "…";
}
