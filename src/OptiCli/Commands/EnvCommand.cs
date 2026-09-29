using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Commands;

/// <summary>
/// The environment <c>serve</c> would give the site, for running it from an IDE instead. Registers a
/// fresh token in the state file so write commands work against that site.
/// </summary>
internal static class EnvCommand
{
    public static Command Create(GlobalOptions options)
    {
        var format = new Option<string>("--format")
        {
            Description = $"{EnvFormat.Shell} (export lines, for eval), {EnvFormat.PowerShell}, {EnvFormat.Dotenv}, {EnvFormat.Json} (envelope), or {EnvFormat.LaunchSettings} (a Properties/launchSettings.json profile).",
            DefaultValueFactory = _ => EnvFormat.Shell,
            HelpName = "format",
        };
        format.AcceptOnlyFromAmong([.. EnvFormat.All]);
        var port = new Option<int?>("--port") { Description = $"Port the site will listen on. Default: the user config's port, else {PortSelector.DefaultPort} (or the next free one).", HelpName = "n" };
        var includeConnection = new Option<bool>("--include-connection")
        {
            Description = $"Also pin the connection string ({AgentProtocol.DatabaseVariable}, with its password). Default: the site uses its own configured connection, which the agent still checks is local (or the approved development database) and opticli checks is the one it reads.",
        };

        var command = new Command("env", """
            Print the environment variables that inject the agent, to start the site yourself (IDE, dotnet run) instead of serve.
            Each run makes a new token and saves it in opticli's user-only state file, so write commands work once the site is
            (re)started with these variables; treat the output like a password. The connection string is left out unless
            --include-connection. Set them only for the site (a subshell or a launch profile): every .NET process started with
            DOTNET_STARTUP_HOOKS loads the agent's hook, and another ASP.NET Core app started that way gets the agent too.
            launchSettings.json is often committed: keep the token (and any connection string) out of git.
            Example: (eval "$(opticli env)" && dotnet run --no-launch-profile)    or: opticli env --format launchSettings
            """);
        command.Options.Add(format);
        command.Options.Add(port);
        command.Options.Add(includeConnection);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var project = context.Project;
            var connection = ServeCommand.RequireServable(context);
            var store = context.StateStore;

            var existing = await AgentProbe.ProbeAsync(store, connection, cancellationToken);
            if (existing.Mode is ServeMode.Background or ServeMode.Foreground && existing.State != AgentState.Stale)
            {
                throw new UsageException($"`opticli serve` runs this site (pid {existing.Pid}); a new token would lock it out.", "Stop it first with `opticli serve --stop`.");
            }

            var settings = UserConfig.ForProject(context.Environment.UserConfigFile, project.Directory);
            var agentDll = AgentLocator.Locate(AppContext.BaseDirectory);
            var selectedPort = PortSelector.Select(parse.GetValue(port), settings?.Port, PortSelector.IsFree);
            var token = SiteEnvironment.NewToken();
            var chosen = parse.GetValue(format)!;
            var pinned = parse.GetValue(includeConnection) ? connection : null;
            var variables = SiteEnvironment.Build(agentDll, token, selectedPort, context.ConnectionRequest.Name, pinned, includeUrls: chosen != EnvFormat.LaunchSettings, approvedRemote: connection);

            store.Write(new ServeState(ServeMode.External, store.ProjectDirectory, selectedPort, token, DateTimeOffset.UtcNow, store.LogPath,
                connection.Server, connection.Database, AgentDll: agentDll));

            if (chosen == EnvFormat.Json)
            {
                return new CommandResult(new
                {
                    variables = variables.ToDictionary(v => v.Key, v => v.Value),
                    url = SiteEnvironment.Url(selectedPort),
                    statePath = store.StatePath,
                    connectionPinned = pinned is not null,
                }, Warnings: connection.IsLocal ? null : [ServeCommand.SharedDatabaseWarning(connection)]);
            }

            var comments = new List<string>
            {
                $"opticli env for {project.ProjectFile}: start the site with these, then write commands reach it at {SiteEnvironment.Url(selectedPort)}.",
                "The token is new; restart the site after running opticli env again.",
            };
            if (!connection.IsLocal)
            {
                comments.Add(ServeCommand.SharedDatabaseWarning(connection));
            }
            if (pinned is null)
            {
                comments.Add($"{AgentProtocol.DatabaseVariable} omitted: the site uses its own connection string (must be '{connection.Database}' on '{connection.Server}'); --include-connection pins it.");
            }
            return new CommandResult(null, Raw: EnvFormat.Render(chosen, variables, selectedPort, comments));
        });
        return command;
    }
}
