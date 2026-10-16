using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Seals;

/// <summary>A seal whose plan, build, target and project are what the run has now: only <see cref="SealCheck.Verify"/> makes one.</summary>
public sealed class VerifiedSeal
{
    internal VerifiedSeal(Seal seal, PlanSnapshot plan, BuildFingerprint build)
    {
        Seal = seal;
        Plan = plan;
        Build = build;
    }

    public Seal Seal { get; }

    /// <summary>The plan as the run read it, the bytes the hash was compared on; the run runs these.</summary>
    public PlanSnapshot Plan { get; }

    public BuildFingerprint Build { get; }
}

/// <summary>
/// The sealed run's checks of a seal against what it sees: first everything it can see without production
/// (<see cref="Verify"/>), then production's model and touched content (<see cref="RequireUnchanged"/>), before the user is
/// asked and again right before the first save. A difference in what the user would approve (target, project, plan) is
/// <c>refused</c>; one that only needs a new seal (time, plan or build changed, production changed) is <c>conflict</c>.
/// </summary>
public static class SealCheck
{
    public const string TargetReason = "sealTarget";
    public const string ProjectReason = "sealProject";
    public const string PlanReason = "sealPlan";
    public const string ExpiredReason = "sealExpired";
    public const string PlanChangedReason = "planChanged";
    public const string BuildChangedReason = "buildChanged";
    public const string ProductionChangedReason = "productionChanged";

    private const string SealAgain = "Seal the plan again (`opticli apply <plan> --dry-run --seal-for <name>`, or `opticli sync push` again), and run the command it prints.";

    /// <summary>Allowed clock difference for a seal made a moment "in the future".</summary>
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    /// <param name="target">The database the run was pointed at (<c>--db</c>), as roles key it.</param>
    /// <exception cref="RefusedException">Another target, project or plan file.</exception>
    /// <exception cref="ConflictException">Too old, or the plan, its files or the build changed since.</exception>
    public static VerifiedSeal Verify(Seal seal, DatabaseKey target, string projectDirectory, PlanSnapshot plan, BuildFingerprint build, DateTimeOffset now)
    {
        if (seal.Target.Key != target)
        {
            throw Refused($"Seal {seal.Id} is for '{seal.Target.Database}' on '{seal.Target.Server}', not '{target.Database}' on '{target.Server}'.", TargetReason, seal.Id,
                "Run it with --db naming the database it was sealed for, or seal the plan for this one.");
        }
        if (!SamePath(seal.Project, projectDirectory))
        {
            throw Refused($"Seal {seal.Id} was made in project {seal.Project}, not {projectDirectory}.", ProjectReason, seal.Id,
                "Run it in the project it was sealed in (--project).");
        }
        if (!SamePath(seal.Plan, plan.Path))
        {
            throw Refused($"Seal {seal.Id} is for the plan {seal.Plan}, not {plan.Path}.", PlanReason, seal.Id, "Run it with the plan it was sealed for.");
        }
        if (now < seal.Created - Skew || now > seal.Expires)
        {
            throw Conflict($"Seal {seal.Id} was made {seal.Created:u} and expired {seal.Expires:u}: a seal is good for {Seal.MaxAge.TotalHours:0} hours.", ExpiredReason, seal.Id, []);
        }
        if (!string.Equals(plan.Hash, seal.PlanHash, StringComparison.Ordinal))
        {
            throw Conflict($"The plan or a file it reads changed since seal {seal.Id} (hash {ProductionState.Short(seal.PlanHash)}, now {ProductionState.Short(plan.Hash)}).",
                PlanChangedReason, seal.Id, [$"plan {plan.Path} or one of its {plan.Files.Count} file(s)"]);
        }
        if (!string.Equals(build.Hash, seal.Build.Hash, StringComparison.Ordinal) || !SamePath(build.Output, seal.Build.Output))
        {
            throw Conflict($"The site's build changed since seal {seal.Id} ({seal.Build.Output} built {seal.Build.Built:u}, now {build.Output} built {build.Built:u}): the code that would run against production isn't the code that was sealed.",
                BuildChangedReason, seal.Id, [$"build output {build.Output}, the project's appsettings*.json or user secrets"]);
        }
        return new VerifiedSeal(seal, plan, build);
    }

    /// <exception cref="ConflictException">Production's model or content the plan touches changed since the seal; <c>details.changes</c> lists what.</exception>
    public static void RequireUnchanged(VerifiedSeal verified, ProductionState now)
    {
        var changes = ProductionState.Changes(verified.Seal.Production, now);
        if (changes.Count > 0)
        {
            throw Conflict($"Production changed since seal {verified.Seal.Id} in {changes.Count} way(s): {string.Join("; ", changes.Take(5))}{(changes.Count > 5 ? "; …" : "")}.",
                ProductionChangedReason, verified.Seal.Id, changes);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static RefusedException Refused(string message, string reason, string id, string hint) =>
        new(message, hint) { Details = new { reason, seal = id } };

    private static ConflictException Conflict(string message, string reason, string id, IReadOnlyList<string> changes) =>
        new(message, SealAgain) { Details = new { reason, seal = id, changes } };
}
