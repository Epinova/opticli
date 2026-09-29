using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;

namespace OptiCli.Commands;

/// <summary>Lists the project's connection strings and records which one is its development database.</summary>
internal static class DbCommand
{
    /// <summary>What a person or agent should know once a remote database is the development database.</summary>
    public const string RemoteNote =
        "This is a remote database, probably shared with others: reads and writes reach it directly. `opticli serve` runs "
        + "the site against it with the scheduler, automatic schema updates and content type sync turned off.";

    private sealed record ListResult(
        DatabaseChoice? Development,
        SelectionMode? Mode,
        SavedView? Saved,
        IReadOnlyList<DatabaseChoice> Choices,
        ProblemView? Problem);

    private sealed record SavedView(string Id, string? Server, string? Database, string From, DateTimeOffset ChosenAt, string ChosenVia, bool StillMatches);

    private sealed record ProblemView(string Code, string Message, string? Hint);

    private sealed record UseResult(DatabaseChoice Development, SavedDatabase Saved, string? Note);

    public static Command Create(GlobalOptions options)
    {
        var db = new Command("db", """
            Which database opticli uses: list the project's connection strings, choose the development database.
            By default opticli uses the project's development database: the one chosen with `db use`, else the local database
            the site's Development configuration uses. It never picks a remote database by itself: a command fails with
            needs_selection (exit 6) until one is chosen. --db <id> uses another one for a single run.
            """);
        db.Subcommands.Add(List(options));
        db.Subcommands.Add(Use(options));
        db.Subcommands.Add(Forget(options));
        return db;
    }

    private static Command List(GlobalOptions options)
    {
        var command = new Command("list", """
            List every connection string the project has (launch profiles, user secrets, every appsettings file) with an id,
            server, database, whether it is local and where it was found, and which one is the development database.
            Passwords are never shown. Example: opticli db list
            """);
        CommandRunner.SetHandler(command, options, (context, _) =>
        {
            if (context.TryGetProject(out var projectError) is null)
            {
                throw projectError!;
            }
            var resolution = context.ResolveConnection();
            var choices = resolution.Choices();
            var saved = resolution.Saved is { } s
                ? new SavedView(s.Id, s.Server, s.Database, resolution.Describe(s), s.ChosenAt, s.ChosenVia, resolution.DevelopmentMode == SelectionMode.Saved)
                : null;
            var problem = resolution.Failure is { } failure ? new ProblemView(ExitCodes.Name(failure.Code), failure.Message, failure.Hint) : null;
            var result = new ListResult(choices.FirstOrDefault(c => c.Development == true), resolution.DevelopmentMode, saved, choices, problem);
            return Task.FromResult(new CommandResult(result, Source: CommandResult.CliSource));
        });
        return command;
    }

    private static Command Use(GlobalOptions options)
    {
        var id = new Argument<string?>("id")
        {
            Description = "The id from `opticli db list` (or a needs_selection error). Leave it out at a terminal to pick from a list.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var command = new Command("use", """
            Save which of the project's connection strings is its development database (in the opticli user config).
            Only run it with the id the user chose. The choice holds until that setting changes: if the server or database
            at that source changes, opticli asks again. At a terminal, without an id, it shows the list and asks.
            Example: opticli db use a71c3f
            """);
        command.Arguments.Add(id);
        CommandRunner.SetHandler(command, options, (context, _) =>
        {
            var resolution = context.ResolveConnection();
            var selectable = resolution.Selectable;
            var wanted = context.Parse.GetValue(id);
            if (wanted is null)
            {
                if (!DatabasePrompt.CanAsk)
                {
                    throw new UsageException("Pass the id of the database the user chose.", "`opticli db list` shows the ids.");
                }
                if (!DatabasePrompt.AskAndSave(context, null))
                {
                    throw new UsageException(selectable.Count == 0 ? "This project has no connection strings to choose from." : "Nothing was chosen.");
                }
                return Task.FromResult(Saved(context));
            }

            var candidate = selectable.FirstOrDefault(c => string.Equals(c.Id, wanted.Trim(), StringComparison.OrdinalIgnoreCase));
            if (candidate is null)
            {
                var choices = resolution.Choices();
                var hint = int.TryParse(wanted, out var n) && n >= 1 && n <= choices.Count
                    ? $"Pass the id, not the number: {choices[n - 1].Id} is number {n} now. Ids change when the configuration does, so a stale list can't choose the wrong database."
                    : "`opticli db list` shows the ids.";
                throw new NotFoundException($"'{wanted}' is not the id of one of this project's connection strings.", hint)
                {
                    Details = new SelectionDetails(choices, null),
                };
            }

            DatabasePrompt.Save(context, candidate, SavedDatabase.ViaCommand);
            return Task.FromResult(Saved(context));
        });
        return command;
    }

    private static Command Forget(GlobalOptions options)
    {
        var command = new Command("forget", """
            Remove the saved development database, so opticli goes back to the local database of the site's Development
            configuration (or asks again). Example: opticli db forget
            """);
        CommandRunner.SetHandler(command, options, (context, _) =>
        {
            var had = context.ResolveConnection().Saved is not null;
            UserConfig.SaveDatabase(context.Environment.UserConfigFile, context.Project.Directory, null);
            return Task.FromResult(new CommandResult(new { forgotten = had }, Source: CommandResult.CliSource));
        });
        return command;
    }

    /// <summary>Re-resolves after saving, so the result shows what opticli will use from now on.</summary>
    private static CommandResult Saved(CliContext context)
    {
        var resolution = ConnectionResolver.Resolve(context.ConnectionRequest, context.Project, context.Environment);
        var development = resolution.Choices().First(c => c.Development == true);
        var note = development.Local ? null : RemoteNote;
        return new CommandResult(new UseResult(development, resolution.Saved!, note), Source: CommandResult.CliSource);
    }
}
