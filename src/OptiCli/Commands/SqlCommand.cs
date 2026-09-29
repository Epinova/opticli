using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Sql;

namespace OptiCli.Commands;

internal static class SqlCommand
{
    public static Command Create(GlobalOptions options)
    {
        var query = new Argument<string>("query") { Description = "One SELECT (or WITH ... SELECT) statement." };
        var limit = new Option<int>("--limit") { Description = "Maximum rows returned (truncated: true when there were more).", DefaultValueFactory = _ => 100, HelpName = "n" };
        var full = new Option<bool>("--full") { Description = "Do not cut long strings and binary values." };
        var personal = new Option<bool>("--include-personal-data")
        {
            Description = $"Allow reading tables with personal data ({PersonalDataTables.Description}).",
        };
        var command = new Command("sql", """
            Run one read-only SELECT against the site's database, for questions the other commands don't answer.
            Only a single SELECT/WITH statement is accepted (no writes, EXEC, other databases or server catalog views);
            personal-data tables need --include-personal-data; it always runs in a transaction that is rolled back.
            Example: opticli sql "SELECT TOP 5 pkID, Name FROM tblContentType ORDER BY pkID DESC"
            """);
        command.Arguments.Add(query);
        command.Options.Add(limit);
        command.Options.Add(full);
        command.Options.Add(personal);
        options.AddJsonLines(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var max = context.Parse.GetValue(limit);
            if (max < 1)
            {
                throw new Core.Errors.UsageException("--limit must be at least 1.");
            }
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            var result = await RawQuery.RunAsync(
                db, context.Parse.GetValue(query)!, max, context.Parse.GetValue(personal), context.Parse.GetValue(full), cancellationToken);
            return new CommandResult(
                result,
                Warnings: result.Truncated == true ? [$"The query returned more than {max} rows; only the first {max} are shown (raise --limit)."] : null,
                Lines: result.Rows);
        });
        return command;
    }
}
