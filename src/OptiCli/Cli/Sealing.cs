using System.Globalization;
using System.Text;
using OptiCli.Core.Configuration;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Core.Safety;
using OptiCli.Core.Seals;
using OptiCli.Core.Serve;
using OptiCli.Core.SourceScan;
using OptiCli.Core.Sync;
using OptiCli.Core.Writes;

namespace OptiCli.Cli;

/// <summary>A model difference between the local build and production, by side.</summary>
/// <param name="Side"><c>local</c>: the local build has it and production doesn't (or has it otherwise); <c>production</c>: the other way round.</param>
internal sealed record SealModelDifference(string Side, string Type, string? Property, string Difference);

/// <param name="UserCommand">The command the user runs in their own terminal; an agent hands it over and never runs it.</param>
/// <param name="Leftovers">Content types and properties only production's database has, accepted by name (<see cref="SealedLeftovers"/>).</param>
internal sealed record SealResult(
    string Seal,
    string File,
    DateTimeOffset Expires,
    SealedDatabase Target,
    string Plan,
    string PlanHash,
    int PlanFiles,
    IReadOnlyList<string> Options,
    SealSummary Summary,
    SealedBuild Build,
    string Model,
    string ValidatedOn,
    string UserCommand,
    IReadOnlyList<string> NotChecked,
    IReadOnlyList<string> Leftovers);

internal sealed record SealedDatabase(string Server, string? Database, string Role);

internal sealed record SealedBuild(string Output, DateTime Built, string? Commit, bool? Uncommitted);

/// <summary>
/// Seals a plan for production (<c>apply --dry-run --seal-for</c>, <c>sync push</c>): what an agent may do. Checks the plan
/// against the local site (a dry run), compares production's content model with the local build's (the local database
/// holds it once its site started on this build), reads production's versions of everything the plan touches, and keeps
/// all of it in a seal (<see cref="SealStore"/>), with the command the user runs.
/// </summary>
internal static class Sealing
{
    /// <summary><c>details.reason</c> when the database checked against isn't a local one.</summary>
    public const string ValidationReason = "sealValidation";

    /// <summary><c>details.reason</c> when production's content model and the local build's differ.</summary>
    public const string ModelReason = "modelDiffers";

    /// <summary>What a seal doesn't check, said in every result.</summary>
    public static readonly IReadOnlyList<string> NotChecked =
    [
        "production's servers keep what they have cached unless the user gives production's event bus (Azure Service Bus) at the run: without it, restart the site (every instance) after the run; the result's restartNeeded says which",
        "the run starts this build in Development against production: its startup code, event handlers (search indexing, webhooks, emails) and configuration run with it",
        "media files only reach production when the user gives its blob storage (Azure) at the run; without it, a plan with upload steps is refused there",
        "deletes, unpublish steps and access rights: a sealed apply never does them; an expiry (StopPublish) that has passed, set by the plan, still takes content offline (summary.expiring counts those steps)",
    ];

    /// <summary>The production database named by <c>--seal-for</c>, <c>--to</c> or the run's <c>--db</c>: like <c>--db</c>, never <c>--connection</c> or <c>OPTICLI_DB</c>.</summary>
    /// <exception cref="RefusedException">It isn't production (<c>details.reason</c>: <see cref="SealCheck.TargetReason"/>).</exception>
    public static (ConnectionCandidate Candidate, VerifiedConnectionString Connection) Production(CliContext context, string name, string option)
    {
        var resolution = ConnectionResolver.Resolve(context.ConnectionRequest with { Explicit = null, Profile = null, Database = name }, context.TryGetProject(out _), context.Environment, runConnection: false);
        var connection = resolution.Require();
        var candidate = resolution.Chosen!;
        if (candidate.Role != DatabaseRole.Prod || candidate.Target is null)
        {
            throw new RefusedException(
                $"{option} {name} names '{candidate.Database}' on '{candidate.Server}', which doesn't have the role prod: a seal is only for production.",
                "To a dev or test database, plain `opticli apply <plan> --db <name>` writes (shared mode, drift gate). A production database is one the user gave the role prod (`opticli db add <name> --role prod`).")
            {
                Details = new { reason = SealCheck.TargetReason, candidate.Server, candidate.Database, role = candidate.Role },
            };
        }
        return (candidate, connection);
    }

    /// <summary>The database a seal's dry run uses: the development database, which must be local (its site committed this build's model).</summary>
    /// <exception cref="RefusedException">It isn't a local one.</exception>
    public static ConnectionCandidate RequireLocal(CliContext context, ConnectionCandidate production, string what)
    {
        var chosen = context.ResolveConnection().Chosen ?? throw context.ResolveConnection().Failure!;
        if (chosen.Role != DatabaseRole.Local || chosen.Target == production.Target)
        {
            throw new RefusedException(
                $"{what} checks the plan against a local database (role local), and '{chosen.Database}' on '{chosen.Server}' has the role {(chosen.Role is { } role ? DatabaseRoles.Name(role) : "none (a remote database)")}.",
                "Seal from the local development database, a recent pull of production (`opticli sync pull --from <name>`), with its site running on this build (`opticli serve`).")
            {
                Details = new { reason = ValidationReason, chosen.Server, chosen.Database, role = chosen.Role },
            };
        }
        return chosen;
    }

    /// <summary>Checks and seals <paramref name="plan"/>; nothing is written but the seal.</summary>
    /// <param name="productionName">What the user's command names production with (<c>--db</c>).</param>
    /// <exception cref="RefusedException">The plan does something a sealed apply doesn't.</exception>
    /// <exception cref="ConflictException">Production's content model differs from the local build's (<see cref="ModelReason"/>).</exception>
    /// <exception cref="OptiCliException">The dry run failed (nothing is sealed).</exception>
    /// <param name="warnings">Gets what the caller should say besides the result.</param>
    public static async Task<SealResult> SealAsync(CliContext context, PlanSnapshot plan, string productionName, SealOptions options, string origin,
        ContentSession local, ConnectionCandidate production, ContentSession productionSession, List<string> warnings, CancellationToken cancellationToken)
    {
        var project = context.Project;
        SealedPlan.Require(plan.Plan, local.Model);
        var settings = UserConfig.ForProject(context.Environment.UserConfigFile, project.Directory);
        var output = OutputLocator.Locate(project, null, settings?.Output, context.Environment.CurrentDirectory);
        // Files go only to production's blob storage, which the run can only give the CMS's Azure blob provider.
        SealedProvidersRun.RequireMediaPossible(plan.Plan, Path.GetDirectoryName(output.Dll)!);

        // The plan's dry run against the local site, exactly as apply does it (and the sealed run will against production).
        var writes = context.Writes(local, updateExisting: options.UpdateExisting, ask: false);
        var allowedTypes = new AllowedTypesCheck(local.Model, () => CSharpSourceIndex.Build(project.SourceRoot));
        await new PlanRunner(local, writes, plan.Directory, allowOutside: false, allowedTypes, options.RequestApproval) { Files = plan, MovesFirst = options.MovesFirst }
            .RunAsync(plan.Plan, dryRun: true, options.Publish, cancellationToken);

        var (localLeftovers, onlyInProduction) = await RequireComparableAsync(context, local, productionSession, warnings, cancellationToken);
        var leftovers = SealedLeftovers.Expected(localLeftovers, onlyInProduction, productionSession.Model);
        if (leftovers.Count > 0)
        {
            warnings.Add($"{leftovers.Count} content type(s) or properties only production's database has (code removed, or newer code in production than this build) are accepted as they are, by name: {Printable.Of(string.Join(", ", leftovers.Take(8)))}{(leftovers.Count > 8 ? ", …" : "")}. The plan may not write them (checked); the run refuses any other difference.");
        }

        var state = await ProductionState.ReadAsync(productionSession, SealedPlan.Touched(plan.Plan), SealedPlan.Referenced(plan.Plan), cancellationToken);
        SealedPlan.RequireNoLeftoverWrites(plan.Plan, leftovers, state);
        RequireNoIdeHook(output.Dll);
        var build = BuildFingerprint.Of(project, output.Dll, context.Environment);
        var summary = SealSummary.Of(plan.Plan, options, state);
        var target = production.Target!;
        var seal = new Seal(
            SealStore.NewId(),
            DateTimeOffset.UtcNow,
            origin,
            project.Directory,
            new SealTarget(production.Server!, production.Database, target.Server, target.Database),
            plan.Path,
            plan.Hash,
            plan.Files.Count,
            options,
            build,
            state,
            summary,
            $"{local.Db.ConnectionString.Database} on {local.Db.ConnectionString.Server}",
            ToolInfo.Version,
            leftovers.Count > 0 ? leftovers : null);
        var file = SealStore.For(context.Environment).Save(seal);
        return new SealResult(
            seal.Id,
            file,
            seal.Expires,
            new SealedDatabase(production.Server!, production.Database, "prod"),
            plan.Path,
            plan.Hash,
            plan.Files.Count,
            options.Flags,
            summary,
            new SealedBuild(build.Output, build.Built, build.Commit, build.Uncommitted),
            ProductionState.Short(state.Model),
            seal.ValidatedOn,
            UserCommand(context, plan.Path, productionName, seal.Id),
            NotChecked,
            leftovers);
    }

    /// <exception cref="ConflictException">Production's content model and the local build's differ (<see cref="ModelReason"/>, <c>details.differences</c>).</exception>
    /// <summary>
    /// The local site's code against its database (<see cref="RequireNoDriftAsync"/>), then the local database against
    /// production (<see cref="RequireSameModelAsync"/>), leaving out of the latter what the former found only in the local
    /// database: those aren't the build's (another branch's types, removed code).
    /// </summary>
    /// <returns>The local leftovers, and what only production has.</returns>
    public static async Task<(IReadOnlyList<string> Local, IReadOnlyList<string> OnlyInProduction)> RequireComparableAsync(
        CliContext context, ContentSession local, ContentSession production, List<string> warnings, CancellationToken cancellationToken)
    {
        var localLeftovers = await RequireNoDriftAsync(context, warnings, cancellationToken);
        return (localLeftovers, await RequireSameModelAsync(local, production, cancellationToken, localLeftovers));
    }

    /// <param name="localLeftovers">Types and properties only the local database has (not the build's): no difference on the local side.</param>
    /// <returns>
    /// The content types and properties only production has (<c>Type</c>, <c>Type.Property</c>): leftovers of removed code, or
    /// newer code there. A sealed plan doesn't write them, so the seal accepts them by name (<see cref="SealedLeftovers"/>).
    /// </returns>
    public static async Task<IReadOnlyList<string>> RequireSameModelAsync(ContentSession local, ContentSession production, CancellationToken cancellationToken, IReadOnlyCollection<string>? localLeftovers = null)
    {
        var notTheBuilds = new HashSet<string>(localLeftovers ?? [], StringComparer.OrdinalIgnoreCase);
        var all = (await ModelDifferencesAsync(local, production, cancellationToken))
            .Where(d => d.Side != "local" || !(notTheBuilds.Contains(d.Type) || (d.Property is not null && notTheBuilds.Contains($"{d.Type}.{d.Property}"))))
            .ToList();
        static bool OnlyInProduction(SealModelDifference d) =>
            d.Side == "production" && d.Difference.StartsWith(ModelCompatibility.NotInBuild.Replace("the target's build", "the local build", StringComparison.Ordinal), StringComparison.Ordinal);
        var differences = all.Where(d => !OnlyInProduction(d)).ToList();
        if (differences.Count > 0)
        {
            throw new ConflictException(
                $"Production's content model and the local build's differ in {differences.Count} way(s): {string.Join("; ", differences.Take(5).Select(d => $"{d.Type}{(d.Property is null ? "" : $".{d.Property}")} ({d.Side}: {d.Difference})"))}{(differences.Count > 5 ? "; …" : "")}. A sealed apply needs them identical.",
                "details.differences lists them. Deploy this build to production first, or check out what production runs (and `opticli serve --build`), then seal again.")
            {
                Details = new { reason = ModelReason, differences },
            };
        }
        return SealedLeftovers.Sorted(all.Where(OnlyInProduction).Select(d => d.Property is null ? d.Type : $"{d.Type}.{d.Property}"));
    }

    /// <summary>
    /// The local site's code compared with its database's model (the agent's drift check, which a local site doesn't make by
    /// itself): types and properties only in the database (removed code, admin mode) are in production too, as its model is
    /// the local database's, and the sealed run's site would find them there and stop writing (it accepts no drift).
    /// </summary>
    /// <exception cref="ConflictException">They differ (<see cref="ModelReason"/>, <c>details.drift</c>).</exception>
    /// <returns>The local database's leftovers (types and properties only it has, from removed code), which the seal accepts.</returns>
    /// <exception cref="ConflictException">Any other difference between the local site's code and its database.</exception>
    public static async Task<IReadOnlyList<string>> RequireNoDriftAsync(CliContext context, List<string> warnings, CancellationToken cancellationToken)
    {
        var drift = await (await context.ConnectAgentAsync(cancellationToken)).ModelDriftAsync(cancellationToken);
        if (!drift.Checked)
        {
            warnings.Add("The local site's agent is older than this opticli, so it couldn't compare its code with the database's model; the sealed run's drift check still does. Restart it (`opticli serve --stop`, `opticli serve`) to check now.");
            return [];
        }
        var (leftovers, other) = SealedLeftovers.Split(drift);
        if (other.Count > 0)
        {
            throw new ConflictException(
                $"The build and its database differ: {string.Join("; ", other.Take(5).Select(o => $"{o.Item.Name} ({o.Item.Difference})"))}{(other.Count > 5 ? "; …" : "")}. Production's content model is the local database's, so the sealed run's site would find the same there and stop writing.",
                "details.drift lists them. Types and properties only in the database (removed code) are accepted by name; anything else isn't: build what production runs, or deploy this build there, then seal again.")
            {
                Details = new { reason = ModelReason, drift },
            };
        }
        return leftovers;
    }

    /// <summary>Both ways: what the local build has that production lacks (or has otherwise), and the other way round.</summary>
    private static async Task<IReadOnlyList<SealModelDifference>> ModelDifferencesAsync(ContentSession local, ContentSession production, CancellationToken cancellationToken)
    {
        // The run's site writes with the local build: a property production's own code no longer has is still written to.
        var toProduction = await ModelCompatibility.CompareAsync(local, production, cancellationToken, sourceBuildWrites: true);
        var fromProduction = await ModelCompatibility.CompareAsync(production, local, cancellationToken);
        return toProduction.Differences(local.Model, local.Model.Types.Select(t => t.Id)).Select(d => new SealModelDifference("local", d.Type, d.Property, d.Difference.Replace("the target's build", "production", StringComparison.Ordinal)))
            .Concat(fromProduction.Differences(production.Model, production.Model.Types.Select(t => t.Id)).Select(d => new SealModelDifference("production", d.Type, d.Property, d.Difference.Replace("the target's build", "the local build", StringComparison.Ordinal))))
            .ToList();
    }

    /// <summary>The command the user runs: <c>opticli apply &lt;plan&gt; --db &lt;name&gt; --seal &lt;id&gt;</c>, with <c>--project</c> when this run had one.</summary>
    public static string UserCommand(CliContext context, string plan, string productionName, string seal)
    {
        var parts = new List<string> { "opticli", "apply", Quote(plan), "--db", Quote(productionName), "--seal", seal };
        if (context.Parse.GetValue(context.Options.Project) is { } projectOption)
        {
            parts.AddRange(["--project", Quote(Path.GetFullPath(projectOption, context.Environment.CurrentDirectory))]);
        }
        return string.Join(' ', parts);
    }

    /// <summary>
    /// A word as a shell takes it: as it is when it is plain, else quoted (single quotes on Unix, double on Windows).
    /// A backslash is plain on Windows, where cmd and PowerShell don't escape with it, so a path stays as it is.
    /// </summary>
    internal static string Quote(string word)
    {
        var plain = OperatingSystem.IsWindows() ? "_-./:@%+=,\\" : "_-./:@%+=,";
        if (word.Length > 0 && word.All(c => char.IsAsciiLetterOrDigit(c) || plain.Contains(c)))
        {
            return word;
        }
        return OperatingSystem.IsWindows() ? $"\"{word.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : $"'{word.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    /// <summary>The seal as text, for a terminal.</summary>
    public static string Text(SealResult result)
    {
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Sealed {result.Seal} for '{result.Target.Database}' on '{result.Target.Server}' (role prod), good until {result.Expires:u}.")
            .AppendLine(CultureInfo.InvariantCulture, $"  plan {result.Plan} ({result.PlanFiles} file(s)), {Describe(result.Summary)}")
            .AppendLine(CultureInfo.InvariantCulture, $"  checked against {result.ValidatedOn}{(result.Leftovers.Count > 0 ? $"; accepts {result.Leftovers.Count} type(s)/properties only production has: {Printable.Of(string.Join(", ", result.Leftovers))}" : "")}; build {result.Build.Output}{(result.Build.Commit is { } commit ? $" (git {commit}{(result.Build.Uncommitted == true ? ", uncommitted changes" : "")})" : "")}")
            .AppendLine()
            .AppendLine("The user runs this in their own terminal (an agent hands it over and never runs it):")
            .AppendLine(CultureInfo.InvariantCulture, $"  {result.UserCommand}");
        return text.ToString();
    }

    /// <summary>A summary in one line: steps per op, types, what is new and what exists.</summary>
    public static string Describe(SealSummary summary) =>
        $"{summary.Steps} step(s): {string.Join(", ", summary.Operations.Select(o => $"{o.Value} {o.Key}"))}"
        + (summary.Types.Count > 0 ? $"; content types {string.Join(", ", summary.Types.Select(t => $"{t.Key} {t.Value}"))}" : "")
        + (summary.New + summary.Existing > 0 ? $"; creates {summary.New} new item(s), updates {summary.Existing} production has" : "")
        + $"; {summary.Publishes} publishing; {summary.Touched} item(s) production has, whose versions are checked"
        + (summary.Expiring > 0 ? $"; {summary.Expiring} set an expiry (StopPublish) that has passed: published, that content goes offline" : "")
        + (summary.Media > 0 ? $"; {summary.Media} upload a media file: the run asks for production's blob storage" : "");

    /// <summary>
    /// A build from IDE mode names opticli's copy of the agent in its <c>runtimeconfig.json</c>, which the runtime loads
    /// besides the agent the sealed run gives the site: a run with production's write login loads this opticli's agent and
    /// nothing else.
    /// </summary>
    /// <exception cref="RefusedException">The build has that hook.</exception>
    public static void RequireNoIdeHook(string outputDll)
    {
        if (IdeProject.InBuild(outputDll).AgentDll is { } hook)
        {
            throw new RefusedException(
                $"The build loads opticli's agent from `opticli ide on` ({hook}, in its runtimeconfig.json): a sealed run against production loads only this opticli's agent.",
                "Turn IDE mode off (`opticli ide off`), rebuild the site, and seal again.");
        }
    }
}
