using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Commands;

/// <summary>
/// IDE mode: the site the developer starts from the IDE (Rider's run and debug, Visual Studio, <c>dotnet run</c>) loads
/// the agent through its build, so writes go through that process and show there at once, with nothing to set per run.
/// </summary>
internal static class IdeCommand
{
    /// <param name="Git">
    /// <c>ignored</c> (git already ignores the file), <c>excluded</c> (opticli added it to <c>.git/info/exclude</c>),
    /// <c>notIgnored</c> (it couldn't), <c>noRepository</c>.
    /// </param>
    private sealed record OnResult(
        string UserFile,
        bool Created,
        string Agent,
        string? AgentVersion,
        string Configuration,
        string IdeFile,
        IdeDatabase Database,
        string Git,
        string? ExcludeFile,
        IReadOnlyList<string>? PrunedCopies,
        bool RebuildNeeded,
        string Next);

    /// <param name="Remote">The approved remote development database; the agent accepts it besides local ones.</param>
    private sealed record IdeDatabase(string Server, string? Name, bool Remote);

    private sealed record OffResult(string UserFile, bool Removed, bool FileDeleted, string? ExcludeFile, bool RebuildNeeded);

    /// <param name="AgentCurrent">The copy is this opticli's agent; false: `ide on` again (and rebuild) to update it.</param>
    /// <param name="Build">The build output looked at, and whether its runtimeconfig names this block's agent (else: rebuild).</param>
    /// <param name="Site">The site the CLI would write through: the IDE's when it runs, else what `serve --status` reports.</param>
    private sealed record StatusResult(
        bool On,
        string UserFile,
        string? Agent,
        bool? AgentExists,
        string? AgentVersion,
        bool? AgentCurrent,
        string? Configuration,
        string? RemoteDatabase,
        BuildView? Build,
        string Git,
        AgentStatus? Site);

    private sealed record BuildView(string Output, bool HasHook);

    public static Command Create(GlobalOptions options)
    {
        var ide = new Command("ide", """
            IDE mode: the site you start from your IDE (Rider's run or debug, Visual Studio, dotnet run, dotnet watch) gets the
            opticli agent from its build, so writes go through that process and show there at once (no second site with
            its own cache, no restart). Nothing to set per run, nothing secret in the project or a launch profile.
            `ide on` adds a block to <Project>.csproj.user (MSBuild imports it; git normally ignores it, and opticli adds it to
            .git/info/exclude when not) that names opticli's copy of the agent as the Debug build's startup hook. Rebuild,
            then start the site from the IDE as usual: the agent makes its own token and tells opticli where it listens
            (HTTPS with the development certificate works). It never changes how the site starts or stops it from starting:
            against another database, or outside Development, it just stays off for that run.
            Example: opticli ide on    then: opticli ide status | opticli ide off
            """);
        ide.Subcommands.Add(On(options));
        ide.Subcommands.Add(Off(options));
        ide.Subcommands.Add(Status(options));
        return ide;
    }

    private static Command On(GlobalOptions options)
    {
        var configuration = new Option<string>("--configuration")
        {
            Description = "The build configuration that gets the agent.",
            DefaultValueFactory = _ => IdeProject.DefaultConfiguration,
            HelpName = "name",
        };
        var command = new Command("on", """
            Turn IDE mode on for this project (again after `opticli` is updated or `db use` changes, to refresh it). Writes the
            block to <Project>.csproj.user, copies the agent to opticli's state directory (it must stay put while builds name
            it) and keeps the file out of git. Refused when git tracks the .csproj.user. Rebuild and restart the site from the
            IDE afterwards. The agent accepts a local database, or the development database chosen with `db use`.
            Example: opticli ide on
            """);
        command.Options.Add(configuration);
        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var project = context.Project;
            var name = context.Parse.GetValue(configuration)!.Trim();
            if (name.Length == 0)
            {
                throw new UsageException("--configuration needs a name, e.g. Debug.");
            }
            var connection = ServeCommand.RequireServable(context);
            var userFile = IdeProject.UserFile(project);
            var tracking = IdeProject.Tracking(userFile);
            if (tracking == GitTracking.Tracked)
            {
                throw new RefusedException(
                    $"{Path.GetFileName(userFile)} is committed to git: opticli won't put this machine's paths in it.",
                    $"Untrack it (git rm --cached \"{Path.GetFileName(userFile)}\" and add *.user to .gitignore), or start the site with the variables from `opticli env --format launchSettings`.");
            }

            var (choice, toolAgent) = await ServeCommand.SelectAgentAsync(context, connection, cancellationToken);
            var stateDirectory = context.Environment.StateDirectory;
            var copy = IdeProject.CopyAgent(stateDirectory, toolAgent, choice.CmsMajor);
            var store = context.StateStore;
            var connectionName = context.ConnectionRequest.Name;
            var block = new IdeBlock(
                copy,
                store.IdePath,
                name,
                connection.IsLocal ? null : AgentProtocol.FormatRemote(connection.Server, connection.Database),
                string.Equals(connectionName, AgentProtocol.DefaultConnectionName, StringComparison.Ordinal) ? null : connectionName);
            var created = IdeProject.Write(userFile, block);
            // A token `opticli env` registered is for a run with its variables, which IDE mode replaces.
            try
            {
                if (store.Read() is { Mode: ServeMode.External })
                {
                    store.Delete();
                }
            }
            catch (CorruptStateException)
            {
            }
            var excludeFile = tracking == GitTracking.NotIgnored ? IdeProject.Exclude(userFile) : null;
            var pruned = IdeProject.PruneCopies(stateDirectory, copy, DateTime.UtcNow);

            var warnings = new List<string>();
            if (tracking == GitTracking.NotIgnored && excludeFile is null)
            {
                warnings.Add($"git doesn't ignore {Path.GetFileName(userFile)}, and opticli couldn't add it to .git/info/exclude: keep it out of commits (it names paths on this machine).");
            }
            if (!connection.IsLocal)
            {
                warnings.Add($"The development database '{connection.Database}' on '{connection.Server}' is remote. In IDE mode the site starts as it always does (scheduler, schema updates and content type sync as the site sets them, unlike `opticli serve`); opticli's writes still stop on drift between your code and the database.");
            }
            var rebuild = !HasHook(context, project, copy, out _);
            if (RunningServe(context) is { } served)
            {
                warnings.Add($"`opticli serve` runs this site (pid {served}). Once you start it from the IDE, writes go there; stop the other with `opticli serve --stop`.");
            }

            var next = rebuild
                ? $"Rebuild the site ({name}): in Rider or Visual Studio use Rebuild, since Run's up-to-date check doesn't notice a changed .csproj.user. Then start it from the IDE; `opticli serve --status` shows it (mode ide)."
                : "The build already has the hook: restart the site from the IDE if it runs.";
            return new CommandResult(
                new OnResult(userFile, created, copy, IdeProject.AgentVersion(copy), name, store.IdePath,
                    new IdeDatabase(connection.Server, connection.Database, !connection.IsLocal),
                    tracking switch { GitTracking.Ignored => "ignored", GitTracking.NotIgnored => excludeFile is null ? "notIgnored" : "excluded", _ => "noRepository" },
                    excludeFile, pruned.Count > 0 ? pruned : null, rebuild, next),
                Warnings: warnings.Count > 0 ? warnings : null,
                Source: CommandResult.CliSource);
        });
        return command;
    }

    private static Command Off(GlobalOptions options)
    {
        var command = new Command("off", """
            Turn IDE mode off for this project: removes opticli's block from <Project>.csproj.user (the file too when nothing
            else is in it) and the line opticli added to .git/info/exclude. The build loads the agent until it is rebuilt.
            Example: opticli ide off
            """);
        CommandRunner.SetHandler(command, options, (context, cancellationToken) =>
        {
            var project = context.Project;
            var userFile = IdeProject.UserFile(project);
            var block = IdeProject.Read(userFile);
            var (removed, deleted) = IdeProject.Remove(userFile);
            var excludeFile = IdeProject.Unexclude(userFile);
            var rebuild = block is not null && HasHook(context, project, block.AgentDll, out _);
            var warnings = rebuild ? new[] { "The current build still loads the agent: rebuild the site (and restart it) to drop it." } : null;
            return Task.FromResult(new CommandResult(
                new OffResult(userFile, removed, deleted, excludeFile, rebuild),
                Warnings: warnings,
                Source: CommandResult.CliSource));
        });
        return command;
    }

    private static Command Status(GlobalOptions options)
    {
        var command = new Command("status", """
            Whether IDE mode is on for this project: the block in <Project>.csproj.user, opticli's copy of the agent (and
            whether it is this opticli's), whether the build output has the hook (else rebuild), whether git ignores the file,
            and the site opticli writes through (the IDE's when it runs). Example: opticli ide status
            """);
        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var project = context.Project;
            var userFile = IdeProject.UserFile(project);
            var block = IdeProject.Read(userFile);
            var warnings = Warnings(context, project, block).ToList();
            bool? current = block is null ? null : IsCurrent(block.AgentDll);
            BuildView? build = null;
            if (block is not null)
            {
                var hasHook = HasHook(context, project, block.AgentDll, out var output);
                build = output is null ? null : new BuildView(output, hasHook);
            }
            var tracking = IdeProject.Tracking(userFile);
            AgentStatus? site = null;
            try
            {
                var resolution = context.ResolveConnection();
                site = await AgentProbe.ProbeAsync(context.StateStore, resolution.Chosen is null ? null : resolution.Require(), cancellationToken);
            }
            catch (OptiCliException ex)
            {
                warnings.Add($"The site's agent wasn't checked: {ex.Message}");
            }
            return new CommandResult(
                new StatusResult(block is not null, userFile, block?.AgentDll, block is null ? null : File.Exists(block.AgentDll), block is null ? null : IdeProject.AgentVersion(block.AgentDll),
                    current, block?.Configuration, block?.RemoteDatabase, build,
                    tracking switch { GitTracking.Ignored => "ignored", GitTracking.NotIgnored => "notIgnored", GitTracking.Tracked => "tracked", _ => "noRepository" },
                    site),
                Warnings: warnings.Count > 0 ? warnings : null,
                Source: Core.Writes.WriteExecutor.AgentSource);
        });
        return command;
    }

    /// <summary>What needs doing for IDE mode to work (for <c>ide status</c> and <c>doctor</c>); empty when it is off or fine.</summary>
    internal static IEnumerable<string> Warnings(CliContext context, Core.Discovery.ProjectInfo project, IdeBlock? block)
    {
        if (block is null)
        {
            yield break;
        }
        if (!File.Exists(block.AgentDll))
        {
            yield return "IDE mode: opticli's copy of the agent that the .csproj.user block names is gone, so builds leave the hook out: run `opticli ide on` again and rebuild.";
        }
        else if (IsCurrent(block.AgentDll) == false)
        {
            yield return "IDE mode: the .csproj.user block names an agent from another opticli version: run `opticli ide on` again, rebuild and restart the site to update it.";
        }
        if (!HasHook(context, project, block.AgentDll, out var output) && output is not null)
        {
            yield return $"IDE mode: the build output ({Path.GetRelativePath(project.Directory, output)}) doesn't load the agent from `opticli ide on`: rebuild the site ({block.Configuration}; in the IDE use Rebuild, since Run may skip the build).";
        }
        if (RemoteChanged(context, block) is { } changed)
        {
            yield return changed;
        }
        if (IdeProject.Tracking(IdeProject.UserFile(project)) == GitTracking.NotIgnored)
        {
            yield return $"IDE mode: git doesn't ignore {Path.GetFileName(IdeProject.UserFile(project))}: keep it out of commits (`opticli ide on` adds it to .git/info/exclude).";
        }
    }

    /// <summary>Whether the site's build output names <paramref name="agentDll"/> as its startup hook.</summary>
    /// <param name="output">The build output looked at; null when there is none.</param>
    internal static bool HasHook(CliContext context, Core.Discovery.ProjectInfo project, string agentDll, out string? output)
    {
        output = null;
        try
        {
            var settings = UserConfig.ForProject(context.Environment.UserConfigFile, project.Directory);
            output = OutputLocator.Locate(project, null, settings?.Output, context.Environment.CurrentDirectory).Dll;
        }
        catch (NotFoundException)
        {
            return false;
        }
        return IdeProject.InBuild(output).AgentDll is { } hook && SamePath(hook, agentDll);
    }

    /// <summary>The copy is the agent this opticli ships (same bytes): false after an update until `ide on` runs again.</summary>
    private static bool? IsCurrent(string copy)
    {
        if (!File.Exists(copy) || Path.GetFileName(Path.GetDirectoryName(copy)) is not { } folder || !folder.StartsWith("cms", StringComparison.Ordinal)
            || !int.TryParse(folder[3..], out var major))
        {
            return null;
        }
        try
        {
            var tool = AgentLocator.Locate(AppContext.BaseDirectory, major);
            return File.ReadAllBytes(tool).AsSpan().SequenceEqual(File.ReadAllBytes(copy));
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    /// <summary>The block approves another remote database than the development database now is.</summary>
    private static string? RemoteChanged(CliContext context, IdeBlock block)
    {
        try
        {
            var connection = context.UseConnection();
            var now = connection.IsLocal ? null : AgentProtocol.FormatRemote(connection.Server, connection.Database);
            return string.Equals(now, block.RemoteDatabase, StringComparison.OrdinalIgnoreCase)
                ? null
                : "IDE mode: the development database changed since `opticli ide on`: run it again (and rebuild) so the agent accepts the new one.";
        }
        catch (OptiCliException)
        {
            return null;
        }
    }

    /// <summary>The pid of a site `serve` started for this project that still runs; null when none.</summary>
    private static int? RunningServe(CliContext context)
    {
        try
        {
            if (context.StateStore.Read() is { Pid: { } pid, ProcessStartTime: var started })
            {
                using var process = SiteProcess.Find(pid, started);
                return process is null ? null : pid;
            }
        }
        catch (CorruptStateException)
        {
        }
        return null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
