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
            Unlike `serve`, it leaves the site's scheduler as the site sets it (OPTICLI_SCHEDULER=on): that run is yours.
            Example: (eval "$(opticli env)" && dotnet run --no-launch-profile)    or: opticli env --format launchSettings
            """);
        command.Options.Add(format);
        command.Options.Add(port);
        command.Options.Add(includeConnection);

        // --json is the envelope, like everywhere else; it only conflicts with another format asked for by name.
        CommandRunner.SetHandler(command, options, new ResultModes()
            .Check(context =>
            {
                if (context.Parse.GetValue(options.Json) && context.Parse.GetResult(format) is { Implicit: false } && context.Parse.GetValue(format) != EnvFormat.Json)
                {
                    throw new UsageException($"--json and --format {context.Parse.GetValue(format)} ask for different output; use one.");
                }
            })
            .When(ModeCondition.AnyOf(ModeCondition.Set(options.Json), ModeCondition.Is(format, EnvFormat.Json)), async (context, cancellationToken) =>
            {
                var env = await PrepareAsync(context, EnvFormat.Json, port, includeConnection, cancellationToken);
                return new CommandResult<EnvOutput>(
                    new EnvOutput(env.Variables.ToDictionary(v => v.Key, v => v.Value), SiteEnvironment.Url(env.Port), env.StatePath, env.Pinned is not null),
                    Warnings: env.Warnings.Count > 0 ? env.Warnings : null);
            })
            .Otherwise(async (context, cancellationToken) =>
            {
                var chosen = context.Parse.GetValue(format)!;
                var env = await PrepareAsync(context, chosen, port, includeConnection, cancellationToken);
                var comments = new List<string>
                {
                    $"opticli env for {context.Project.ProjectFile}: start the site with these, then write commands reach it at {SiteEnvironment.Url(env.Port)}.",
                    "The token is new; restart the site after running opticli env again.",
                };
                comments.AddRange(env.Warnings);
                if (env.Pinned is null)
                {
                    comments.Add($"{AgentProtocol.DatabaseVariable} omitted: the site uses its own connection string (must be '{env.Connection.Database}' on '{env.Connection.Server}'); --include-connection pins it.");
                }
                return new CommandResult<RawOutput>(RawOutput.Instance, Raw: EnvFormat.Render(chosen, env.Variables, env.Port, comments));
            }));
        return command;
    }

    /// <summary><c>env --json</c>: what the other formats print as text.</summary>
    /// <param name="Variables">The variables to start the site with (a blank value unsets one an earlier <c>opticli env</c> exported).</param>
    /// <param name="Url">Where write commands reach the site once it runs with them.</param>
    /// <param name="StatePath">The state file that now holds the new token.</param>
    /// <param name="ConnectionPinned">The connection string is among the variables (<c>--include-connection</c>).</param>
    private sealed record EnvOutput(IReadOnlyDictionary<string, string> Variables, string Url, string StatePath, bool ConnectionPinned);

    /// <param name="Pinned">The connection string the variables pin; null when the site uses its own.</param>
    private sealed record Prepared(IReadOnlyList<KeyValuePair<string, string>> Variables, int Port, string StatePath, Core.Safety.VerifiedConnectionString Connection,
        Core.Safety.VerifiedConnectionString? Pinned, IReadOnlyList<string> Warnings);

    /// <summary>Checks the site may be started this way, registers a fresh token, and builds the variables in <paramref name="chosen"/>'s form.</summary>
    private static async Task<Prepared> PrepareAsync(CliContext context, string chosen, Option<int?> port, Option<bool> includeConnection, CancellationToken cancellationToken)
    {
        var parse = context.Parse;
        var project = context.Project;
        if (IdeProject.Read(IdeProject.UserFile(project)) is not null)
        {
            // The build loads the agent already; these variables would give it a token the IDE's runs don't have.
            throw new UsageException(
                "IDE mode is on for this project (`opticli ide on`): start the site from your IDE as it is, without these variables.",
                "`opticli ide off` turns IDE mode off, if you want to use `opticli env` instead.");
        }
        var connection = ServeCommand.RequireServable(context);
        var store = context.StateStore;

        // As for `serve`: no other start may decide nothing runs while this one writes its token.
        using var startLock = await store.LockAsync(TimeSpan.FromMinutes(10), cancellationToken);
        var existing = await AgentProbe.ProbeAsync(store, connection, cancellationToken);
        if (existing.Mode is ServeMode.Background or ServeMode.Foreground && existing.State != AgentState.Stale)
        {
            throw new UsageException($"`opticli serve` runs this site (pid {existing.Pid}); a new token would lock it out.", "Stop it first with `opticli serve --stop`.");
        }

        var settings = UserConfig.ForProject(context.Environment.UserConfigFile, project.Directory);
        var (_, agentDll) = await ServeCommand.SelectAgentAsync(context, connection, cancellationToken);
        var selectedPort = PortSelector.Select(parse.GetValue(port), settings?.Port, PortSelector.IsFree);
        var token = SiteEnvironment.NewToken();
        var pinned = parse.GetValue(includeConnection) ? connection : null;
        var siteEndpoints = KestrelEndpoints.Configured(project, context.Environment);
        var built = SiteEnvironment.Build(agentDll, token, selectedPort, context.ConnectionRequest.Name, pinned,
            includeUrls: chosen != EnvFormat.LaunchSettings, approvedRemote: connection, kestrelEndpoints: siteEndpoints.Count > 0, scheduler: true);
        // Blank what this run leaves out but an earlier `opticli env` exported in this shell, as `serve` does for its
        // site: a stale OPTICLI_DB would still pin the site, a stale OPTICLI_REMOTE_DB still approve that database.
        var stale = SiteEnvironment.NotSet(built).Where(name => context.Environment.Variable(name) is not null).ToList();
        // --profile: that launch profile's variables too, for `dotnet run --no-launch-profile` (a launchSettings profile has them already).
        var profile = context.ConnectionRequest.Profile is { } profileName && chosen != EnvFormat.LaunchSettings
            ? LaunchProfile.Read(project.Directory, profileName, context.ConnectionRequest.Name)
            : null;
        var own = built.Select(v => v.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<KeyValuePair<string, string>> variables =
            [.. (profile?.Applied ?? []).Where(v => !own.Contains(v.Key)), .. built, .. stale.Select(name => KeyValuePair.Create(name, ""))];

        store.Write(new ServeState(ServeMode.External, store.ProjectDirectory, selectedPort, token, DateTimeOffset.UtcNow, store.LogPath,
            connection.Server, connection.Database, AgentDll: agentDll));
        startLock.Dispose();

        var warnings = new List<string>();
        if (profile is not null)
        {
            warnings.Add(profile.Describe());
        }
        if (!connection.IsLocal)
        {
            warnings.Add(ServeCommand.SharedDatabaseWarning(connection));
            warnings.Add("A site you start yourself isn't checked for EF Core migrations the shared database lacks, or for a CMS schema version it can't start with: `opticli serve` checks those first. The site agent still reports content type drift (`opticli drift`), and writes stop on it.");
        }
        if (siteEndpoints.Count > 0)
        {
            warnings.Add(KestrelEndpoints.Warning(siteEndpoints));
        }
        // serve removes these from the site's environment; env can only say so. ASP.NET Core reads them in no set order.
        var competing = SiteEnvironment.CompetingConnectionVariables(context.ConnectionRequest.Name, context.Environment.Variables.Select(v => v.Key));
        if (competing.Count > 0)
        {
            warnings.Add($"{string.Join(", ", competing)} {(competing.Count == 1 ? "is" : "are")} exported in this shell, and the site reads {(competing.Count == 1 ? "it" : "them")} as its {context.ConnectionRequest.Name} connection string, possibly over "
                + (pinned is null ? "its own configuration" : ConnectionVariables.Name(context.ConnectionRequest.Name))
                + $": unset {(competing.Count == 1 ? "it" : "them")} before starting the site (or use `opticli serve`, which does).");
        }
        if (stale.Count > 0)
        {
            warnings.Add($"{string.Join(", ", stale)} {(stale.Count == 1 ? "is" : "are")} set in this shell (an earlier `opticli env`?) and blanked here; run the site from a subshell so they don't linger.");
        }

        return new Prepared(variables, selectedPort, store.StatePath, connection, pinned, warnings);
    }
}
