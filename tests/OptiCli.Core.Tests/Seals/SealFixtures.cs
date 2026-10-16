using OptiCli.Core.Configuration;
using OptiCli.Core.Seals;

namespace OptiCli.Core.Tests.Seals;

/// <summary>Seals and production states for the tests: a target, a plan's hash, a build, one touched page.</summary>
internal static class SealFixtures
{
    public static readonly Guid Page = Guid.Parse("61104228-962d-4dd2-925c-b5b12e2373ee");

    public static readonly Guid Start = Guid.Parse("7b08c7d5-7585-47e5-add7-5822da68c3ce");

    public static readonly DatabaseKey Target = DatabaseKey.Of("sql.example.net", "cms-prod")!;

    public static readonly string Project = Path.Combine(Path.GetTempPath(), "opticli-tests", "site");

    public static readonly string PlanPath = Path.Combine(Project, "plans", "plan.json");

    public static readonly BuildFingerprint Build = new(Path.Combine(Project, "bin", "Site.dll"), new DateTime(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc), new string('b', 64), 120, "abc123def456", false);

    public static ProductionState State(int latest = 456, DateTime? saved = null, int? published = 456, Guid? parent = null, string model = "m1") =>
        new(model, [new SealedItem(Page, true, false, parent ?? Start, "StandardPage", "About us", [new SealedBranch("en", latest, saved ?? new DateTime(2026, 10, 16, 7, 0, 0), published)])]);

    public static Seal Seal(DateTimeOffset? created = null, string planHash = "a1b2", BuildFingerprint? build = null, ProductionState? state = null) =>
        new(
            SealStore.NewId(),
            created ?? DateTimeOffset.UtcNow,
            "apply",
            Project,
            new SealTarget("sql.example.net", "cms-prod", Target.Server, Target.Database),
            PlanPath,
            planHash,
            1,
            new SealOptions(UpdateExisting: false, Publish: true, RequestApproval: false),
            build ?? Build,
            state ?? State(),
            new SealSummary(1, new Dictionary<string, int> { ["set"] = 1 }, new Dictionary<string, int>(), 1, 0, 0, 1),
            "cms-local on localhost",
            "0.0.0");
}
