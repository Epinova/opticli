using System.CommandLine;
using System.Runtime.InteropServices;
using OptiCli.Cli;
using OptiCli.Core.Cms;
using OptiCli.Core.Configuration;
using OptiCli.Core.Data;
using OptiCli.Core.Discovery;
using OptiCli.Core.Drift;
using OptiCli.Core.Errors;
using OptiCli.Core.Safety;
using OptiCli.Core.Serve;
using OptiCli.Core.Sites;
using OptiCli.Core.Skills;
using OptiCli.Protocol;

namespace OptiCli.Commands;

/// <summary>
/// Explains what opticli would use and why: project, every connection string candidate (never the
/// string itself), the database, the agent and installed copies of the skill. Always exits 0; <c>healthy</c> says whether reads work.
/// </summary>
internal static class DoctorCommand
{
    private sealed record Report(
        bool Healthy,
        ToolSection Tool,
        ProjectSection Project,
        ConnectionSection Connection,
        DatabaseSection Database,
        AgentStatus? Agent,
        SchedulerSection? Scheduler,
        DriftReport? Drift,
        IReadOnlyList<SavedPrimary>? SitesMapping,
        IReadOnlyList<InstalledSkill> Skills,
        IReadOnlyList<string> Warnings);

    private sealed record ToolSection(string Version, string Runtime);

    /// <param name="Serve">What <c>opticli serve</c> does with the site's scheduler for this project: <c>off</c> unless --scheduler or <c>"scheduler": true</c> in the user config.</param>
    /// <param name="Running">The running site's scheduler, as it reports it; null when no site answers.</param>
    /// <param name="OverdueJobs">Enabled jobs whose next run has passed, which a site with its scheduler on starts at once.</param>
    private sealed record SchedulerSection(string Serve, string? Running, int? OverdueJobs);

    /// <param name="Imports">The files besides the project file read for its properties (Directory.Build.props and imports).</param>
    /// <param name="CmsMajor">The CMS major the project builds against (12 or 13), from <paramref name="CmsVersion"/>.</param>
    private sealed record ProjectSection(
        bool Found,
        string? Path = null,
        string? Solution = null,
        string? SourceRoot = null,
        string? HowFound = null,
        string? TargetFramework = null,
        string? CmsVersion = null,
        int? CmsMajor = null,
        string? UserSecretsId = null,
        IReadOnlyList<string>? Imports = null,
        Problem? Error = null);

    /// <param name="Development">The project's development database and how it was found (<c>saved</c>, <c>configured</c>, <c>automatic</c>).</param>
    private sealed record ConnectionSection(
        string Name,
        CandidateView? Chosen,
        SelectionMode? Mode,
        CandidateView? Development,
        SelectionMode? DevelopmentMode,
        SavedDatabase? Saved,
        IReadOnlyList<CandidateView> Candidates,
        Problem? Error);

    private sealed record CandidateView(
        string? Id,
        ConnectionSource Source,
        string Location,
        string? Key,
        string? Profile,
        string? Server,
        string? Database,
        bool? Local,
        CandidateStatus Status,
        string? Reason);

    /// <param name="CmsMajor">The CMS major the database's schema belongs to (12 or 13), from <paramref name="SchemaVersion"/>.</param>
    private sealed record DatabaseSection(bool Reachable, string? Server = null, string? Database = null, int? SchemaVersion = null, int? CmsMajor = null, string? SqlServerVersion = null, int? ContentTypes = null, int? ContentItems = null, Problem? Error = null);

    private sealed record Problem(string Code, string Message, string? Hint);

    public static Command Create(GlobalOptions options)
    {
        var command = new Command("doctor", """
            Check the setup: project, connection string, database, write agent and installed skill, and where each came from.
            Lists every connection string candidate with its id, source, server, database and whether it is local (passwords
            are never shown), which one is the development database and why, the CMS schema version, the agent's state
            (running, stopped, stale, unresponsive), the scheduler (what `serve` does with it, the running site's, and how
            many jobs are overdue), against a shared database drift (what differs from this build), and the sites whose
            primary host differs from the mapping saved with `sites primary --save`. Always exits 0:
            data.healthy says whether reads work, data.warnings lists problems. Run it first when another command fails.
            Example: opticli doctor
            """);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var warnings = new List<string>();

            var project = context.TryGetProject(out var projectError);
            var cmsVersion = project is null ? null : PackageVersions.FindCms(project.Project);
            var projectSection = project is null
                ? new ProjectSection(false, Error: ToProblem(projectError!))
                : new ProjectSection(
                    true,
                    project.ProjectFile,
                    project.SolutionFile,
                    project.SourceRoot,
                    project.HowFound,
                    project.Project.TargetFramework,
                    cmsVersion,
                    PackageVersions.Major(cmsVersion),
                    project.Project.UserSecretsId,
                    project.Project.Imports.Count > 0 ? project.Project.Imports : null);
            warnings.AddRange(project?.Project.Warnings ?? []);
            if (project is not null && projectSection.CmsVersion is null)
            {
                warnings.Add(File.Exists(PackageVersions.AssetsFile(project.Project))
                    ? $"The CMS version is unknown: {CsprojFile.CmsPackage} is neither referenced with a version nor in obj/project.assets.json."
                    : $"The CMS version is unknown until the project is restored: run dotnet restore {project.ProjectFile}");
            }

            var resolution = context.ResolveConnection();
            var candidates = resolution.Candidates.Select(c => ToView(c, project)).ToList();
            var connectionSection = new ConnectionSection(
                resolution.Name,
                resolution.Chosen is null ? null : ToView(resolution.Chosen, project),
                resolution.Mode,
                resolution.Development is null ? null : ToView(resolution.Development, project),
                resolution.DevelopmentMode,
                resolution.Saved,
                candidates,
                resolution.Failure is null ? null : ToProblem(resolution.Failure));

            warnings.AddRange(resolution.Warnings());
            if (resolution.Failure is NeedsSelectionException needsSelection)
            {
                warnings.Add($"{needsSelection.Message} {needsSelection.Hint}");
            }

            var databaseSection = await ProbeDatabaseAsync(context, cancellationToken);
            if (projectSection.CmsMajor is { } builds && databaseSection.CmsMajor is { } stored && builds != stored)
            {
                warnings.Add($"The project builds against CMS {builds} ({projectSection.CmsVersion}), but the database has a CMS {stored} schema (version {databaseSection.SchemaVersion}): "
                    + (builds > stored
                        ? $"the site upgrades it to CMS {builds} when it starts, after which CMS {stored} can't use it. Check that the connection string points at the right database before starting the site."
                        : $"CMS {builds} won't start against it. Check that the connection string points at the right database, or update the project's EPiServer packages."));
            }
            var agent = project is null
                ? null
                : await AgentProbe.ProbeAsync(context.StateStore, resolution.Chosen is null ? null : TryVerify(resolution), cancellationToken);
            if (agent is { State: not (AgentState.Running or AgentState.Stopped) })
            {
                warnings.Add($"Agent: {agent.Message}");
            }

            var scheduler = project is null || !databaseSection.Reachable
                ? null
                : await SchedulerAsync(context, project, resolution.Chosen, agent, warnings, cancellationToken);

            var drift = project is null || resolution.Chosen is null || !databaseSection.Reachable
                ? null
                : await DriftAsync(context, project, resolution.Chosen, warnings, cancellationToken);

            var sitesMapping = project is null || !databaseSection.Reachable
                ? null
                : await SitesMappingAsync(context, project, warnings, cancellationToken);

            var skills = InstalledSkills(context);
            foreach (var skill in skills.Where(s => s.Outdated))
            {
                warnings.Add($"The opticli skill in {skill.Path} was written for opticli {skill.Version ?? "(no version)"}, this is {ToolInfo.Version}. "
                    + $"Update it: opticli skill install{(skill.Scope == SkillScope.Repository ? " --repo" : "")} (it asks for --force only if the files were edited).");
            }

            var report = new Report(
                project is not null && databaseSection.Reachable,
                new ToolSection(ToolInfo.Version, RuntimeInformation.FrameworkDescription),
                projectSection,
                connectionSection,
                databaseSection,
                agent,
                scheduler,
                drift,
                sitesMapping,
                skills,
                warnings);
            return new CommandResult(report);
        });
        return command;
    }

    /// <summary>
    /// Against a shared database: what <c>serve</c> found differs between the build and the database. Before <c>serve</c>
    /// has run, the part it compares before starting the site (EF Core migrations, the CMS schema version).
    /// </summary>
    private static async Task<DriftReport?> DriftAsync(CliContext context, ProjectInfo project, ConnectionCandidate chosen, List<string> warnings, CancellationToken cancellationToken)
    {
        if (chosen.IsLocal != false)
        {
            return new DriftReport { Checked = false, Notes = [DriftCommand.LocalNote] };
        }
        try
        {
            // Only a report about this same database: the state may be from a run against another one.
            if (context.StateStore.Read() is { Drift: { } reported } state
                && string.Equals(state.DbServer?.Trim(), chosen.Server?.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(state.DbName?.Trim(), chosen.Database?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return reported;
            }
        }
        catch (CorruptStateException)
        {
            // Reported with the agent's state.
        }
        try
        {
            var settings = UserConfig.ForProject(context.Environment.UserConfigFile, project.Directory);
            var output = OutputLocator.Locate(project, null, settings?.Output, context.Environment.CurrentDirectory);
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            var startup = await StartupDriftCheck.RunAsync(db, Path.GetDirectoryName(output.Dll)!, cancellationToken);
            if (startup.SchemaRefusal is { } refusal)
            {
                warnings.Add($"{refusal} {startup.SchemaHint}");
            }
            if (startup.PendingMigrations.Count > 0)
            {
                warnings.Add($"This build has {startup.PendingMigrations.Count} EF Core migration(s) the shared database doesn't; `opticli serve` refuses to start it without --allow-pending-migrations.");
            }
            return new DriftReport
            {
                Checked = true,
                Partial = true,
                Migrations = startup.Drift.Migrations,
                Schema = startup.Drift.Schema,
                Notes = [.. startup.Drift.Notes, "Content types, properties and Dynamic Data Store types are compared once `opticli serve` runs; the fingerprint comes with that."],
            };
        }
        catch (OptiCliException ex)
        {
            warnings.Add($"Drift: not compared ({ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// Whether the site's scheduler runs: what <c>serve</c> does, what the running site reports, and how many jobs are
    /// overdue. With the scheduler on, those start at once: on a restored production database, imports, emails, and the
    /// emptying of the recycle bin.
    /// </summary>
    private static async Task<SchedulerSection?> SchedulerAsync(CliContext context, ProjectInfo project, ConnectionCandidate? chosen, AgentStatus? agent, List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            var configured = UserConfig.ForProject(context.Environment.UserConfigFile, project.Directory)?.Scheduler == true && chosen?.IsLocal != false;
            var running = agent?.State == AgentState.Running ? agent.Scheduler : null;
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            var overdue = await ServeCommand.OverdueJobsAsync(db, cancellationToken);
            if (agent is { State: AgentState.Running, Scheduler: null } && chosen?.IsLocal != false)
            {
                warnings.Add(ServeCommand.OldAgentSchedulerWarning(agent, overdue.Count));
            }
            else if (overdue.Count > 0 && running == "on")
            {
                warnings.Add(ServeCommand.OverdueWarning(overdue, "start in the running site, whose scheduler is on"));
            }
            else if (overdue.Count > 0 && running is null && configured)
            {
                warnings.Add(ServeCommand.OverdueWarning(overdue, "run when `opticli serve` starts the site (\"scheduler\": true in the user config)"));
            }
            return new SchedulerSection(configured ? "on" : "off", running, overdue.Count);
        }
        catch (OptiCliException ex)
        {
            warnings.Add($"Scheduler: jobs not read ({ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// The saved <c>sites primary</c> mapping against the sites' primary hosts now, read from the database (no agent): after
    /// a database restore it says to run <c>sites primary --from-config</c>.
    /// </summary>
    private static async Task<IReadOnlyList<SavedPrimary>?> SitesMappingAsync(CliContext context, ProjectInfo project, List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            if (UserConfig.ForProject(context.Environment.UserConfigFile, project.Directory)?.PrimaryHosts is not { } mapping)
            {
                return null;
            }
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            var sites = await SiteReader.ListAsync(db, cancellationToken);
            var languages = (await LanguageReader.ListAsync(db, cancellationToken)).Where(l => l.Enabled).Select(l => l.Code).ToList();
            var entries = PrimaryMapping.Compare(mapping, sites, languages);
            warnings.AddRange(PrimaryMapping.Warnings(entries));
            return entries;
        }
        catch (OptiCliException ex)
        {
            warnings.Add($"Sites mapping: not compared ({ex.Message})");
            return null;
        }
    }

    /// <summary>The user's and the repository's copy of the skill, where installed.</summary>
    private static List<InstalledSkill> InstalledSkills(CliContext context)
    {
        var found = new List<InstalledSkill?> { SkillInstaller.Inspect(SkillScope.User, SkillLocations.User(context.Environment), ToolInfo.Version) };
        if (SkillCommand.RepositoryRoot(context) is { } root)
        {
            found.Add(SkillInstaller.Inspect(SkillScope.Repository, SkillLocations.Repository(root), ToolInfo.Version));
        }
        return found.OfType<InstalledSkill>().DistinctBy(s => s.Path).ToList();
    }

    private static async Task<DatabaseSection> ProbeDatabaseAsync(CliContext context, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            var info = await DatabaseInfoReader.ReadAsync(db, cancellationToken);
            return new DatabaseSection(true, db.Server, info.Database, info.SchemaVersion, info.SchemaVersion is { } version ? CmsSchema.MajorOf(version) : null,
                info.SqlServerVersion, info.ContentTypes, info.ContentItems);
        }
        catch (OptiCliException ex)
        {
            return new DatabaseSection(false, Error: ToProblem(ex));
        }
    }

    /// <summary>Files inside the project are shown relative to it; everything else as found.</summary>
    private static CandidateView ToView(ConnectionCandidate c, ProjectInfo? project)
    {
        var location = project is not null && c.Location.StartsWith(project.Directory + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? System.IO.Path.GetRelativePath(project.Directory, c.Location)
            : c.Location;
        return new(c.Id, c.Source, location, c.Key, c.Profile, c.Server, c.Database, c.IsLocal, c.Status, c.Reason);
    }

    private static VerifiedConnectionString? TryVerify(ConnectionResolution resolution)
    {
        try
        {
            return resolution.Require();
        }
        catch (OptiCliException)
        {
            return null;
        }
    }

    private static Problem ToProblem(OptiCliException ex) => new(ExitCodes.Name(ex.Code), ex.Message, ex.Hint);
}
