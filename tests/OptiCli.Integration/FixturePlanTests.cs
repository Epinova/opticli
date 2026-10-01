using OptiCli.Core.Writes;

namespace OptiCli.Integration;

/// <summary>The edge-case fixture plan (<see cref="SiteSettings.PlanPath"/>), which setup.sh has already applied.</summary>
public sealed class FixturePlanTests
{
    [SiteFact]
    public async Task Running_the_fixture_plan_again_would_change_nothing()
    {
        if (SiteSettings.PlanPath is not { } path)
        {
            return;
        }
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent), updateExisting: true);
        var plan = WritePlan.Parse(await File.ReadAllTextAsync(path, cancellationToken));

        var run = await new PlanRunner(site.Session, writes, Path.GetDirectoryName(Path.GetFullPath(path))!).RunAsync(plan, dryRun: true, publishAll: false, cancellationToken);

        // A change here is a value that doesn't read back as it was written (the next run saves it again).
        var changed = run.Operations
            .Where(o => o.Result is WriteOutput { Changes.Count: > 0 })
            .Select(o => $"operations[{o.Index}] ({o.Op} {o.Id}): {string.Join(", ", ((WriteOutput)o.Result!).Changes.Select(c => c.Property))}")
            .ToList();
        Assert.True(changed.Count == 0, string.Join("\n", changed));
    }
}
