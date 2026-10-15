using System.CommandLine;
using System.Text.RegularExpressions;
using OptiCli.Cli;
using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;
using OptiCli.Core.Safety;

namespace OptiCli.Commands;

/// <summary>
/// Lists the project's connection strings, records which one is its development database, adds databases by name and
/// gives databases their role (<see cref="DatabaseRole"/>).
/// </summary>
internal static partial class DbCommand
{
    /// <summary><c>details.reason</c> of a role lowered away from a terminal.</summary>
    public const string LoweringReason = "roleLowered";

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

    /// <param name="Replaced">An entry of that name was there and is replaced.</param>
    /// <param name="Previous">The role its server and database had been given before; omitted when none.</param>
    private sealed record AddResult(DatabaseChoice Database, bool Replaced, DatabaseRole? Previous, string? Note);

    /// <param name="RoleKept">The role of its server and database, which stays (<c>db role</c> changes it).</param>
    private sealed record RemoveResult(string Removed, DatabaseRole? RoleKept);

    private sealed record RoleResult(DatabaseChoice Database, DatabaseRole? Previous, bool Changed, string? Note);

    /// <summary>What a person or agent should know about a database given the role prod.</summary>
    public const string ProdNote =
        "Production: opticli only reads it, with --db, and with ApplicationIntent=ReadOnly unless the connection string sets the intent. "
        + "It is never the development database; writes, `serve` and `env` against it are refused (exit 3). Save only a read-only login "
        + "for it: `opticli doctor` warns when the login can write.";

    public static Command Create(GlobalOptions options)
    {
        var db = new Command("db", """
            Which database opticli uses: list the project's connection strings, choose the development database, add other
            environments' databases by name and give databases a role (local, dev, test, prod).
            By default opticli uses the project's development database: the one chosen with `db use`, else the local database
            the site's Development configuration uses. It never picks a remote database by itself: a command fails with
            needs_selection (exit 6) until one is chosen. --db <id|name> uses another one for a single run.
            A role belongs to a server and database, whichever entry or setting names it. A prod database is only read: never the
            development database, and writes, serve and env against it are refused (exit 3, details.reason prodDatabase).
            """);
        db.Subcommands.Add(List(options));
        db.Subcommands.Add(Use(options));
        db.Subcommands.Add(Forget(options));
        db.Subcommands.Add(Add(options));
        db.Subcommands.Add(Remove(options));
        db.Subcommands.Add(Role(options));
        return db;
    }

    private static Command List(GlobalOptions options)
    {
        var command = new Command("list", """
            List every connection string the project has (launch profiles, exported ConnectionStrings__<Name>, user secrets,
            every appsettings file, the databases added with `db add`) with an id, name, server, database, whether it is local,
            its role and where it was found, and which one is the development database. Passwords are never shown.
            Example: opticli db list
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
            Description = "The id from `opticli db list` (or a needs_selection error), or the name of a database added with `db add`. Leave it out at a terminal to pick from a list.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var command = new Command("use", """
            Save which of the project's connection strings is its development database (in the opticli user config).
            Only run it with the id the user chose. The choice holds until that setting changes: if the server or database
            at that source changes, opticli asks again. At a terminal, without an id, it shows the list and asks.
            A prod database is refused (exit 3). Example: opticli db use a71c3f
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

            var candidate = selectable.FirstOrDefault(c => string.Equals(c.Id, wanted.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? selectable.FirstOrDefault(c => string.Equals(c.Name, wanted.Trim(), StringComparison.OrdinalIgnoreCase));
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

    private static Command Add(GlobalOptions options)
    {
        var name = new Argument<string>("name") { Description = "What --db calls it: letters, digits, '.', '_' and '-' (not 6 hex digits, which look like an id)." };
        var role = new Option<string>("--role")
        {
            Description = $"Its role: {DatabaseRoles.Syntax}. It belongs to the server and database, for every entry and setting that names them.",
            HelpName = "role",
            Required = true,
        };
        var command = new Command("add", """
            Add another environment's database by name (in the opticli user config, for this project), with its role, so
            --db <name> reads it. Takes the connection string from --connection; without it, at a terminal, it asks (the
            password without showing it). Elsewhere --connection is needed, and puts the password in the command line and the
            shell history: let the user run it themselves. For prod, save a read-only login. A name that exists is replaced.
            Giving a server and database a weaker role than they have needs a terminal (exit 3 elsewhere).
            Example: opticli db add prod --role prod
            """);
        command.Arguments.Add(name);
        command.Options.Add(role);
        CommandRunner.SetHandler(command, options, (context, _) =>
        {
            var project = context.Project;
            var entry = context.Parse.GetValue(name)!.Trim();
            if (!EntryName().IsMatch(entry) || HexId().IsMatch(entry))
            {
                throw new UsageException($"'{entry}' can't be a database's name.", "Use letters, digits, '.', '_' and '-', starting with a letter or digit, and not 6 hex digits (those are ids).");
            }
            var wanted = DatabaseRoles.Parse(context.Parse.GetValue(role)!, "--role");
            var connection = context.Parse.GetValue(options.Connection) ?? AskConnection(entry);
            var verdict = ConnectionSafety.Check(connection);
            if (!verdict.IsValid || string.IsNullOrWhiteSpace(verdict.Server))
            {
                throw new UsageException($"That is not a usable SQL Server connection string: {verdict.Reason ?? "it names no server."}");
            }
            var key = DatabaseKey.Of(verdict.Server, verdict.Database)!;
            RequireRoleFits(wanted, verdict.IsLocal);

            var file = context.Environment.UserConfigFile;
            var replaced = UserConfig.ForProject(file, project.Directory)?.Databases?.Any(d => string.Equals(d.Name, entry, StringComparison.OrdinalIgnoreCase)) == true;
            var previous = SetRole(context, key, wanted, "add");
            UserConfig.SaveNamedDatabase(file, project.Directory, new NamedDatabase(entry, connection));

            var choice = Resolved(context).Choices().First(c => string.Equals(c.Name, entry, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new CommandResult(new AddResult(choice, replaced, previous, Note(wanted)), Source: CommandResult.CliSource));
        });
        return command;
    }

    private static Command Remove(GlobalOptions options)
    {
        var name = new Argument<string>("name") { Description = "The name it was added with." };
        var command = new Command("remove", """
            Remove a database added with `db add`. The role of its server and database stays: another entry or setting that
            names them keeps it (`db role` changes it). Example: opticli db remove staging
            """);
        command.Arguments.Add(name);
        CommandRunner.SetHandler(command, options, (context, _) =>
        {
            var wanted = context.Parse.GetValue(name)!.Trim();
            var named = context.ResolveConnection().Candidates.FirstOrDefault(c => c.Source == ConnectionSource.Named && string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (!UserConfig.RemoveNamedDatabase(context.Environment.UserConfigFile, context.Project.Directory, wanted))
            {
                throw new NotFoundException($"This project has no database named '{wanted}'.", "`opticli db list` shows the names.");
            }
            return Task.FromResult(new CommandResult(new RemoveResult(named?.Name ?? wanted, named?.Role), Source: CommandResult.CliSource));
        });
        return command;
    }

    private static Command Role(GlobalOptions options)
    {
        var target = new Argument<string>("id|name") { Description = "The database: its id or name from `opticli db list`, or its database name when that is one database." };
        var role = new Argument<string>("role") { Description = $"Its role: {DatabaseRoles.Syntax}." };
        var command = new Command("role", """
            Give a database its role: local, dev, test or prod. The role belongs to its server and database, so every
            connection string that names them has it, in this project and every other. local is only for a database on this
            machine (the default there); a remote one has no role until it is given one. A weaker role than the one it has
            needs a terminal, where the user confirms by typing the database's name (refused, exit 3, elsewhere).
            Example: opticli db role e02d9b prod
            """);
        command.Arguments.Add(target);
        command.Arguments.Add(role);
        CommandRunner.SetHandler(command, options, (context, _) =>
        {
            var project = context.TryGetProject(out var _);
            var wanted = DatabaseRoles.Parse(context.Parse.GetValue(role)!, "Role");
            var candidate = ConnectionResolver.Find(context.ResolveConnection(), context.Parse.GetValue(target)!.Trim(), project?.Directory);
            RequireRoleFits(wanted, candidate.IsLocal == true);
            var previous = SetRole(context, candidate.Target!, wanted, "role");

            var choice = Resolved(context).Choices().First(c => c.Id == candidate.Id);
            return Task.FromResult(new CommandResult(new RoleResult(choice, previous, previous != wanted, Note(wanted)), Source: CommandResult.CliSource));
        });
        return command;
    }

    /// <exception cref="UsageException"><c>local</c> for a remote database.</exception>
    private static void RequireRoleFits(DatabaseRole role, bool local)
    {
        if (role == DatabaseRole.Local && !local)
        {
            throw new UsageException("The role local is for a database on this machine.", "Give a remote database dev, test or prod.");
        }
    }

    /// <summary>
    /// Gives the server and database the role, unless they have it. A weaker role than the one given before needs a person
    /// at a terminal who types the database's name, so an agent can't make a production database writable.
    /// </summary>
    /// <returns>The role they had been given; null when none.</returns>
    /// <exception cref="RefusedException">A weaker role away from a terminal.</exception>
    private static DatabaseRole? SetRole(CliContext context, DatabaseKey key, DatabaseRole role, string via)
    {
        var file = context.Environment.UserConfigFile;
        var previous = new RoleTable(UserConfig.Roles(file)).Assigned(key)?.Role;
        if (previous == role)
        {
            return previous;
        }
        if (DatabaseRoles.Lowers(previous, role))
        {
            var what = $"'{key.Database}' on '{key.Server}' has the role {DatabaseRoles.Name(previous!.Value)}";
            if (!DatabasePrompt.CanAsk)
            {
                throw new RefusedException(
                    $"Refusing to give a weaker role: {what}, and giving it {DatabaseRoles.Name(role)} needs a person at a terminal.",
                    "Only the user lowers a database's role, by running this command in their own terminal. An agent tells the user and doesn't look for another way in.")
                {
                    Details = new { reason = LoweringReason, server = key.Server, database = key.Database, role = previous, wanted = role },
                };
            }
            var confirm = string.IsNullOrEmpty(key.Database) ? key.Server : key.Database;
            Console.Error.WriteLine($"{what}. Giving it {DatabaseRoles.Name(role)} lets opticli treat it as {(role == DatabaseRole.Local ? "a local database" : $"a {DatabaseRoles.Name(role)} database")}{(previous == DatabaseRole.Prod ? ", and write to it" : "")}.");
            Console.Error.Write($"Type '{confirm}' to confirm (Enter cancels): ");
            if (!string.Equals(Console.ReadLine()?.Trim(), confirm, StringComparison.OrdinalIgnoreCase))
            {
                throw new UsageException("Not confirmed; the role is unchanged.");
            }
        }
        UserConfig.SaveRole(file, new RoleEntry(key, role, DateTimeOffset.UtcNow, via));
        return previous;
    }

    /// <summary>Asks for a connection string at a terminal, the password without showing it.</summary>
    /// <exception cref="UsageException">Not at a terminal, or nothing given.</exception>
    private static string AskConnection(string name)
    {
        if (!DatabasePrompt.CanAsk)
        {
            throw new UsageException(
                "Pass the connection string with --connection, or run this at a terminal to be asked for it.",
                "--connection puts the password in the command line and the shell history; the user can run `opticli db add` at their own terminal instead, which asks without showing the password.");
        }
        Console.Error.Write($"Connection string for '{name}' (leave the password out): ");
        var text = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new UsageException("No connection string given; nothing was added.");
        }
        if (ConnectionSafety.LacksPassword(text) && HiddenInput.Read("Password (not shown; Enter for none): ") is { Length: > 0 } password)
        {
            return ConnectionSafety.WithPassword(text, password);
        }
        return text;
    }

    private static string? Note(DatabaseRole role) => role == DatabaseRole.Prod ? ProdNote : null;

    /// <summary>A fresh resolution after a change to the user config.</summary>
    private static ConnectionResolution Resolved(CliContext context) =>
        ConnectionResolver.Resolve(context.ConnectionRequest, context.TryGetProject(out _), context.Environment);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex EntryName();

    [GeneratedRegex("^[0-9A-Fa-f]{6}$")]
    private static partial Regex HexId();

    /// <summary>Re-resolves after saving, so the result shows what opticli will use from now on.</summary>
    private static CommandResult Saved(CliContext context)
    {
        var resolution = ConnectionResolver.Resolve(context.ConnectionRequest, context.Project, context.Environment);
        var development = resolution.Choices().First(c => c.Development == true);
        var note = development.Local ? null : RemoteNote;
        return new CommandResult(new UseResult(development, resolution.Saved!, note), Source: CommandResult.CliSource);
    }
}
