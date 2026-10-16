using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;
using OptiCli.Core.Seals;

namespace OptiCli.Core.Tests.Seals;

/// <summary>
/// What the sealed run compares before it asks anything: the database it was pointed at, the project, the plan file and
/// its hash, the build, and the seal's age; then production's state, before the login is asked and right before the first
/// save. What the user would approve (target, project, plan) is refused; what only needs a new seal is a conflict.
/// </summary>
public sealed class SealCheckTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    private async Task<(Seal Seal, PlanSnapshot Plan)> SealedAsync(DateTimeOffset? created = null)
    {
        var path = _folder.Write("plan.json", """{"operations": [{"op": "set", "ref": "61104228-962d-4dd2-925c-b5b12e2373ee", "properties": {"TeaserText": "x"}}]}""");
        var plan = await PlanSnapshot.ReadAsync(path, CancellationToken.None);
        return (SealFixtures.Seal(created ?? Now.AddHours(-1), plan.Hash) with { Plan = path }, plan);
    }

    [Fact]
    public async Task A_seal_whose_target_project_plan_and_build_are_as_sealed_passes()
    {
        var (seal, plan) = await SealedAsync();

        var verified = SealCheck.Verify(seal, DatabaseKey.Of("SQL.example.net,1433", "CMS-PROD")!, SealFixtures.Project + Path.DirectorySeparatorChar, plan, SealFixtures.Build, Now);

        Assert.Same(seal, verified.Seal);
        Assert.Same(plan, verified.Plan);
    }

    [Fact]
    public async Task Another_database_project_or_plan_file_is_refused()
    {
        var (seal, plan) = await SealedAsync();
        var other = _folder.Write("other/plan.json", File.ReadAllText(plan.Path));
        var otherPlan = await PlanSnapshot.ReadAsync(other, CancellationToken.None);

        AssertRefused(SealCheck.TargetReason, () => SealCheck.Verify(seal, DatabaseKey.Of("sql.example.net", "cms-stage")!, SealFixtures.Project, plan, SealFixtures.Build, Now));
        // Another name for the same server is another key: refused, the safe way round.
        AssertRefused(SealCheck.TargetReason, () => SealCheck.Verify(seal, DatabaseKey.Of("10.0.0.5", "cms-prod")!, SealFixtures.Project, plan, SealFixtures.Build, Now));
        AssertRefused(SealCheck.ProjectReason, () => SealCheck.Verify(seal, SealFixtures.Target, SealFixtures.Project + "-other", plan, SealFixtures.Build, Now));
        AssertRefused(SealCheck.PlanReason, () => SealCheck.Verify(seal, SealFixtures.Target, SealFixtures.Project, otherPlan, SealFixtures.Build, Now));
    }

    [Fact]
    public async Task A_seal_is_good_for_a_day()
    {
        var (fresh, plan) = await SealedAsync(Now - Seal.MaxAge + TimeSpan.FromMinutes(1));
        var (stale, _) = await SealedAsync(Now - Seal.MaxAge - TimeSpan.FromMinutes(1));
        var (future, _) = await SealedAsync(Now.AddHours(1));

        SealCheck.Verify(fresh, SealFixtures.Target, SealFixtures.Project, plan, SealFixtures.Build, Now);
        AssertConflict(SealCheck.ExpiredReason, () => SealCheck.Verify(stale with { PlanHash = plan.Hash }, SealFixtures.Target, SealFixtures.Project, plan, SealFixtures.Build, Now));
        AssertConflict(SealCheck.ExpiredReason, () => SealCheck.Verify(future with { PlanHash = plan.Hash }, SealFixtures.Target, SealFixtures.Project, plan, SealFixtures.Build, Now));
    }

    [Fact]
    public async Task A_changed_plan_or_build_is_a_conflict()
    {
        var (seal, plan) = await SealedAsync();

        AssertConflict(SealCheck.PlanChangedReason, () => SealCheck.Verify(seal with { PlanHash = new string('0', 64) }, SealFixtures.Target, SealFixtures.Project, plan, SealFixtures.Build, Now));
        AssertConflict(SealCheck.BuildChangedReason, () => SealCheck.Verify(seal, SealFixtures.Target, SealFixtures.Project, plan, SealFixtures.Build with { Hash = new string('c', 64) }, Now));
        AssertConflict(SealCheck.BuildChangedReason, () => SealCheck.Verify(seal, SealFixtures.Target, SealFixtures.Project, plan, SealFixtures.Build with { Output = Path.Combine(SealFixtures.Project, "bin", "Release", "Site.dll") }, Now));
    }

    [Fact]
    public async Task Production_must_be_as_sealed()
    {
        var (seal, plan) = await SealedAsync();
        var verified = SealCheck.Verify(seal, SealFixtures.Target, SealFixtures.Project, plan, SealFixtures.Build, Now);

        SealCheck.RequireUnchanged(verified, SealFixtures.State());
        var conflict = AssertConflict(SealCheck.ProductionChangedReason, () => SealCheck.RequireUnchanged(verified, SealFixtures.State(latest: 460)));

        Assert.Contains("newer version", conflict.Message, StringComparison.Ordinal);
    }

    private static void AssertRefused(string reason, Action check) =>
        Assert.Equal(reason, SealStoreTests.Reason(Assert.Throws<RefusedException>(check)));

    private static ConflictException AssertConflict(string reason, Action check)
    {
        var conflict = Assert.Throws<ConflictException>(check);
        Assert.Equal(reason, SealStoreTests.Reason(conflict));
        return conflict;
    }
}
