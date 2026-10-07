using System.Diagnostics;
using OptiCli.Integration.Comparison;
using OptiCli.Integration.Sampling;
using Xunit.Abstractions;

namespace OptiCli.Integration;

/// <summary>
/// The agent oracle: the DB read path must show what the CMS itself loads. Samples content across kinds,
/// types and languages (plus recent drafts, and on CMS 13 all Visual Builder content, blueprints and content variations),
/// reads each item both ways and diffs property by property, and compositions node by node.
/// </summary>
public sealed class AgentOracleTests(ITestOutputHelper output)
{
    [SiteFact]
    public async Task Db_read_path_matches_what_the_cms_loads()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);

        var planned = SiteSettings.PlanPath is { } plan ? await ContentSampler.PlanAsync(site.Session, plan, cancellationToken) : [];
        var sample = planned
            .Concat(await ContentSampler.BranchesAsync(site.Session, SiteSettings.Sample, SiteSettings.Seed, cancellationToken))
            .Concat(await ContentSampler.RecentDraftsAsync(site.Session, SiteSettings.Drafts, cancellationToken))
            .Concat(await ContentSampler.VisualBuilderAsync(site.Session, SiteSettings.Sample, cancellationToken))
            .Distinct()
            .ToList();

        var comparer = new ItemComparer(site);
        var watch = Stopwatch.StartNew();
        var results = new List<ItemResult>();
        foreach (var item in sample)
        {
            results.Add(await comparer.CompareAsync(item, cancellationToken));
        }

        var report = MismatchReport.Render(results, watch.Elapsed);
        output.WriteLine(report);
        if (SiteSettings.ReportPath is { } path)
        {
            await File.WriteAllTextAsync(path, report, cancellationToken);
            // Every mismatch, one JSON object per line, for digging into a category.
            await File.WriteAllLinesAsync(Path.ChangeExtension(path, ".jsonl"),
                results.SelectMany(r => r.Mismatches).Select(m => System.Text.Json.JsonSerializer.Serialize(m)), cancellationToken);
        }

        var failing = results.SelectMany(r => r.Mismatches).Where(m => KnownDifferences.Find(m) is null).ToList();
        Assert.True(failing.Count == 0, $"{failing.Count} mismatches between the DB read path and the CMS are not on the allow-list:\n{report}");
    }
}
