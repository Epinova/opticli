using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

/// <summary>
/// Starts, inspects and stops the local site with the opticli agent injected; write commands go through it.
/// </summary>
internal static class ServeCommand
{
    private const int DefaultTimeoutSeconds = 180;
    private const int DefaultLogLines = 200;

    public static Command Create(GlobalOptions options)
    {
        var build = new Option<bool>("--build") { Description = "Run `dotnet build` on the site project first." };
        var port = new Option<int?>("--port") { Description = $"Port on 127.0.0.1. Default: the user config's port, else {PortSelector.DefaultPort}, else the first free one up to {PortSelector.RangeEnd}.", HelpName = "n" };
        var foreground = new Option<bool>("--foreground") { Description = "Run attached, streaming the site's output, until Ctrl+C." };
        var output = new Option<string?>("--output") { Description = "The site DLL to run. Default: bin/{Debug,Release}/<tfm>/<AssemblyName>.dll (newest), or the user config's output.", HelpName = "dll" };
        var timeout = new Option<int>("--timeout") { Description = "Seconds to wait for the site to answer.", DefaultValueFactory = _ => DefaultTimeoutSeconds, HelpName = "seconds" };
        var status = new Option<bool>("--status") { Description = "Show whether the site runs, its port, pid, database and agent version." };
        var logs = new Option<bool>("--logs") { Description = "Show the site's log (its console output)." };
        var tail = new Option<int?>("--tail") { Description = $"With --logs: only the last N lines (default {DefaultLogLines}).", HelpName = "n" };
        var stop = new Option<bool>("--stop") { Description = "Stop the site (SIGTERM, then kill after 20 s) and forget its token." };

        var command = new Command("serve", """
            Start, inspect or stop the local site with the opticli agent injected; write commands need it running.
            Runs the site's build output (its code is not changed) in Development on http://127.0.0.1:<port>, pinned to the
            database opticli reads; the agent refuses to start against anything else (exit 3). Starts in the background and
            returns once the agent answers (30-60 s is normal); state (pid, port, a fresh token) is kept in a user-only file.
            Warns when source files are newer than the build output (--build rebuilds first). Stop it when done.
            Example: opticli serve    then: opticli serve --status | opticli serve --logs --tail 40 | opticli serve --stop
            """);
        foreach (var option in new Option[] { build, port, foreground, output, timeout, status, logs, tail, stop })
        {
            command.Options.Add(option);
        }

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var modes = new[] { parse.GetValue(status), parse.GetValue(logs), parse.GetValue(stop) }.Count(m => m);
            var starting = parse.GetValue(build) || parse.GetValue(port) is not null || parse.GetValue(foreground) || parse.GetValue(output) is not null;
            if (modes > 1 || (modes == 1 && starting))
            {
                throw new UsageException("--status, --logs and --stop are separate actions; don't combine them with each other or with start options.");
            }
            if (parse.GetValue(tail) is not null && !parse.GetValue(logs))
            {
                throw new UsageException("--tail only applies to --logs.");
            }

            if (parse.GetValue(status))
            {
                return new CommandResult(await StatusAsync(context, cancellationToken), Source: WriteExecutor.AgentSource);
            }
            if (parse.GetValue(logs))
            {
                return Logs(context, parse.GetValue(tail) ?? DefaultLogLines);
            }
            if (parse.GetValue(stop))
            {
                var stopped = await SiteLauncher.StopAsync(context.StateStore);
                return new CommandResult(stopped ?? (object)new { stopped = false, message = "Nothing was running for this project." }, Source: WriteExecutor.AgentSource);
            }

            var seconds = parse.GetValue(timeout);
            if (seconds <= 0)
            {
                throw new UsageException("--timeout must be a positive number of seconds.");
            }
            return await StartAsync(context, parse.GetValue(build), parse.GetValue(port), parse.GetValue(foreground), parse.GetValue(output), TimeSpan.FromSeconds(seconds), cancellationToken);
        });
        return command;
    }

    private static async Task<CommandResult> StartAsync(CliContext context, bool build, int? port, bool foreground, string? output, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var project = context.Project;
        // The database check comes before anything is started or even looked for.
        var connection = RequireServable(context);
        var store = context.StateStore;

        var existing = await AgentProbe.ProbeAsync(store, connection, cancellationToken);
        switch (existing.State)
        {
            case AgentState.Running when existing.Mode != ServeMode.External:
                return new CommandResult(existing, Warnings: ["The site was already running; nothing was started."], Source: WriteExecutor.AgentSource);
            case AgentState.Stopped:
                break;
            case AgentState.Stale:
            case AgentState.Unresponsive when existing.Mode == ServeMode.External:
                store.Delete();
                break;
            case AgentState.Running:
                throw new UsageException(
                    $"A site started with the variables from `opticli env` is running on port {existing.Port}.",
                    "Use it as it is, or stop it (and `opticli serve --stop` to forget its token) before `opticli serve`.");
            default:
                throw new UsageException($"A site is already registered for this project: {existing.Message}", "Check `opticli serve --status` and `--logs`; `opticli serve --stop` stops it.");
        }

        var settings = UserConfig.ForProject(context.Environment.UserConfigFile, project.Directory);
        if (build)
        {
            await SiteBuild.RunAsync(project, Console.Error, cancellationToken);
        }
        var siteOutput = OutputLocator.Locate(project, output, settings?.Output, context.Environment.CurrentDirectory);
        var warnings = new List<string>();
        if (!connection.IsLocal)
        {
            warnings.Add(SharedDatabaseWarning(connection));
        }
        if (OutputLocator.NewerSource(project.Directory, siteOutput.Dll) is { } newer)
        {
            warnings.Add($"{Path.GetRelativePath(project.Directory, newer)} is newer than the build output {Path.GetRelativePath(project.Directory, siteOutput.Dll)}; the site may be out of date (use --build).");
        }
        var request = new LaunchRequest(
            project,
            connection,
            context.ConnectionRequest.Name,
            siteOutput,
            AgentLocator.Locate(AppContext.BaseDirectory),
            PortSelector.Select(port, settings?.Port, PortSelector.IsFree),
            timeout);

        if (!foreground)
        {
            var started = await SiteLauncher.StartBackgroundAsync(store, request, cancellationToken);
            return new CommandResult(started, Warnings: warnings.Count > 0 ? warnings : null, Source: WriteExecutor.AgentSource);
        }

        foreach (var warning in warnings)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }
        await SiteLauncher.RunForegroundAsync(store, request, Console.Out,
            ready => Console.Error.WriteLine($"[opticli] agent ready at {ready.Url} (pid {ready.Pid}, database '{ready.Database?.Name}'); Ctrl+C stops the site."),
            cancellationToken);
        return new CommandResult(new { stopped = true });
    }

    /// <summary>
    /// The database the site may run against: a local one, or the remote development database the user chose. A local
    /// build writing to a shared database also syncs its content types into it, so another environment is never served.
    /// </summary>
    /// <exception cref="RefusedException">A remote database that isn't the development database.</exception>
    public static Core.Safety.VerifiedConnectionString RequireServable(CliContext context)
    {
        var connection = context.UseConnection();
        var resolution = context.ResolveConnection();
        if (!resolution.IsDevelopment)
        {
            throw new RefusedException(
                $"Not running the site against '{connection.Database}' on remote server '{connection.Server}': it is not this project's development database.",
                "The site only runs against a local database or the development database the user chose (`opticli db list`). Reads with --db work; writes to other environments are not supported.");
        }
        return connection;
    }

    public static string SharedDatabaseWarning(Core.Safety.VerifiedConnectionString connection) =>
        $"The site runs against the remote development database '{connection.Database}' on '{connection.Server}', which others may use too. "
        + "For this run opticli turned off its scheduler, automatic database schema updates and content type sync, so content types "
        + "or properties that exist only in your local code are not added to the database (writes to them fail).";

    private static async Task<AgentStatus> StatusAsync(CliContext context, CancellationToken cancellationToken)
    {
        var resolution = context.ResolveConnection();
        var expected = resolution.Chosen is null ? null : resolution.Require();
        return await AgentProbe.ProbeAsync(context.StateStore, expected, cancellationToken);
    }

    private static CommandResult Logs(CliContext context, int lines)
    {
        if (lines <= 0)
        {
            throw new UsageException("--tail must be a positive number of lines.");
        }
        var store = context.StateStore;
        if (!File.Exists(store.LogPath))
        {
            throw new NotFoundException("There is no site log for this project yet.", "Start the site with `opticli serve`.");
        }
        var tail = LogTail.Read(store.LogPath, lines);
        return new CommandResult(new { log = store.LogPath, lines = tail }, Text: string.Concat(tail.Select(l => l + Environment.NewLine)));
    }
}
