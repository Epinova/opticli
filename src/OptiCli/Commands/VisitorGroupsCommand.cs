using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Queries;

namespace OptiCli.Commands;

internal static class VisitorGroupsCommand
{
    public static Command Create(GlobalOptions options)
    {
        var list = new ListOptions(options);
        var command = new Command("visitor-groups", """
            List the site's visitor groups (personalization): id (what ContentAreas and rich text store, shown by `get` as
            visitorGroups next to visitorGroupNames, and what `set` takes), name, match (how the criteria combine: all, any,
            or points, with pointsThreshold), securityRole (usable in access rights) and statistics. Their criteria and notes
            aren't read: they can name people. Read from the database; nothing needs to run.
            Example: opticli visitor-groups
            """);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            var groups = await new VisitorGroupReader(db).ListAsync(cancellationToken);
            return CommandResult.From(list.Apply(context.Parse, groups));
        });
        return command;
    }
}
